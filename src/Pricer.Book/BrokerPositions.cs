using System.Collections.Immutable;
using System.Globalization;
using Pricer.Instruments;
using Pricer.Market;

namespace Pricer.Book;

/// <summary>A position as the broker reports it. Structured notes are OTC and never appear here.</summary>
public sealed record BrokerPosition(
    long ContractId, string Symbol, string SecType, string Right, double Strike, string Expiry, double Multiplier, double Quantity);

public interface IPositionProvider
{
    string Name { get; }
    Task<IReadOnlyList<BrokerPosition>> FetchAsync(CancellationToken token);
}

public sealed record Reconciliation(
    ImmutableArray<Position> Positions, int Added, int Changed, int Closed, ImmutableArray<string> Skipped);

public static class BrokerImport
{
    public const string Source = "IBKR";
    private const string DateFormat = "yyyyMMdd";

    /// <summary>Replaces the imported side of the book by contract id. Local trades are never touched.</summary>
    /// <param name="exercise">Exercise style by symbol: the position report does not give it.</param>
    public static Reconciliation Merge(
        IReadOnlyList<Position> book, IReadOnlyList<BrokerPosition> broker, Func<string, ExerciseStyle> exercise)
    {
        var skipped = new List<string>();
        var imported = new List<Position>();
        foreach (var position in broker.Where(p => p.Quantity != 0))
        {
            if (Convert(position, exercise) is { } converted) imported.Add(converted);
            else skipped.Add($"{position.Symbol} ({position.SecType})");
        }

        var known = book.Where(p => p.Source == Source).ToDictionary(p => p.Id);
        var kept = imported.Select(p => known.TryGetValue(p.Id, out var old) ? p with { Tag = old.Tag } : p).ToList();
        int added = kept.Count(p => !known.ContainsKey(p.Id));
        int changed = kept.Count(p => known.TryGetValue(p.Id, out var old) && old.Quantity != p.Quantity);
        int closed = known.Keys.Count(id => kept.All(p => p.Id != id));

        return new Reconciliation([.. book.Where(p => p.Source != Source), .. kept], added, changed, closed, [.. skipped]);
    }

    private static Position? Convert(BrokerPosition p, Func<string, ExerciseStyle> exercise)
    {
        string id = $"{Source}-{p.ContractId}";
        if (p.SecType == "STK")
        {
            return new Position(id, new Stock { Underlying = p.Symbol }, p.Quantity, Source);
        }
        if (p.SecType != "OPT" || !DateOnly.TryParseExact(p.Expiry, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
        {
            return null;
        }

        var right = p.Right is "C" or "CALL" ? OptionRight.Call : OptionRight.Put;
        Instrument option = exercise(p.Symbol) == ExerciseStyle.European
            ? new EuropeanOption { Underlying = p.Symbol, Right = right, Strike = p.Strike, Expiry = expiry }
            : new AmericanOption { Underlying = p.Symbol, Right = right, Strike = p.Strike, Expiry = expiry };
        return new Position(id, option, p.Quantity * p.Multiplier, Source);
    }
}

public static class DemoBook
{
    private const string Vanilla = "Vanillas";
    private const string Exotic = "Exotics";
    private const string Note = "Structured";

    /// <summary>A small book on the snapshot's underlyings, to try the screens with no broker position.</summary>
    public static ImmutableArray<Position> For(MarketSnapshot snapshot, string underlying)
    {
        double spot = snapshot.Underlying(underlying).Spot;
        var asOf = snapshot.AsOf;
        var oneYear = asOf.AddYears(1);
        var threeYears = asOf.AddYears(3);
        double Strike(double level) => Math.Round(level * spot);

        var positions = ImmutableArray.CreateBuilder<Position>();
        void Add(Instrument instrument, double quantity, string tag) =>
            positions.Add(new Position($"DEMO-{positions.Count + 1}", instrument, quantity, Tag: tag));

        Add(new Stock { Underlying = underlying }, 100, Vanilla);
        Add(new EuropeanOption { Underlying = underlying, Right = OptionRight.Call, Strike = Strike(1.05), Expiry = oneYear }, -100, Vanilla);
        Add(new EuropeanOption { Underlying = underlying, Right = OptionRight.Put, Strike = Strike(0.9), Expiry = oneYear }, 100, Vanilla);
        Add(new AmericanOption { Underlying = underlying, Right = OptionRight.Put, Strike = Strike(1), Expiry = oneYear }, 50, Vanilla);
        Add(new BarrierOption
        {
            Underlying = underlying, Right = OptionRight.Call, Strike = Strike(1), Expiry = oneYear, Barrier = Strike(0.8), Kind = BarrierKind.DownOut,
        }, 25, Exotic);
        Add(new AsianOption
        {
            Underlying = underlying, Right = OptionRight.Call, Strike = Strike(1), Expiry = oneYear, AveragingStart = asOf.AddMonths(1),
        }, 25, Exotic);
        Add(new ReverseConvertible { Underlying = underlying, InitialFixing = spot, Start = asOf, Expiry = oneYear, Protection = 0.8 }, 10, Note);
        Add(new Autocall { Underlying = underlying, InitialFixing = spot, Start = asOf, Expiry = threeYears }, 10, Note);
        Add(new Phoenix { Underlying = underlying, InitialFixing = spot, Start = asOf, Expiry = threeYears }, 10, Note);
        if (snapshot.Underlyings.Count > 1)
        {
            Add(new WorstOfPhoenix
            {
                Underlying = underlying, InitialFixing = spot, Start = asOf, Expiry = threeYears,
                Basket = [.. snapshot.Underlyings.Keys.OrderBy(n => n != underlying).Select(n => new BasketLeg(n, snapshot.Underlying(n).Spot))],
            }, 10, Note);
        }
        return positions.ToImmutable();
    }
}
