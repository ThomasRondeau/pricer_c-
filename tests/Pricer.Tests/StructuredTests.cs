using System.Collections.Immutable;
using System.Text.Json;
using Pricer.Core;
using Pricer.Data;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Tests;

public class StructuredTests
{
    private const string A = "A";
    private const string B = "B";
    private const double Spot = 100;
    private const double Vol = 0.2;
    private const double Rate = 0.03;
    private const double Carry = 0.01;
    private static readonly DateOnly Today = new(2026, 1, 2);
    private static readonly DateOnly TwoYears = Today.AddYears(2);
    private static readonly ImmutableArray<Measure> NoMeasures = [];
    private static readonly MonteCarloEngineConfig Mc = new() { Paths = 1 << 18 };
    private static readonly PdeEngineConfig Pde = new() { SpaceSteps = 800, TimeSteps = 400 };
    private static readonly LocalVolConfig LocalVol = new();
    private readonly RiskCalculator _risk = new(EngineRegistry.Default());

    private static MarketSnapshot Flat() => new(
        Today, YieldCurve.Flat(Rate),
        ImmutableSortedDictionary<string, UnderlyingMarket>.Empty
            .Add(A, new UnderlyingMarket(Spot, YieldCurve.Flat(Carry), new FlatVolSurface(Vol)))
            .Add(B, new UnderlyingMarket(Spot, YieldCurve.Flat(Carry), new FlatVolSurface(Vol))));

    private static OptionChain Chain() =>
        new SyntheticOptionChainSource().FetchAsync(new ChainRequest(A), Today, null, default).Result;

    private static MarketSnapshot Smile()
    {
        var built = SurfaceBuilder.Build(Chain(), SyntheticMarketDataProvider.DiscountCurve);
        return new MarketSnapshot(
            Today, SyntheticMarketDataProvider.DiscountCurve,
            ImmutableSortedDictionary<string, UnderlyingMarket>.Empty.Add(A, built.Market));
    }

    private PricingResult Price(
        Instrument instrument, ModelConfig model, EngineConfig engine, MarketSnapshot? market = null, TradeState? state = null,
        ImmutableArray<Measure> measures = default, RiskConfig? risk = null) =>
        _risk.Compute(
            new PricingRequest(new Trade("T", instrument, 1, state), market ?? Flat(), model, engine, Today,
                measures.IsDefault ? NoMeasures : measures),
            risk ?? new RiskConfig());

    private static void Near(double expected, PricingResult result, double errors = 4, double floor = 0) =>
        Assert.True(Math.Abs(result.Pv - expected) <= errors * result.PvStandardError!.Value + floor,
            $"{result.Pv} ± {result.PvStandardError} vs {expected}");

    private static EuropeanOption European(OptionRight right, double strike, DateOnly expiry, string underlying = A) =>
        new() { Underlying = underlying, Right = right, Strike = strike, Expiry = expiry };

    private static ReverseConvertible Convertible(double protection = 1) => new()
    {
        Underlying = A, InitialFixing = Spot, Start = Today, Expiry = TwoYears, Protection = protection,
    };

    private static Autocall AutocallNote(double call = 1, double protection = 0.6) => new()
    {
        Underlying = A, InitialFixing = Spot, Start = Today, Expiry = TwoYears, CallBarrier = call, Protection = protection,
    };

    private static Phoenix PhoenixNote(double call = 1000, double coupon = 0.7, bool memory = true, double protection = 0.6) => new()
    {
        Underlying = A, InitialFixing = Spot, Start = Today, Expiry = TwoYears,
        CallBarrier = call, CouponBarrier = coupon, Memory = memory, Protection = protection,
    };

    private static WorstOfPhoenix Worst(params (string Name, double Fixing)[] legs) => new()
    {
        Underlying = legs[0].Name, InitialFixing = legs[0].Fixing, Start = Today, Expiry = TwoYears,
        Basket = [.. legs.Select(l => new BasketLeg(l.Name, l.Fixing))],
    };

    private static double Years(DateOnly date) => DayCount.Act365F.YearFraction(Today, date);

