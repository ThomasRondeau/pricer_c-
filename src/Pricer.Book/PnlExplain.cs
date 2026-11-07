using System.Collections.Concurrent;
using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Book;

public enum PnlFactor
{
    Time,
    Events,
    Spot,
    Dividends,
    VolLevel,
    VolShape,
    Rates,
    Composition,
    Residual,
}

public enum PnlTimeRoll
{
    /// <summary>Spot and curves move to their forwards: a market that delivers its forwards earns only theta.</summary>
    Forward,

    /// <summary>Spot and curves stay as they are, as in a bumped theta.</summary>
    ConstantSpot,
}

public enum PnlOrdering
{
    SpotVolRates,
    VolSpotRates,
    RatesVolSpot,
}

public sealed record PnlConfig
{
    [Parameter("Time roll", Group = "PnL")]
    public PnlTimeRoll Roll { get; init; } = PnlTimeRoll.Forward;

    /// <summary>The attribution depends on the order; the total does not.</summary>
    [Parameter("Factor order", Group = "PnL")]
    public PnlOrdering Ordering { get; init; } = PnlOrdering.SpotVolRates;

    [Parameter("Split vol level and shape", Group = "PnL")]
    public bool SplitVol { get; init; } = true;

    public ImmutableArray<PnlFactor> Sequence => Ordering switch
    {
        PnlOrdering.VolSpotRates => [PnlFactor.VolLevel, PnlFactor.VolShape, PnlFactor.Spot, PnlFactor.Dividends, PnlFactor.Rates],
        PnlOrdering.RatesVolSpot => [PnlFactor.Rates, PnlFactor.VolLevel, PnlFactor.VolShape, PnlFactor.Spot, PnlFactor.Dividends],
        _ => [PnlFactor.Spot, PnlFactor.Dividends, PnlFactor.VolLevel, PnlFactor.VolShape, PnlFactor.Rates],
    };
}

/// <param name="Waterfall">Full revaluation amounts, indexed by <see cref="PnlFactor"/>.</param>
/// <param name="Greeks">The same split from the sensitivities at t0, whose residual shows what they miss.</param>
/// <param name="Total">PV change plus cash received.</param>
public sealed record TradeExplain(
    string Id, Position Position, double Total, ImmutableArray<double> Waterfall, ImmutableArray<double> Greeks, string? Error);

public sealed record PnlExplain(
    DateOnly From, DateOnly To, PnlConfig Config, ImmutableArray<TradeExplain> Trades, ImmutableArray<string> Warnings);

public sealed record PnlRow(string Key, int Trades, int Failed, double Total, ImmutableArray<double> Waterfall, ImmutableArray<double> Greeks)
{
    public double this[PnlFactor factor] => Waterfall[(int)factor];
}

public static class PnlAggregator
{
    private static readonly int Factors = Enum.GetValues<PnlFactor>().Length;

    public static ImmutableArray<PnlRow> Group(IEnumerable<TradeExplain> trades, BookGrouping grouping) =>
        [.. trades.GroupBy(t => BookAggregator.Key(t.Position, grouping)).OrderBy(g => g.Key).Select(g => Row(g.Key, [.. g]))];

    public static PnlRow Row(string key, IReadOnlyList<TradeExplain> trades)
    {
        var good = trades.Where(t => t.Error is null).ToList();
        double[] Sum(Func<TradeExplain, ImmutableArray<double>> pick)
        {
            double[] total = new double[Factors];
            foreach (var trade in good)
            {
                var values = pick(trade);
                for (int i = 0; i < Factors; i++) total[i] += values[i];
            }
            return total;
        }
        return new PnlRow(key, trades.Count, trades.Count - good.Count, good.Sum(t => t.Total), [.. Sum(t => t.Waterfall)], [.. Sum(t => t.Greeks)]);
    }
}

/// <summary>Walks each trade from (t0, M0) to (t1, M1) one factor at a time, in full revaluation, with the
/// same seeds and grids throughout so that no simulation noise lands in a factor.</summary>
public sealed class PnlExplainer(EngineRegistry registry)
{
    private const int MaxConcurrentTrades = 4;
    private const double VolPoint = 0.01;
    private const double RatePoint = 0.01;
    private const double ReferenceMaturity = 1;
    private const double MinPillar = 1e-4;
    private static readonly int Factors = Enum.GetValues<PnlFactor>().Length;

