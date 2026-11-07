using System.Collections.Immutable;
using System.Globalization;
using Pricer.Core;

namespace Pricer.Book;

/// <param name="Causes">Each input that differs, by name, with both values.</param>
public sealed record TradeDifference(string Id, double? Left, double? Right, double Change, ImmutableArray<string> Causes);

public sealed record RunComparison(
    double LeftPv, double RightPv, ImmutableArray<string> ConfigChanges, ImmutableArray<TradeDifference> Trades);

/// <summary>Diff of two runs down to the parameter and the trade. Trades with neither a change of value
/// nor a change of input are left out.</summary>
public static class RunComparer
{
    public static RunComparison Compare(RunResult left, RunResult right)
    {
        var a = left.Trades.ToDictionary(t => t.Position.Id);
        var b = right.Trades.ToDictionary(t => t.Position.Id);

        var trades = new List<TradeDifference>();
        foreach (string id in a.Keys.Union(b.Keys).Order())
        {
            a.TryGetValue(id, out var l);
            b.TryGetValue(id, out var r);
            var causes = Causes(l, r);
            double? lv = l?.Result?.Pv;
            double? rv = r?.Result?.Pv;
            double change = (rv ?? 0) - (lv ?? 0);
            if (change != 0 || causes.Length > 0) trades.Add(new TradeDifference(id, lv, rv, change, causes));
        }

        return new RunComparison(
            Total(left), Total(right), ConfigChanges(left, right), [.. trades.OrderByDescending(t => Math.Abs(t.Change))]);
    }

    private static double Total(RunResult run) => run.Trades.Sum(t => t.Result?.Pv ?? 0);

    private static ImmutableArray<string> ConfigChanges(RunResult left, RunResult right)
    {
        var changes = new List<string>();
        if (left.SnapshotId != right.SnapshotId) changes.Add($"Snapshot: {left.SnapshotId} → {right.SnapshotId}");
        if (left.Valuation != right.Valuation) changes.Add($"Valuation date: {left.Valuation:yyyy-MM-dd} → {right.Valuation:yyyy-MM-dd}");
        if (left.CodeVersion != right.CodeVersion) changes.Add("Different code version");

        var (x, y) = (left.Config, right.Config);
        changes.AddRange(Parameters(x, y));
        changes.AddRange(Parameters(x.Pde, y.Pde));
        changes.AddRange(Parameters(x.MonteCarlo, y.MonteCarlo));
        changes.AddRange(Parameters(x.LocalVol, y.LocalVol));
        changes.AddRange(Parameters(x.Risk, y.Risk));
        return [.. changes];
    }

    private static ImmutableArray<string> Causes(TradeResult? left, TradeResult? right)
    {
        if (left is null) return ["Trade added"];
        if (right is null) return ["Trade removed"];

        var causes = new List<string>();
        var (p, q) = (left.Position, right.Position);
        if (p.Quantity != q.Quantity) causes.Add($"Quantity: {Text(p.Quantity)} → {Text(q.Quantity)}");
        if (p.Instrument.GetType() != q.Instrument.GetType()) causes.Add("Product type");
        else causes.AddRange(Parameters(p.Instrument, q.Instrument));
        if (!States(p, q)) causes.Add("Lifecycle state");

        if (left.Model is { } lm && right.Model is { } rm)
        {
            if (lm.GetType() != rm.GetType()) causes.Add($"Model: {lm.GetType().Name} → {rm.GetType().Name}");
            else causes.AddRange(Parameters(lm, rm));
        }
        if (left.Engine is { } le && right.Engine is { } re)
        {
            if (le.GetType() != re.GetType()) causes.Add($"Engine: {le.GetType().Name} → {re.GetType().Name}");
            else causes.AddRange(Parameters(le, re));
        }

        foreach (var point in left.Market)
        {
            if (right.Market.FirstOrDefault(m => m.Underlying == point.Underlying) is not { } other) continue;
            if (point.Spot != other.Spot) causes.Add($"Spot {point.Underlying}: {Text(point.Spot)} → {Text(other.Spot)}");
            if (point.Vol != other.Vol) causes.Add($"Vol {point.Underlying}: {Text(point.Vol)} → {Text(other.Vol)}");
            if (point.Rate != other.Rate) causes.Add($"Rate at expiry: {Text(point.Rate)} → {Text(other.Rate)}");
        }

        if ((left.Error is null) != (right.Error is null)) causes.Add($"Error: {left.Error ?? "none"} → {right.Error ?? "none"}");
        return [.. causes.Distinct()];
    }

    private static bool States(Position p, Position q) =>
        ContentHash.Of(new { p.State }) == ContentHash.Of(new { q.State });

    private static IEnumerable<string> Parameters(object left, object right) =>
        ParameterSchema.Diff(left, right).Select(d => $"{d.Parameter.Label}: {Text(d.Left)} → {Text(d.Right)}");

    private static string Text(object? value) => value switch
    {
        double number => number.ToString("G6", CultureInfo.CurrentCulture),
        _ => value?.ToString() ?? "",
    };
}