    private static double Df(DateOnly date) => Math.Exp(-Rate * Years(date));

    [Fact]
    public void HestonMatchesFangOosterleeReference()
    {
        var p = new HestonConfig { V0 = 0.0175, Kappa = 1.5768, Theta = 0.0398, Xi = 0.5751, Rho = -0.5711 };
        Assert.Equal(5.78515545, new HestonSlice(p, 1).Evaluate(100, 100, 100, 1, true).Price, 5);
    }

    [Theory]
    [InlineData(80, 0.08)]
    [InlineData(100, 1)]
    [InlineData(130, 3)]
    public void HestonGreeksMatchFiniteDifferencesOfThePrice(double strike, double t)
    {
        var p = new HestonConfig { V0 = 0.05, Kappa = 2, Theta = 0.06, Xi = 0.6, Rho = -0.7 };
        double df = Math.Exp(-Rate * t);
        double carry = Math.Exp(-Carry * t);
        var slice = new HestonSlice(p, t);
        double Value(double s) => slice.Evaluate(s, s * carry / df, strike, df, false).Price;

        var s = slice.Evaluate(100, 100 * carry / df, strike, df, false);
        const double h = 0.01;
        Assert.Equal((Value(100 + h) - Value(100 - h)) / (2 * h), s.Delta, 5);
        Assert.Equal((Value(100 + h) - 2 * s.Price + Value(100 - h)) / (h * h), s.Gamma, 4);

        const double dv = 1e-5;
        double Vega(double v0) => new HestonSlice(p with { V0 = v0 }, t).Evaluate(100, 100 * carry / df, strike, df, false).Price;
        double byVariance = (Vega(p.V0 + dv) - Vega(p.V0 - dv)) / (2 * dv);
        Assert.Equal(byVariance * 2 * Math.Sqrt(p.V0), s.VegaV0, 3);
    }

    [Fact]
    public void HestonWithoutVolOfVolIsBlackScholes()
    {
        var p = new HestonConfig { V0 = Vol * Vol, Theta = Vol * Vol, Xi = 0.02, Rho = 0 };
        var option = European(OptionRight.Call, 110, TwoYears);
        double closed = Price(option, new BlackScholesConfig(), new AnalyticEngineConfig()).Pv;
        Assert.Equal(closed, Price(option, p, new AnalyticEngineConfig()).Pv, 2);
    }

    [Theory]
    [InlineData(OptionRight.Call, 100)]
    [InlineData(OptionRight.Put, 80)]
    public void HestonSimulationMatchesSemiAnalytic(OptionRight right, double strike)
    {
        var p = new HestonConfig { V0 = 0.04, Kappa = 1.5, Theta = 0.05, Xi = 0.7, Rho = -0.7 };
        var option = European(right, strike, TwoYears);
        double semi = Price(option, p, new AnalyticEngineConfig()).Pv;
        Near(semi, Price(option, p, Mc with { StepsPerYear = 26 }), floor: 0.03);
    }

    [Fact]
    public void HestonFitsTheSyntheticSmileAndGreeksSeparateModelFromMarket()
    {
        var market = Smile();
        var fit = HestonCalibrator.Fit(market, A);
        Assert.True(fit.RmseVol < 0.01, $"rmse {fit.RmseVol}");

        var option = European(OptionRight.Call, 100, Today.AddDays(365));
        ImmutableArray<Measure> vega = [Measure.Vega];
        var fixedModel = Price(option, fit.Config with { RecalibrateOnBump = false }, new AnalyticEngineConfig(), market, measures: vega,
            risk: new RiskConfig { Preference = GreekPreference.ForceBump });
        Assert.Equal(0, fixedModel.Greek(Measure.Vega)!.Value, 10);

        var refit = fit.Config with { RecalibrateOnBump = true };
        var marketVega = Price(option, refit, new AnalyticEngineConfig(), market, measures: vega,
            risk: new RiskConfig { Preference = GreekPreference.ForceBump }).Greek(Measure.Vega)!;
        var modelVega = Price(option, refit, new AnalyticEngineConfig(), market, measures: vega).Greek(Measure.Vega)!;

        double blackVega = Price(option, new BlackScholesConfig(), new AnalyticEngineConfig(), market, measures: vega).Greek(Measure.Vega)!.Value;
        Assert.True(Math.Abs(marketVega.Value / blackVega - 1) < 0.15, $"{marketVega.Value} vs {blackVega}");
        Assert.Equal(GreekMethod.SemiAnalytic, modelVega.Method);
        Assert.Equal(GreekConvention.Model, modelVega.Convention);
        Assert.Equal(GreekConvention.MarketStickyStrike, marketVega.Convention);
    }