    private sealed record Step(PnlFactor Factor, MarketSnapshot Market, int Fit);

    public async Task<PnlExplain> ExplainAsync(
        RunResult left, MarketSnapshot leftSnapshot, RunResult right, MarketSnapshot rightSnapshot, PnlConfig config,
        IProgress<RunProgress>? progress, CancellationToken token)
    {
        if (right.Valuation < left.Valuation) throw new ArgumentException("The final run precedes the initial run.");

        var start = left.Config.Shocked(leftSnapshot);
        var end = right.Config.Shocked(rightSnapshot);
        var rolled = Roll(start, end.AsOf, config.Roll);
        var steps = Steps(rolled, end, config);

        var finals = right.Trades.ToDictionary(t => t.Position.Id);
        var ids = left.Trades.Select(t => t.Position.Id).ToHashSet();
        TradeResult[] added = [.. right.Trades.Where(t => !ids.Contains(t.Position.Id))];
        var results = new TradeExplain[left.Trades.Length];
        var fits = new ConcurrentDictionary<(string, int), Lazy<HestonConfig>>();
        var risk = new RiskCalculator(registry);
        var engine = new RunEngine(registry, new MemoryRunCache());
        int done = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentTrades, CancellationToken = token };
        await Parallel.ForEachAsync(Enumerable.Range(0, left.Trades.Length), options, (i, _) =>
        {
            var initial = left.Trades[i];
            results[i] = Explain(initial, finals.GetValueOrDefault(initial.Position.Id), left.Config, start, rolled, end, steps, risk, engine, fits);
            progress?.Report(new RunProgress(Interlocked.Increment(ref done), left.Trades.Length));
            return ValueTask.CompletedTask;
        });

