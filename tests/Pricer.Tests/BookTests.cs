using System.Collections.Immutable;
using Pricer.Book;
using Pricer.Data;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Tests;

public class BookTests : IDisposable
{
    private const string A = "A";
    private const string B = "B";
    private const long SnapshotId = 1;
    private static readonly DateOnly Today = new(2026, 1, 2);
    private static readonly RunConfig Light = new() { MonteCarlo = new MonteCarloEngineConfig { Paths = 1 << 11, Batches = 8 } };
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pricer-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static MarketSnapshot Market(double spotA = 100, double spotB = 200) => new(
        Today, YieldCurve.Flat(0.03),
        ImmutableSortedDictionary<string, UnderlyingMarket>.Empty
            .Add(A, new UnderlyingMarket(spotA, YieldCurve.Flat(0.01), new FlatVolSurface(0.2)))
            .Add(B, new UnderlyingMarket(spotB, YieldCurve.Flat(0.01), new FlatVolSurface(0.3))));

    private static Position Stock(string id, string underlying, double quantity = 10) =>
        new(id, new Stock { Underlying = underlying }, quantity);

    private static Position Call(string id, string underlying, double strike) => new(
        id, new EuropeanOption { Underlying = underlying, Right = OptionRight.Call, Strike = strike, Expiry = Today.AddYears(1) }, 5);

    private static Task<RunResult> Run(
        IReadOnlyList<Position> positions, RunConfig? config = null, MarketSnapshot? market = null, IRunCache? cache = null,
        CancellationToken token = default) =>
        new RunEngine(EngineRegistry.Default(), cache ?? new MemoryRunCache())
            .ExecuteAsync(positions, market ?? Market(), SnapshotId, config ?? Light, null, token);

    [Fact]
    public async Task DemoBookPricesEveryProductWithItsBestEngine()
    {
        var book = DemoBook.For(Market(), A);
        var run = await Run(book);

        Assert.All(run.Trades, t => Assert.Null(t.Error));
        Assert.Equal(book.Length, run.Trades.Length);

        string EngineOf(string id) => run.Trades.First(t => t.Position.Id == id).Result!.Engine;
        Assert.Equal("BlackScholes.Analytic", EngineOf("DEMO-2"));
        Assert.Equal("BlackScholes.Pde", EngineOf("DEMO-4"));
        Assert.Equal("LocalVol.Pde", EngineOf("DEMO-5"));
        Assert.Equal("LocalVol.MonteCarlo", EngineOf("DEMO-8"));
    }

    [Fact]
    public async Task GreeksAreAlwaysInMarketConventionSoTheyAdd()
    {
        var run = await Run([Call("C", A, 100)]);
        var greeks = run.Trades[0].Result!.Greeks;
        Assert.All(greeks, g =>
        {
            Assert.Equal(GreekMethod.BumpReprice, g.Method);
            Assert.Equal(GreekConvention.MarketStickyStrike, g.Convention);
        });
        Assert.Equal(0, BookAggregator.Group(run.Trades, BookGrouping.Total)[0].ModelGreekTrades);
    }

    [Fact]
    public async Task HestonModelGreeksAreCountedAndSignalled()
    {
        var run = await Run([Call("C", A, 100) with { Model = ModelChoice.Heston }]);
        Assert.Null(run.Trades[0].Error);
        Assert.Equal(1, BookAggregator.Group(run.Trades, BookGrouping.Total)[0].ModelGreekTrades);
    }

    [Fact]
    public async Task SecondRunComesFromCacheWithIdenticalResults()
    {
        var cache = new MemoryRunCache();
        var book = DemoBook.For(Market(), A);
        var first = await Run(book, cache: cache);
        cache.Add(first);
        var second = await Run(book, cache: cache);

        Assert.All(second.Trades, t => Assert.True(t.FromCache));
        Assert.Equal(first.Trades.Select(t => t.Result!.Pv), second.Trades.Select(t => t.Result!.Pv));
    }

    [Fact]
    public async Task MarketMoveRecomputesOnlyTheTradesItTouches()
    {
        var cache = new MemoryRunCache();
        Position[] book = [Call("on-a", A, 100), Call("on-b", B, 200)];
        cache.Add(await Run(book, cache: cache));

        var moved = await Run(book, market: Market(spotA: 101), cache: cache);
        Assert.False(moved.Trades.First(t => t.Position.Id == "on-a").FromCache);
        Assert.True(moved.Trades.First(t => t.Position.Id == "on-b").FromCache);
    }

