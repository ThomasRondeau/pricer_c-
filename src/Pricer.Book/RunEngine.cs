using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Pricer.Core;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Book;

/// <summary>Market levels a trade saw, kept so that a later diff can name what moved.</summary>
public sealed record MarketPoint(string Underlying, double Spot, double Vol, double Rate);

/// <param name="Hash">Every input of the calculation; null when the trade failed before pricing.</param>
public sealed record TradeResult(
    Position Position, string? Hash, PricingResult? Result, string? Error, bool FromCache, double Milliseconds,
    ModelConfig? Model, EngineConfig? Engine, ImmutableArray<MarketPoint> Market);

public sealed record RunResult(
    DateOnly Valuation, long SnapshotId, string CodeVersion, RunConfig Config, ImmutableArray<TradeResult> Trades);

public sealed record RunProgress(int Done, int Total);

public interface IRunCache
{
    PricingResult? Find(string hash);
}

public sealed class MemoryRunCache : IRunCache
{
    private readonly ConcurrentDictionary<string, PricingResult> _results = new();

    public PricingResult? Find(string hash) => _results.GetValueOrDefault(hash);

    public void Add(RunResult run)
    {
        foreach (var trade in run.Trades.Where(t => t.Hash is not null && t.Result is not null))
        {
            _results[trade.Hash!] = trade.Result!;
        }
    }
}

/// <summary>Prices a book trade by trade. A trade's hash covers only its own underlyings, so a market move
/// recomputes only the trades it touches. A failing trade is reported and does not stop the run.</summary>
public sealed class RunEngine(EngineRegistry registry, IRunCache cache)
{
    private const int MaxConcurrentTrades = 4;
    private static readonly ImmutableArray<Measure> AllMeasures = [.. Enum.GetValues<Measure>()];

    /// <summary>Identity of the compiled pricing code: a run replays only on the code that produced it.</summary>
    public static string CodeVersion { get; } = ContentHash.Of(new[]
    {
        typeof(ContentHash), typeof(MarketSnapshot), typeof(Instrument), typeof(ModelConfig), typeof(EngineRegistry), typeof(RiskCalculator),
    }.Select(t => t.Module.ModuleVersionId.ToString()).ToArray());

    public async Task<RunResult> ExecuteAsync(
        IReadOnlyList<Position> positions, MarketSnapshot snapshot, long snapshotId, RunConfig config,
        IProgress<RunProgress>? progress, CancellationToken token)
    {
        var market = config.Shocked(snapshot);
        var results = new TradeResult[positions.Count];
        var heston = new ConcurrentDictionary<string, Lazy<HestonConfig>>();
        var risk = new RiskCalculator(registry);
        int done = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentTrades, CancellationToken = token };
        await Parallel.ForEachAsync(Enumerable.Range(0, positions.Count), options, (index, _) =>
        {
            results[index] = Price(positions[index], market, config, risk, heston);
            progress?.Report(new RunProgress(Interlocked.Increment(ref done), positions.Count));
            return ValueTask.CompletedTask;
        });
        return new RunResult(market.AsOf, snapshotId, CodeVersion, config, [.. results]);
    }

    private TradeResult Price(
        Position position, MarketSnapshot market, RunConfig config, RiskCalculator risk,
        ConcurrentDictionary<string, Lazy<HestonConfig>> heston)
    {
        var watch = Stopwatch.StartNew();
        ModelConfig? model = null;
        EngineConfig? engine = null;
        string? hash = null;
        ImmutableArray<MarketPoint> points = [];
        try
        {
            var instrument = position.Instrument;
            points = Points(instrument, market);
            var choice = config.ModelFor(position);
            model = Model(choice, instrument, market, config, heston);
            engine = ResolveEngine(instrument, model, config);

            var riskConfig = config.RiskFor(instrument);
            hash = Hash(position, market, model, engine, riskConfig);
            if (cache.Find(hash) is { } known)
            {
                return new TradeResult(position, hash, known, null, true, watch.Elapsed.TotalMilliseconds, model, engine, points);
            }

            var request = new PricingRequest(position.ToTrade(), market, model, engine, market.AsOf, AllMeasures);
            var result = risk.Compute(request, riskConfig);
            return new TradeResult(position, hash, result, null, false, watch.Elapsed.TotalMilliseconds, model, engine, points);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            return new TradeResult(position, null, null, e.Message, false, watch.Elapsed.TotalMilliseconds, model, engine, points);
        }
    }

    private static ModelConfig Model(
        ModelChoice choice, Instrument instrument, MarketSnapshot market, RunConfig config,
        ConcurrentDictionary<string, Lazy<HestonConfig>> heston) => choice switch
    {
        ModelChoice.LocalVol => config.LocalVol,
        // One calibration per underlying, whatever the number of trades on it.
        ModelChoice.Heston => heston.GetOrAdd(
            instrument.Underlying, u => new Lazy<HestonConfig>(() => HestonCalibrator.Fit(market, u).Config)).Value,
        _ => new BlackScholesConfig(),
    };

    /// <summary>The most exact engine the product has under this model.</summary>
    public EngineConfig ResolveEngine(Instrument instrument, ModelConfig model, RunConfig config)
    {
        var available = registry.ConfigsFor(instrument.GetType(), model.GetType());
        if (available.Contains(typeof(AnalyticEngineConfig))) return new AnalyticEngineConfig();
        if (available.Contains(typeof(PdeEngineConfig))) return config.Pde;
        if (available.Contains(typeof(MonteCarloEngineConfig))) return config.MonteCarlo;
        throw new NotSupportedException($"No engine for {instrument.GetType().Name} under {model.GetType().Name}.");
    }

    private static string Hash(Position position, MarketSnapshot market, ModelConfig model, EngineConfig engine, RiskConfig risk)
    {
        var instrument = position.Instrument;
        var relevant = instrument.Underlyings.Order().ToDictionary(n => n, market.Underlying);
        return ContentHash.Of(new
        {
            CodeVersion, Instrument = instrument, position.Quantity, position.State, market.AsOf, market.DiscountCurve,
            Underlyings = relevant, Model = model, Engine = engine, Risk = risk,
        });
    }

    private static ImmutableArray<MarketPoint> Points(Instrument instrument, MarketSnapshot market)
    {
        var dated = instrument as IDatedProduct;
        double t = dated is null ? 1 : Math.Max(DayCount.Act365F.YearFraction(market.AsOf, dated.Expiry), 1 / 365.0);
        return [.. instrument.Underlyings.Select(name =>
        {
            var underlying = market.Underlying(name);
            double strike = dated?.ReferenceStrike ?? underlying.Spot;
            return new MarketPoint(name, underlying.Spot, underlying.Vol.Vol(strike, t), market.DiscountCurve.ZeroRate(t));
        })];
    }
}