    [Theory]
    [InlineData(OptionRight.Call, 110)]
    [InlineData(OptionRight.Put, 90)]
    public void LocalVolOnFlatSurfaceIsBlackScholes(OptionRight right, double strike)
    {
        var option = European(right, strike, TwoYears);
        double closed = Price(option, new BlackScholesConfig(), new AnalyticEngineConfig()).Pv;
        Assert.Equal(closed, Price(option, LocalVol, Pde).Pv, 2);
        Near(closed, Price(option, LocalVol, Mc), floor: 0.02);
    }

    [Theory]
    [InlineData(80, 90)]
    [InlineData(90, 180)]
    [InlineData(100, 180)]
    [InlineData(115, 365)]
    [InlineData(85, 365)]
    [InlineData(100, 730)]
    public void DupireRepricesTheSurface(double strike, int days)
    {
        var market = Smile();
        var expiry = Today.AddDays(days);
        double t = days / 365.0;
        var right = strike >= 100 ? OptionRight.Call : OptionRight.Put;
        var option = European(right, strike, expiry);

        double target = Price(option, new BlackScholesConfig(), new AnalyticEngineConfig(), market).Pv;
        double surfaceVega = Price(option, new BlackScholesConfig(), new AnalyticEngineConfig(), market,
            measures: [Measure.Vega]).Greek(Measure.Vega)!.Value;

        double pde = Price(option, LocalVol, Pde, market).Pv;
        Assert.True(Math.Abs(pde - target) / surfaceVega < 0.3, $"PDE off by {(pde - target) / surfaceVega:F3} vol points at T={t}");

        var mc = Price(option, LocalVol, Mc, market);
        Assert.True(Math.Abs(mc.Pv - target) / surfaceVega < 0.5, $"MC off by {(mc.Pv - target) / surfaceVega:F3} vol points at T={t}");
    }

    [Fact]
    public void HestonDeltaAndGammaKeepModelParametersAndStaySmooth()
    {
        var market = Smile();
        var fit = HestonCalibrator.Fit(market, A).Config with { RecalibrateOnBump = true };
        var note = AutocallNote();
        var result = Price(note, fit, Mc with { Paths = 1 << 16 }, market, measures: [Measure.Delta, Measure.Gamma],
            risk: new RiskConfig { SpotBump = 0.01 });

        Assert.Equal(GreekConvention.Model, result.Greek(Measure.Delta)!.Convention);
        Assert.Equal(GreekConvention.Model, result.Greek(Measure.Gamma)!.Convention);
        Assert.True(Math.Abs(result.Greek(Measure.Gamma)!.Value) < 0.1, $"gamma {result.Greek(Measure.Gamma)!.Value}");
    }

    [Fact]
    public void LocalVolGridReportsNoInvalidNodesOnASmoothSurface()
    {
        var market = Smile().Underlying(A);
        double Forward(double t) => market.Spot * market.DividendCurve.Discount(t) / SyntheticMarketDataProvider.DiscountCurve.Discount(t);
        var grid = new LocalVolGrid(market.Vol, Forward, [0.1, 0.5, 1], 0.02, 2);
        Assert.True(grid.ClampedShare < 0.05, $"{grid.ClampedShare}");
    }