    [Fact]
    public async Task ConfigChangeRecomputesOnlyWhatItAffects()
    {
        var cache = new MemoryRunCache();
        var note = new Position("note", new Autocall
        {
            Underlying = A, InitialFixing = 100, Start = Today, Expiry = Today.AddYears(1),
        }, 1);
        Position[] book = [note, Call("call", A, 100)];
        cache.Add(await Run(book, cache: cache));

        var reseeded = Light with { MonteCarlo = Light.MonteCarlo with { Seed = 7 } };
        var second = await Run(book, reseeded, cache: cache);
        Assert.False(second.Trades.First(t => t.Position.Id == "note").FromCache);
        Assert.True(second.Trades.First(t => t.Position.Id == "call").FromCache);
    }

    [Fact]
    public async Task FailingTradeIsReportedAndNotCachedAndOthersSurvive()
    {
        var late = new Autocall { Underlying = A, InitialFixing = 100, Start = Today.AddMonths(-6), Expiry = Today.AddYears(1) };
        Position[] book = [Stock("good", A), Stock("unknown", "NOPE"), new Position("no-fixing", late, 1)];
        var cache = new MemoryRunCache();
        var run = await Run(book, cache: cache);
        cache.Add(run);

        Assert.Null(run.Trades[0].Error);
        Assert.Contains("NOPE", run.Trades[1].Error);
        Assert.Contains("Missing fixing", run.Trades[2].Error);

        var total = BookAggregator.Group(run.Trades, BookGrouping.Total)[0];
        Assert.Equal(2, total.Failed);
        Assert.Equal(1000, total.Pv, 8);
        Assert.False((await Run(book, cache: cache)).Trades[2].FromCache);
    }

    [Fact]
    public async Task UnsupportedModelIsAPerTradeError()
    {
        var american = new AmericanOption { Underlying = A, Right = OptionRight.Put, Strike = 100, Expiry = Today.AddYears(1) };
        var run = await Run([new Position("x", american, 1, Model: ModelChoice.Heston)]);
        Assert.Contains("No engine", run.Trades[0].Error);
    }