        return new PnlExplain(
            start.AsOf, end.AsOf, config, [.. results, .. added.Select(New)], Warnings(left, right));
    }

    private static ImmutableArray<string> Warnings(RunResult left, RunResult right)
    {
        var warnings = new List<string>();
        if (left.CodeVersion != right.CodeVersion) warnings.Add("Different code versions: the model gap goes into the residual.");
        var (a, b) = (left.Config with { SpotShock = 0, VolShock = 0 }, right.Config with { SpotShock = 0, VolShock = 0 });
        if (ContentHash.Of(a) != ContentHash.Of(b)) warnings.Add("Different configurations: the method gap goes into the residual.");
        int failed = left.Trades.Concat(right.Trades).Count(t => t.Error is not null);
        if (failed > 0) warnings.Add($"{failed} failed results in the runs: these trades are not explained.");
        return [.. warnings];
    }

    /// <summary>The market at t1 as t0 expected it. The surface stays a function of strike and time to expiry.</summary>
    public static MarketSnapshot Roll(MarketSnapshot market, DateOnly to, PnlTimeRoll roll)
    {
        double dt = DayCount.Act365F.YearFraction(market.AsOf, to);
        if (roll == PnlTimeRoll.ConstantSpot || dt == 0) return market with { AsOf = to };

        var underlyings = market.Underlyings.ToImmutableSortedDictionary(
            u => u.Key,
            u => u.Value with
            {
                Spot = u.Value.Spot * u.Value.DividendCurve.Discount(dt) / market.DiscountCurve.Discount(dt),
                DividendCurve = Rolled(u.Value.DividendCurve, dt),
            });
        return market with { AsOf = to, DiscountCurve = Rolled(market.DiscountCurve, dt), Underlyings = underlyings };
    }

    private static YieldCurve Rolled(YieldCurve curve, double dt) => curve with
    {
        ZeroRates = [.. curve.Times.Select(t =>
        {
            double tau = Math.Max(t, MinPillar);
            return -Math.Log(curve.Discount(tau + dt) / curve.Discount(dt)) / tau;
        })],
    };

    private static ImmutableArray<Step> Steps(MarketSnapshot rolled, MarketSnapshot end, PnlConfig config)
    {
        var steps = ImmutableArray.CreateBuilder<Step>();
        var current = rolled;
        int fit = 0;
        foreach (var factor in config.Sequence)
        {
            current = Apply(factor, current, end, config);
            // Heston does not read the surface: it follows it from the vol steps on, and only there.
            if (factor is PnlFactor.VolLevel or PnlFactor.VolShape) fit = steps.Count + 1;
            steps.Add(new Step(factor, current, fit));
        }
        return steps.ToImmutable();
    }

    private static MarketSnapshot Apply(PnlFactor factor, MarketSnapshot market, MarketSnapshot end, PnlConfig config)
    {
        if (factor == PnlFactor.Rates) return market with { DiscountCurve = end.DiscountCurve };
        foreach (var (name, current) in market.Underlyings)
        {
            if (!end.Underlyings.TryGetValue(name, out var target)) continue;
            market = market.WithUnderlying(name, factor switch
            {
                PnlFactor.Spot => current with { Spot = target.Spot },
                PnlFactor.Dividends => current with { DividendCurve = target.DividendCurve },
                PnlFactor.VolLevel when config.SplitVol => current with { Vol = current.Vol.Shifted(LevelChange(current, target)) },
                PnlFactor.VolShape => current with { Vol = target.Vol },
                _ => current,
            });
        }
        return market;
    }

    /// <summary>The move of the one-year at-the-money vol, at a fixed strike.</summary>
    private static double LevelChange(UnderlyingMarket from, UnderlyingMarket to) =>
        to.Vol.Vol(to.Spot, ReferenceMaturity) - from.Vol.Vol(to.Spot, ReferenceMaturity);

    private TradeExplain Explain(
        TradeResult initial, TradeResult? final, RunConfig config, MarketSnapshot start, MarketSnapshot rolled, MarketSnapshot end,
        ImmutableArray<Step> steps, RiskCalculator risk, RunEngine engine, ConcurrentDictionary<(string, int), Lazy<HestonConfig>> fits)
    {
        var position = initial.Position;
        double[] zero = new double[Factors];
        if (initial.Result is not { } first) return Failed(position, initial.Error);

        double startValue = Worth(first);
        if (final is null || !SameProduct(position, final.Position))
        {
            // Closed: the exit price is unknown, so the value leaves the book through composition.
            zero[(int)PnlFactor.Composition] = -startValue;
            return new TradeExplain(position.Id, position, -startValue, [.. zero], [.. zero], null);
        }
        if (final.Result is not { } last) return Failed(position, final.Error);

        try
        {
            double Value(Position p, MarketSnapshot market, int fit)
            {
                var model = Model(p, market, config, start, fit, fits);
                var request = new PricingRequest(
                    p.ToTrade(), market, model, engine.ResolveEngine(p.Instrument, model, config), market.AsOf, []);
                return Worth(risk.Compute(request, config.RiskFor(p.Instrument)));
            }

            double[] waterfall = new double[Factors];
            double before = Value(position, start, 0);
            double previous = before;

            double Advance(PnlFactor factor, Position p, MarketSnapshot market, int fit)
            {
                double value = Value(p, market, fit);
                waterfall[(int)factor] += value - previous;
                previous = value;
                return value;
            }

            Advance(PnlFactor.Time, position with { State = Expected(position, start, end.AsOf) }, rolled, 0);
            var settled = position with { State = final.Position.State };
            Advance(PnlFactor.Events, settled, rolled, 0);
            foreach (var step in steps) Advance(step.Factor, settled, step.Market, step.Fit);

            double total = Worth(last) - startValue;
            if (position.Quantity != final.Position.Quantity)
            {
                double scale = final.Position.Quantity / position.Quantity;
                waterfall[(int)PnlFactor.Composition] = previous * (scale - 1);
                previous *= scale;
            }
            waterfall[(int)PnlFactor.Residual] = total - waterfall.Sum();

            return new TradeExplain(position.Id, position, total, [.. waterfall], Greeks(initial, final, start.AsOf, rolled, end, waterfall, total), null);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            return Failed(position, e.Message);
        }
    }

    private static TradeExplain Failed(Position position, string? error) =>
        new(position.Id, position, 0, [.. new double[Factors]], [.. new double[Factors]], error ?? "Failed");

    private static TradeExplain New(TradeResult trade)
    {
        double[] amounts = new double[Factors];
        if (trade.Result is not { } result) return Failed(trade.Position, trade.Error);
        amounts[(int)PnlFactor.Composition] = Worth(result);
        return new TradeExplain(trade.Position.Id, trade.Position, Worth(result), [.. amounts], [.. amounts], null);
    }

    /// <summary>Value plus the cash a trade has paid so far: their change is the PnL, whenever the cash falls.</summary>
    private static double Worth(PricingResult result) => result.Pv + result.Realised;

    private static bool SameProduct(Position a, Position b) => ContentHash.Of(a.Instrument) == ContentHash.Of(b.Instrument);

    private static ModelConfig Model(
        Position position, MarketSnapshot market, RunConfig config, MarketSnapshot start, int fit,
        ConcurrentDictionary<(string, int), Lazy<HestonConfig>> fits)
    {
        var choice = config.ModelFor(position);
        if (choice != ModelChoice.Heston) return choice == ModelChoice.LocalVol ? config.LocalVol : new BlackScholesConfig();

        string underlying = position.Instrument.Underlying;
        var reference = fit == 0 ? start : market;
        return fits.GetOrAdd((underlying, fit), _ => new Lazy<HestonConfig>(() => HestonCalibrator.Fit(reference, underlying).Config)).Value;
    }

    /// <summary>The state at t1 had the fixings between the dates come out at their forwards.</summary>
    private static TradeState? Expected(Position position, MarketSnapshot start, DateOnly to)
    {
        var instrument = position.Instrument;
        if (instrument is not (StructuredNote or AsianOption)) return position.State;

        var known = (position.State?.Fixings ?? []).Select(f => (f.Underlying, f.Date)).ToHashSet();
        var extra = new List<FixingRecord>();
        var dates = PayoffCompiler.Compile(instrument, start.AsOf, 0, 0).Events.Select(e => e.Date).Distinct();
        foreach (var date in dates.Where(d => d >= start.AsOf && d < to))
        {
            double dt = DayCount.Act365F.YearFraction(start.AsOf, date);
            foreach (string name in instrument.Underlyings)
            {
                if (known.Contains((name, date))) continue;
                var market = start.Underlying(name);
                extra.Add(new FixingRecord(name, date, market.Spot * market.DividendCurve.Discount(dt) / start.DiscountCurve.Discount(dt)));
            }
        }
        var state = position.State ?? new TradeState();
        return state with { Fixings = [.. state.Fixings, .. extra] };
    }

    /// <summary>delta dS + gamma dS^2 / 2 + vega dvol + theta dt + rho dr, with the sensitivities of t0. Carry to the
    /// rolled spot sits with time, as in the waterfall. Cash events and composition have no greek and are copied.</summary>
    private static ImmutableArray<double> Greeks(
        TradeResult initial, TradeResult final, DateOnly start, MarketSnapshot rolled, MarketSnapshot end, double[] waterfall, double total)
    {
        double[] greeks = new double[Factors];
        var result = initial.Result!;
        double days = end.AsOf.DayNumber - start.DayNumber;

        foreach (var point in initial.Market)
        {
            string name = point.Underlying;
            string? tag = initial.Position.Instrument.Underlyings.Length > 1 ? name : null;
            double delta = result.Greek(Measure.Delta, tag)?.Value ?? 0;
            double gamma = result.Greek(Measure.Gamma, tag)?.Value ?? 0;
            double vega = result.Greek(Measure.Vega, tag)?.Value ?? 0;
            double forward = rolled.Underlying(name).Spot;
            double target = end.Underlying(name).Spot;
            double move = target - point.Spot;

            greeks[(int)PnlFactor.Time] += delta * (forward - point.Spot);
            greeks[(int)PnlFactor.Spot] += delta * (target - forward) + 0.5 * gamma * move * move;
            if (final.Market.FirstOrDefault(m => m.Underlying == name) is { } later)
            {
                greeks[(int)PnlFactor.VolLevel] += vega * (later.Vol - point.Vol) / VolPoint;
                greeks[(int)PnlFactor.Rates] += (result.Greek(Measure.Rho)?.Value ?? 0) * (later.Rate - point.Rate) / RatePoint;
            }
        }

        greeks[(int)PnlFactor.Time] += (result.Greek(Measure.Theta)?.Value ?? 0) * days;
        greeks[(int)PnlFactor.Events] = waterfall[(int)PnlFactor.Events];
        greeks[(int)PnlFactor.Composition] = waterfall[(int)PnlFactor.Composition];
        greeks[(int)PnlFactor.Residual] = total - greeks.Sum();
        return [.. greeks];
    }
}