    [Fact]
    public void SkewMakesDownBarrierDifferentFromFlatVol()
    {
        var market = Smile();
        var option = new BarrierOption
        {
            Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = Today.AddDays(365), Barrier = 85, Kind = BarrierKind.DownOut,
        };
        double flat = Price(option, new BlackScholesConfig(), Pde, market).Pv;
        double local = Price(option, LocalVol, Pde, market).Pv;
        Near(local, Price(option, LocalVol, Mc with { StepsPerYear = 365 }, market), floor: 0.1);
        Assert.True(Math.Abs(local - flat) > 0.05, $"{local} vs {flat}");
    }

    [Fact]
    public void AmericanUnderLocalVolIsWorthAtLeastTheEuropean()
    {
        var market = Smile();
        var american = new AmericanOption { Underlying = A, Right = OptionRight.Put, Strike = 105, Expiry = Today.AddDays(365) };
        var european = European(OptionRight.Put, 105, Today.AddDays(365));
        Assert.True(Price(american, LocalVol, Pde, market).Pv >= Price(european, LocalVol, Pde, market).Pv);
    }

    [Fact]
    public void ReverseConvertibleDecomposesIntoBondCouponsAndShortPut()
    {
        var note = Convertible();
        double put = Price(European(OptionRight.Put, Spot, TwoYears), new BlackScholesConfig(), new AnalyticEngineConfig()).Pv;
        double coupons = note.Observations().Sum(d => note.Notional * note.Coupon * Df(d));
        double expected = note.Notional * Df(TwoYears) + coupons - note.Notional / Spot * put;

        Near(expected, Price(note, new BlackScholesConfig(), Mc));
        Near(expected, Price(note, LocalVol, Mc), floor: 0.02);
    }

    [Fact]
    public void AutocallThatCannotBeCalledIsDigitalPlusAssetPut()
    {
        var note = AutocallNote(call: 1000);
        double level = Spot * note.Protection;
        var engine = new AnalyticEngineConfig();
        var digital = new DigitalOption { Underlying = A, Right = OptionRight.Call, Strike = level, Expiry = TwoYears };
        var digitalPut = digital with { Right = OptionRight.Put };

        // Below the barrier the note holds performance: an asset-or-nothing put, K * digital put - put.
        var model = new BlackScholesConfig { VolAdjustment = 0 };
        double put = Price(European(OptionRight.Put, level, TwoYears), model, engine).Pv;
        double asset = level * Price(digitalPut, model, engine).Pv - put;
        double expected = note.Notional * (Price(digital, model, engine).Pv + asset / Spot);

        // The note reads the surface at its reference strike, and the flat surface makes that immaterial.
        Near(expected, Price(note, model, Mc));
    }

    [Fact]
    public void AutocallCertainToBeCalledRedeemsAtFirstObservation()
    {
        var note = AutocallNote(call: 0.01);
        double expected = note.Notional * (1 + note.Coupon) * Df(note.Observations()[0]);
        Assert.Equal(expected, Price(note, new BlackScholesConfig(), Mc).Pv, 8);
        Assert.Equal(expected, Price(note, LocalVol, Mc).Pv, 8);
    }

    [Fact]
    public void AutocallPathwiseIsNotAvailableAndLikelihoodRatioAgreesWithBump()
    {
        var note = AutocallNote();
        ImmutableArray<Measure> delta = [Measure.Delta];
        var mc = Price(note, new BlackScholesConfig(), Mc, measures: delta).Greek(Measure.Delta)!;
        Assert.Equal(GreekMethod.LikelihoodRatio, mc.Method);

        var bumped = Price(note, new BlackScholesConfig(), Mc, measures: delta,
            risk: new RiskConfig { Preference = GreekPreference.ForceBump, SpotBump = 0.01 }).Greek(Measure.Delta)!;
        Assert.True(Math.Abs(mc.Value - bumped.Value) < 4 * mc.StandardError + 0.01, $"{mc.Value} ± {mc.StandardError} vs {bumped.Value}");
    }

