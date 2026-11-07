using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Market;

namespace Pricer.Models;

public sealed record SmilePoint(double Strike, double Vol);

public sealed record SliceReport(
    DateOnly Expiry, SviSlice Slice, double RmseVol, double MinButterfly, ImmutableArray<SmilePoint> Points);

public sealed record CalibrationReport(
    string Underlying, ImmutableArray<SliceReport> Slices, int CalendarViolations, ImmutableArray<string> Warnings);

public sealed record SurfaceBuildResult(UnderlyingMarket Market, CalibrationReport Report);

/// <summary>Turns a raw option chain into forwards and an SVI surface, and reports the fit quality.</summary>
public static class SurfaceBuilder
{
    private const int ForwardStrikes = 7;
    private const int MinForwardPairs = 3;
    private const double MaxRelativeSpread = 0.5;
    private const double MinVolSpread = 0.002;
    private const double ButterflyTolerance = -1e-6;
    private const double CalendarTolerance = -1e-9;

    public static SurfaceBuildResult Build(OptionChain chain, YieldCurve discountCurve)
    {
        var slices = new List<SliceReport>();
        var warnings = new List<string>();
        if (chain.Exercise == ExerciseStyle.American)
        {
            warnings.Add("American options: forwards and vols approximated with a European model.");
        }

        foreach (var group in chain.Quotes.Where(q => q.IsTwoSided).GroupBy(q => q.Expiry).OrderBy(g => g.Key))
        {
            double t = DayCount.Act365F.YearFraction(chain.AsOf, group.Key);
            if (t <= 0) continue;
            double df = discountCurve.Discount(t);

            if (ImpliedForward([.. group], chain.Spot, df) is not { } forward)
            {
                warnings.Add($"{group.Key:yyyy-MM-dd} skipped: put-call parity unusable.");
                continue;
            }

            var points = OutOfTheMoneyVols(group, forward, t, df);
            if (points.Count < SviCalibrator.MinPoints)
            {
                warnings.Add($"{group.Key:yyyy-MM-dd} skipped: only {points.Count} usable points.");
                continue;
            }

            double[] ks = [.. points.Select(p => Math.Log(p.Strike / forward))];
            double[] vols = [.. points.Select(p => p.Vol)];
            double[] weights = [.. points.Select(p => 1 / Math.Pow(Math.Max(p.Spread, MinVolSpread), 2))];
            double scale = weights.Max();
            var slice = SviCalibrator.Fit(t, forward, ks, vols, [.. weights.Select(w => w / scale)]);

            double rmse = Math.Sqrt(ks.Zip(vols, (k, v) => Math.Pow(Math.Sqrt(slice.TotalVariance(k) / t) - v, 2)).Average());
            double minDensity = Grid(ks.Min(), ks.Max()).Min(slice.ButterflyDensity);
            if (minDensity < ButterflyTolerance)
            {
                warnings.Add($"{group.Key:yyyy-MM-dd}: butterfly arbitrage in the quoted range.");
            }
            slices.Add(new SliceReport(group.Key, slice, rmse, minDensity, [.. points.Select(p => new SmilePoint(p.Strike, p.Vol))]));
        }

        if (slices.Count == 0) throw new InvalidOperationException($"{chain.Underlying}: no expiry calibrated.");

        int calendar = CountCalendarViolations(slices);
        if (calendar > 0) warnings.Add($"{calendar} expiry pair(s) in calendar arbitrage.");

        var surface = new SviVolSurface([.. slices.Select(s => s.Slice)]);
        var carry = new YieldCurve(
            [.. slices.Select(s => s.Slice.T)],
            [.. slices.Select(s => discountCurve.ZeroRate(s.Slice.T) - Math.Log(s.Slice.Forward / chain.Spot) / s.Slice.T)]);

        return new SurfaceBuildResult(
            new UnderlyingMarket(chain.Spot, carry, surface),
            new CalibrationReport(chain.Underlying, [.. slices], calendar, [.. warnings]));
    }

    /// <summary>Median of K + (C - P) / DF over the strikes nearest to spot.</summary>
    public static double? ImpliedForward(IReadOnlyCollection<OptionQuote> quotes, double spot, double discount)
    {
        var estimates = quotes.Where(q => q.IsCall)
            .Join(quotes.Where(q => !q.IsCall), c => c.Strike, p => p.Strike, (c, p) => (c.Strike, Value: c.Mid - p.Mid))
            .OrderBy(x => Math.Abs(x.Strike - spot))
            .Take(ForwardStrikes)
            .Select(x => x.Strike + x.Value / discount)
            .Order()
            .ToArray();

        if (estimates.Length < MinForwardPairs) return null;
        int middle = estimates.Length / 2;
        return estimates.Length % 2 == 1 ? estimates[middle] : (estimates[middle - 1] + estimates[middle]) / 2;
    }

    private sealed record VolPoint(double Strike, double Vol, double Spread);

    private static List<VolPoint> OutOfTheMoneyVols(IEnumerable<OptionQuote> quotes, double forward, double t, double df)
    {
        var points = new List<VolPoint>();
        foreach (var q in quotes.Where(q => q.IsCall == q.Strike >= forward).OrderBy(q => q.Strike))
        {
            if ((q.Ask - q.Bid) / q.Mid > MaxRelativeSpread) continue;
            if (ImpliedVol.Solve(q.Mid, forward, q.Strike, t, df, q.IsCall) is not { } vol) continue;
            double? bid = ImpliedVol.Solve(q.Bid, forward, q.Strike, t, df, q.IsCall);
            double? ask = ImpliedVol.Solve(q.Ask, forward, q.Strike, t, df, q.IsCall);
            points.Add(new VolPoint(q.Strike, vol, bid is { } b && ask is { } a ? a - b : MinVolSpread));
        }
        return points;
    }

    private static int CountCalendarViolations(List<SliceReport> slices)
    {
        int violations = 0;
        for (int i = 1; i < slices.Count; i++)
        {
            var (near, far) = (slices[i - 1].Slice, slices[i].Slice);
            double low = Math.Log(slices[i - 1].Points[0].Strike / near.Forward);
            double high = Math.Log(slices[i - 1].Points[^1].Strike / near.Forward);
            if (Grid(low, high).Any(k => far.TotalVariance(k) - near.TotalVariance(k) < CalendarTolerance)) violations++;
        }
        return violations;
    }

    private static IEnumerable<double> Grid(double low, double high) =>
        Enumerable.Range(0, 41).Select(i => low + (high - low) * i / 40.0);
}
