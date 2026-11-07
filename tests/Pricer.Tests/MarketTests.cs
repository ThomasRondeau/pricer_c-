using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Data;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Tests;

public class MarketTests
{
    private static readonly DateOnly Today = new(2026, 1, 2);
    private static readonly YieldCurve Curve = SyntheticMarketDataProvider.DiscountCurve;

    private static OptionChain Chain(int seed = 7) => new SyntheticOptionChainSource(seed)
        .FetchAsync(new ChainRequest("SYNTH"), Today, null, default).Result;

    private static SurfaceBuildResult Build() => SurfaceBuilder.Build(Chain(), Curve);

    [Theory]
    [InlineData(100, 100, 1.0, 0.2, true)]
    [InlineData(110, 80, 0.25, 0.45, false)]
    [InlineData(95, 130, 2.0, 0.15, true)]
    public void ImpliedVolRoundTrips(double forward, double strike, double t, double vol, bool isCall)
    {
        double df = Math.Exp(-0.03 * t);
        double price = BlackScholes.Evaluate(new(forward, strike, t, vol, 0.03, 0.03), isCall).Price;
        Assert.Equal(vol, ImpliedVol.Solve(price, forward, strike, t, df, isCall)!.Value, 6);
    }

    [Fact]
    public void ImpliedVolRejectsPriceBelowIntrinsic()
    {
        Assert.Null(ImpliedVol.Solve(1.0, 120, 100, 1, 0.97, true));
    }

    [Fact]
    public void ForwardsAreRecoveredFromParity()
    {
        var result = Build();
        foreach (var slice in result.Report.Slices)
        {
            double t = slice.Slice.T;
            double expected = 100 * Math.Exp(-0.02 * t) / Curve.Discount(t);
            Assert.True(Math.Abs(slice.Slice.Forward - expected) < 0.02, $"T={t}: {slice.Slice.Forward} vs {expected}");
        }
    }

    [Fact]
    public void SurfaceRecoversTrueSmileAndIsArbitrageFree()
    {
        var result = Build();
        Assert.Equal(6, result.Report.Slices.Length);
        Assert.Equal(0, result.Report.CalendarViolations);
        Assert.Empty(result.Report.Warnings);

        foreach (var report in result.Report.Slices)
        {
            Assert.True(report.RmseVol < 0.003, $"RMSE {report.RmseVol} at T={report.Slice.T}");
            var truth = SyntheticOptionChainSource.TrueSlice(report.Slice.T, report.Slice.Forward);
            foreach (double strike in new[] { 85.0, 100.0, 115.0 })
            {
                double k = Math.Log(strike / report.Slice.Forward);
                double fitted = Math.Sqrt(report.Slice.TotalVariance(k) / report.Slice.T);
                double expected = Math.Sqrt(truth.TotalVariance(k) / report.Slice.T);
                Assert.True(Math.Abs(fitted - expected) < 0.004, $"T={report.Slice.T} K={strike}: {fitted} vs {expected}");
            }
        }
    }

    [Fact]
    public void BrokenChainIsReportedNotHidden()
    {
        var chain = Chain();
        var thin = chain with { Quotes = [.. chain.Quotes.Where(q => q.Expiry != Today.AddDays(90) || q.Strike % 10 == 0 && q.Strike < 100)] };
        var result = SurfaceBuilder.Build(thin, Curve);
        Assert.Equal(5, result.Report.Slices.Length);
        Assert.Single(result.Report.Warnings);
    }

    [Fact]
    public void AmericanChainCarriesWarning()
    {
        var result = SurfaceBuilder.Build(Chain() with { Exercise = ExerciseStyle.American }, Curve);
        Assert.Contains(result.Report.Warnings, w => w.Contains("American options"));
    }

    [Fact]
    public void CalendarArbitrageIsDetected()
    {
        var chain = Chain();
        var far = Today.AddDays(730);
        var cheap = chain with
        {
            Quotes = [.. chain.Quotes.Select(q => q.Expiry == far ? q with { Bid = q.Bid * 0.3, Ask = q.Ask * 0.3 } : q)],
        };
        Assert.True(SurfaceBuilder.Build(cheap, Curve).Report.CalendarViolations > 0);
    }

    [Fact]
    public void SurfaceInterpolatesVarianceAndExtrapolatesFlat()
    {
        var surface = (SviVolSurface)Build().Market.Vol;
        var first = surface.Slices[0];
        var last = surface.Slices[^1];

        Assert.Equal(surface.Vol(100, first.T), surface.Vol(100, first.T / 2), 12);
        Assert.Equal(surface.Vol(100, last.T), surface.Vol(100, last.T * 2), 12);

        double mid = (surface.Slices[2].T + surface.Slices[3].T) / 2;
        double variance = surface.Vol(100, mid) * surface.Vol(100, mid) * mid;
        double low = surface.Vol(100, surface.Slices[2].T);
        double high = surface.Vol(100, surface.Slices[3].T);
        Assert.InRange(variance, low * low * surface.Slices[2].T, high * high * surface.Slices[3].T);
    }