    [Fact]
    public async Task CancellationStopsTheRun()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(DemoBook.For(Market(), A), token: cancel.Token));
    }

    [Fact]
    public async Task AggregationIsInCashAndGroupsAddUpToTheTotal()
    {
        Position[] book = [Stock("a", A), Stock("b", B, -2) with { Tag = "hedge" }, Call("c", A, 100)];
        var run = await Run(book);

        var total = BookAggregator.Group(run.Trades, BookGrouping.Total)[0];
        var byName = BookAggregator.Group(run.Trades, BookGrouping.Underlying);
        var stockOnly = BookAggregator.Group(run.Trades.Where(t => t.Position.Id == "b"), BookGrouping.Total)[0];

        Assert.Equal(-400, stockOnly.CashDelta, 6);
        Assert.Equal(total.Pv, byName.Sum(r => r.Pv), 8);
        Assert.Equal(total.CashDelta, byName.Sum(r => r.CashDelta), 8);
        Assert.Equal(3, BookAggregator.Group(run.Trades, BookGrouping.Product).Sum(r => r.Trades));
        Assert.Contains(BookAggregator.Group(run.Trades, BookGrouping.Tag), r => r.Key == "hedge");
    }

    [Fact]
    public async Task WorstOfCashDeltaUsesEachLegsSpot()
    {
        var worst = new WorstOfPhoenix
        {
            Underlying = A, InitialFixing = 100, Start = Today, Expiry = Today.AddYears(1),
            Basket = [new BasketLeg(A, 100), new BasketLeg(B, 200)],
        };
        var run = await Run([new Position("w", worst, 1)]);
        var result = run.Trades[0].Result!;
        double expected = result.Greek(Measure.Delta, A)!.Value * 100 + result.Greek(Measure.Delta, B)!.Value * 200;
        Assert.Equal(expected, BookAggregator.Group(run.Trades, BookGrouping.Total)[0].CashDelta, 8);
        Assert.Equal("A+B", BookAggregator.Key(run.Trades[0].Position, BookGrouping.Underlying));
    }

    [Fact]
    public async Task ScenarioShocksEveryUnderlyingBeforePricing()
    {
        var run = await Run([Stock("a", A), Stock("b", B)], Light with { SpotShock = 0.1 });
        Assert.Equal(1100, run.Trades[0].Result!.Pv, 8);
        Assert.Equal(2200, run.Trades[1].Result!.Pv, 8);
    }

    [Fact]
    public async Task StoreRoundTripsPositionsAndRunsAndServesAsCache()
    {
        var store = new BookStore(_path);
        var book = DemoBook.For(Market(), A);
        store.Save(book);
        Assert.Equal(book.Length, store.Positions().Count);
        Assert.IsType<Autocall>(store.Positions().First(p => p.Id == "DEMO-8").Instrument);

        var run = await Run(store.Positions(), cache: store);
        long id = store.Save(run, "premier");
        var loaded = store.Load(id);
        Assert.Equal(run.Trades.Sum(t => t.Result!.Pv), loaded.Trades.Sum(t => t.Result!.Pv), 8);
        Assert.Equal(run.Config.MonteCarlo, loaded.Config.MonteCarlo);
        Assert.Equal("premier", Assert.Single(store.Runs()).Label);

        var second = await Run(store.Positions(), cache: new BookStore(_path));
        Assert.All(second.Trades, t => Assert.True(t.FromCache));
        Assert.Equal(run.Trades.OrderBy(t => t.Position.Id).Select(t => t.Result!.Pv), second.Trades.OrderBy(t => t.Position.Id).Select(t => t.Result!.Pv));

        store.Delete("DEMO-1");
        Assert.Equal(book.Length - 1, store.Positions().Count);
    }

    [Fact]
    public async Task ComparisonNamesEachCauseDownToTheParameter()
    {
        var call = Call("call", A, 100);
        Position[] left = [call, Stock("gone", A), Stock("same", B)];
        Position[] right =
        [
            call with { Instrument = ((EuropeanOption)call.Instrument) with { Strike = 110 } },
            Stock("new", A),
            Stock("same", B),
        ];

        var comparison = RunComparer.Compare(
            await Run(left),
            await Run(right, Light with { VolShock = 0.01 }, Market(spotA: 105)));

        TradeDifference Trade(string id) => comparison.Trades.First(t => t.Id == id);
        Assert.Contains(Trade("call").Causes, c => c.StartsWith("Strike"));
        Assert.Contains(Trade("call").Causes, c => c.StartsWith("Spot A"));
        Assert.Contains(Trade("call").Causes, c => c.StartsWith("Vol A"));
        Assert.Equal("Trade removed", Assert.Single(Trade("gone").Causes));
        Assert.Equal("Trade added", Assert.Single(Trade("new").Causes));
        Assert.Contains(comparison.ConfigChanges, c => c.StartsWith("Scenario: vol shock"));
        Assert.Contains(Trade("same").Causes, c => c.StartsWith("Vol B"));
        Assert.Equal(comparison.RightPv - comparison.LeftPv, comparison.Trades.Sum(t => t.Change), 8);
    }

    [Fact]
    public async Task IdenticalRunsHaveNothingToReport()
    {
        var book = DemoBook.For(Market(), A);
        var comparison = RunComparer.Compare(await Run(book), await Run(book));
        Assert.Empty(comparison.Trades);
        Assert.Empty(comparison.ConfigChanges);
    }

    [Fact]
    public void BrokerImportConvertsAndSkips()
    {
        BrokerPosition[] broker =
        [
            new(1, "SPY", "STK", "", 0, "", 1, 200),
            new(2, "SPY", "OPT", "P", 700, "20270115", 100, -3),
            new(3, "SPX", "OPT", "C", 7000, "20270115", 100, 1),
            new(4, "ES", "FUT", "", 0, "202703", 50, 1),
            new(5, "QQQ", "STK", "", 0, "", 1, 0),
        ];
        var result = BrokerImport.Merge([], broker, s => s == "SPX" ? ExerciseStyle.European : ExerciseStyle.American);

        Assert.Equal(3, result.Positions.Length);
        Assert.Equal(3, result.Added);
        Assert.Equal("ES (FUT)", Assert.Single(result.Skipped));
        var put = result.Positions.First(p => p.Id == "IBKR-2");
        Assert.IsType<AmericanOption>(put.Instrument);
        Assert.Equal(-300, put.Quantity);
        Assert.IsType<EuropeanOption>(result.Positions.First(p => p.Id == "IBKR-3").Instrument);
    }

    [Fact]
    public void ReconciliationKeepsLocalTradesAndTagsAndClosesTheRest()
    {
        var local = Stock("note", A);
        var kept = new Position("IBKR-1", new Stock { Underlying = "SPY" }, 100, BrokerImport.Source, "hedge");
        var closed = new Position("IBKR-9", new Stock { Underlying = "QQQ" }, 5, BrokerImport.Source);
        BrokerPosition[] broker = [new(1, "SPY", "STK", "", 0, "", 1, 150), new(2, "IWM", "STK", "", 0, "", 1, 10)];

        var result = BrokerImport.Merge([local, kept, closed], broker, _ => ExerciseStyle.American);

        Assert.Contains(local, result.Positions);
        Assert.DoesNotContain(result.Positions, p => p.Id == "IBKR-9");
        var updated = result.Positions.First(p => p.Id == "IBKR-1");
        Assert.Equal(150, updated.Quantity);
        Assert.Equal("hedge", updated.Tag);
        Assert.Equal((1, 1, 1), (result.Added, result.Changed, result.Closed));
    }
}