    [Fact]
    public void PhoenixWithCertainCouponsIsTheReverseConvertible()
    {
        var phoenix = PhoenixNote(coupon: 0.0001, protection: 1);
        var convertible = Convertible(protection: 1);
        Assert.Equal(Price(convertible, new BlackScholesConfig(), Mc).Pv, Price(phoenix, new BlackScholesConfig(), Mc).Pv, 8);
    }

    [Fact]
    public void MemoryAddsValueAndCouponBarrierTakesItAway()
    {
        double Value(Phoenix p) => Price(p, new BlackScholesConfig(), Mc).Pv;
        Assert.True(Value(PhoenixNote(memory: true)) > Value(PhoenixNote(memory: false)));
        Assert.True(Value(PhoenixNote(coupon: 0.6)) > Value(PhoenixNote(coupon: 0.9)));
    }

    [Fact]
    public void CalledNoteHasNoValueLeftAndReportsItsCash()
    {
        var note = AutocallNote() with { Start = Today.AddMonths(-3) };
        var state = new TradeState { Fixings = [new FixingRecord(A, Today.AddMonths(-3).AddMonths(3), 105)] };
        var first = note.Observations()[0];
        Assert.Equal(Today, first);

        var called = Price(note with { Start = Today.AddMonths(-4) }, new BlackScholesConfig(), Mc,
            state: new TradeState { Fixings = [new FixingRecord(A, Today.AddMonths(-1), 105)] });
        Assert.Equal(0, called.Pv);
        Assert.Equal(note.Notional * (1 + note.Coupon), called.Realised, 10);

        // Called today: the cash is still due, so it stays in the value.
        var today = Price(note, new BlackScholesConfig(), Mc, state: state);
        Assert.Equal(note.Notional * (1 + note.Coupon), today.Pv, 10);
        Assert.Equal(0, today.Realised);
    }

    [Fact]
    public void MissedCouponsAreRememberedFromFixings()
    {
        var start = Today.AddMonths(-4);
        var phoenix = PhoenixNote(coupon: 0.7, memory: true, call: 1000) with { Start = start };
        var missed = new TradeState { Fixings = [new FixingRecord(A, start.AddMonths(3), 50)] };
        var earned = new TradeState { Fixings = [new FixingRecord(A, start.AddMonths(3), 90)] };

        var withMemory = Price(phoenix, new BlackScholesConfig(), Mc, state: missed);
        var without = Price(phoenix with { Memory = false }, new BlackScholesConfig(), Mc, state: missed);
        var paid = Price(phoenix, new BlackScholesConfig(), Mc, state: earned);

        Assert.True(withMemory.Pv > without.Pv + 1);
        Assert.Equal(0, withMemory.Realised);
        Assert.Equal(phoenix.Notional * phoenix.Coupon, paid.Realised, 10);
    }

    [Fact]
    public void MissingFixingIsAnError()
    {
        var note = AutocallNote() with { Start = Today.AddMonths(-4) };
        var error = Assert.Throws<InvalidOperationException>(() => Price(note, new BlackScholesConfig(), Mc));
        Assert.Contains("Missing fixing", error.Message);
    }

    [Fact]
    public void AsianCarriesItsPastFixings()
    {
        var option = new AsianOption
        {
            Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = Today.AddDays(30), AveragingStart = Today.AddDays(-30), Fixings = 3,
        };
        var state = new TradeState { Fixings = [new FixingRecord(A, Today.AddDays(-30), 110), new FixingRecord(A, Today, 120)] };

        // Average = (110 + 120 + S) / 3, so the option is a third of a call struck at 300 - 230 = 70.
        double expected = Price(European(OptionRight.Call, 70, Today.AddDays(30)), new BlackScholesConfig(), new AnalyticEngineConfig()).Pv / 3;
        Near(expected, Price(option, new BlackScholesConfig(), Mc, state: state), floor: 1e-3);
    }