    [Fact]
    public void SpotBumpKeepsSurfaceFixedByStrike()
    {
        var market = Build().Market;
        var snapshot = new MarketSnapshot(Today, Curve,
            ImmutableSortedDictionary<string, UnderlyingMarket>.Empty.Add("X", market));
        var bumped = new SpotBump("X", 0.05).Apply(snapshot);
        Assert.Equal(market.Vol.Vol(105, 0.5), bumped.Underlying("X").Vol.Vol(105, 0.5));
    }

    [Fact]
    public void VolBumpShiftsWholeSurface()
    {
        var surface = Build().Market.Vol;
        Assert.Equal(surface.Vol(90, 0.7) + 0.01, surface.Shifted(0.01).Vol(90, 0.7), 12);
    }

    [Fact]
    public void EngineRepricesMarketQuotesAndForwards()
    {
        var chain = Chain();
        var result = SurfaceBuilder.Build(chain, Curve);
        var snapshot = new MarketSnapshot(Today, Curve,
            ImmutableSortedDictionary<string, UnderlyingMarket>.Empty.Add("SYNTH", result.Market));
        var risk = new RiskCalculator(EngineRegistry.Default());

        foreach (var quote in chain.Quotes.Where(q => q.Strike is >= 85 and <= 115))
        {
            var option = new EuropeanOption
            {
                Underlying = "SYNTH", Strike = quote.Strike, Expiry = quote.Expiry,
                Right = quote.IsCall ? OptionRight.Call : OptionRight.Put,
            };
            var request = new PricingRequest(new Trade("Q", option, 1), snapshot, new BlackScholesConfig(),
                new AnalyticEngineConfig(), Today, []);
            double pv = risk.Compute(request, new RiskConfig()).Pv;
            double tolerance = Math.Max(quote.Ask - quote.Bid, 0.05);
            Assert.True(Math.Abs(pv - quote.Mid) < tolerance, $"{quote}: {pv}");
        }
    }

    [Fact]
    public void GreeksStillAgreeWithBumpOnCalibratedSurface()
    {
        var snapshot = new MarketSnapshot(Today, Curve,
            ImmutableSortedDictionary<string, UnderlyingMarket>.Empty.Add("SYNTH", Build().Market));
        var option = new EuropeanOption { Underlying = "SYNTH", Strike = 105, Expiry = Today.AddDays(200), Right = OptionRight.Call };
        var request = new PricingRequest(new Trade("Q", option, 1), snapshot, new BlackScholesConfig(),
            new AnalyticEngineConfig(), Today, [Measure.Delta, Measure.Vega]);
        var risk = new RiskCalculator(EngineRegistry.Default());

        var closed = risk.Compute(request, new RiskConfig());
        var bumped = risk.Compute(request, new RiskConfig { Preference = GreekPreference.ForceBump });
        Assert.Equal(closed.Greek(Measure.Delta)!.Value, bumped.Greek(Measure.Delta)!.Value, 3);
        Assert.Equal(closed.Greek(Measure.Vega)!.Value, bumped.Greek(Measure.Vega)!.Value, 3);
    }

    [Fact]
    public async Task CaptureRoundTripsThroughStore()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pricer-test-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SnapshotStore(path);
            var settings = new CaptureSettings { Watchlist = [new ChainRequest("SYNTH")], DiscountCurveConfirmed = true };
            var outcome = await new SnapshotCapture(new SyntheticOptionChainSource(), store).RunAsync(settings, Today, null, default);

            var loaded = store.Load(outcome.SnapshotId);
            var info = Assert.Single(store.List());
            Assert.Equal(info.Hash, ContentHash.Of(loaded.Snapshot));
            Assert.Equal("SYNTH", info.Underlyings);
            Assert.Equal(outcome.Reports[0].Slices.Length, loaded.Reports[0].Slices.Length);

            var original = ((SviVolSurface)store.GetSnapshot(Today.AddDays(3)).Underlying("SYNTH").Vol).Vol(100, 0.5);
            Assert.Equal(((SviVolSurface)loaded.Snapshot.Underlying("SYNTH").Vol).Vol(100, 0.5), original);
            Assert.NotEmpty(store.LoadChain(outcome.SnapshotId, "SYNTH").Quotes);
            Assert.Throws<KeyNotFoundException>(() => store.GetSnapshot(Today.AddDays(-1)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnconfirmedCurveIsFlagged()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pricer-test-{Guid.NewGuid():N}.db");
        try
        {
            var settings = new CaptureSettings { Watchlist = [new ChainRequest("SYNTH")] };
            var outcome = await new SnapshotCapture(new SyntheticOptionChainSource(), new SnapshotStore(path))
                .RunAsync(settings, Today, null, default);
            Assert.Contains(outcome.Reports[0].Warnings, w => w.Contains("Default discount curve"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