    [Fact]
    public void RecordedBreachOverridesASpotBackInsideTheBarrier()
    {
        var option = new BarrierOption
        {
            Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = TwoYears, Barrier = 80, Kind = BarrierKind.DownOut,
        };
        var state = new TradeState { BarrierBreached = true };
        double vanilla = Price(European(OptionRight.Call, 100, TwoYears), new BlackScholesConfig(), new AnalyticEngineConfig()).Pv;
        foreach (EngineConfig engine in new EngineConfig[] { new AnalyticEngineConfig(), Pde, Mc })
        {
            Assert.Equal(0, Price(option, new BlackScholesConfig(), engine, state: state).Pv);
            Assert.Equal(vanilla, Price(option with { Kind = BarrierKind.DownIn }, new BlackScholesConfig(), engine, state: state).Pv, 1);
        }
    }

    [Fact]
    public void WorstOfFullyCorrelatedIdenticalLegsIsTheSingleNote()
    {
        var worst = Worst((A, Spot), (B, Spot)) with { CallBarrier = 1 };
        var single = PhoenixNote(call: 1);
        var model = LocalVol with { Correlation = 1 };
        var basket = Price(worst, model, Mc);
        var mono = Price(single, model, Mc);
        Assert.True(Math.Abs(basket.Pv - mono.Pv) < 4 * Math.Sqrt(Math.Pow(basket.PvStandardError!.Value, 2) + Math.Pow(mono.PvStandardError!.Value, 2)) + 0.02,
            $"{basket.Pv} vs {mono.Pv}");
    }

    [Fact]
    public void WorstOfGainsFromCorrelationAndHasGreeksPerUnderlying()
    {
        // With calls, higher correlation also ends coupons sooner and the sign can flip: test the downside alone.
        var worst = Worst((A, Spot), (B, Spot)) with { CallBarrier = 1000, Protection = 0.9 };
        double Value(double rho) => Price(worst, LocalVol with { Correlation = rho }, Mc).Pv;
        Assert.True(Value(0.2) < Value(0.8));

        var result = Price(worst, LocalVol, Mc with { Paths = 1 << 16 }, measures: [Measure.Delta, Measure.Theta],
            risk: new RiskConfig { SpotBump = 0.01 });
        var a = result.Greek(Measure.Delta, A)!;
        var b = result.Greek(Measure.Delta, B)!;
        Assert.True(a.Value > 0 && b.Value > 0);
        Assert.True(Math.Abs(a.Value - b.Value) < 0.3 * Math.Abs(a.Value), $"{a.Value} vs {b.Value}");
        Assert.Single(result.Greeks, g => g.Measure == Measure.Theta);
    }

    [Fact]
    public void NotesAreReproducibleAndRoundTripThroughJsonAndHash()
    {
        Instrument worst = Worst((A, Spot), (B, 90));
        var copy = JsonSerializer.Deserialize<Instrument>(JsonSerializer.Serialize(worst))!;
        Assert.Equal(ContentHash.Of(worst), ContentHash.Of(copy));
        Assert.Equal(new[] { A, B }, copy.Underlyings.ToArray());

        Assert.Equal(Price(worst, LocalVol, Mc).Pv, Price(worst, LocalVol, Mc).Pv);
        var state = new TradeState { Fixings = [new FixingRecord(A, Today, 100)] };
        Assert.NotEqual(ContentHash.Of(new Trade("T", worst, 1)), ContentHash.Of(new Trade("T", worst, 1, state)));
    }

    [Fact]
    public void ModelComparisonPutsEveryModelInVolUnits()
    {
        var market = Smile();
        var report = SurfaceBuilder.Build(Chain(), SyntheticMarketDataProvider.DiscountCurve).Report;
        var heston = HestonCalibrator.Fit(market, A).Config;

        var fits = ModelComparison.Run(market, A, report, heston, LocalVol);

        Assert.Equal(report.Slices.Length, fits.Length);
        Assert.All(fits, f =>
        {
            Assert.True(f.RmseSvi < 0.002, $"svi {f.RmseSvi}");
            Assert.True(f.RmseLocalVol < 0.01, $"lv {f.RmseLocalVol} at {f.T}");
            Assert.True(f.RmseHeston < 0.03, $"heston {f.RmseHeston} at {f.T}");
        });
    }
}
