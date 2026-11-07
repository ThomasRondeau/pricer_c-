using Pricer.Market;
using Pricer.Models;

namespace Pricer.Data;

public sealed record ChainRequest(
    string Symbol, string SecType = "STK", string Exchange = "SMART", string Currency = "USD",
    ExerciseStyle Exercise = ExerciseStyle.American);

public interface IOptionChainSource
{
    string Name { get; }
    Task<OptionChain> FetchAsync(ChainRequest request, DateOnly asOf, IProgress<string>? progress, CancellationToken token);
}

/// <summary>Quotes generated from a known SVI surface with bid-ask noise. Lets the whole capture
/// and calibration path run, and be checked against the true surface, without TWS.</summary>
public sealed class SyntheticOptionChainSource(int seed = 7) : IOptionChainSource
{
    private const double Spot = 100;
    private const double DividendYield = 0.02;
    private static readonly int[] Days = [30, 60, 90, 180, 365, 730];

    public string Name => "Synthetic";

    public static SviSlice TrueSlice(double t, double forward)
    {
        double root = Math.Sqrt(t);
        return new SviSlice(t, forward, 0.02 * t, 0.08 * root, -0.5, 0.02, 0.1 + 0.1 * root);
    }

    public Task<OptionChain> FetchAsync(ChainRequest request, DateOnly asOf, IProgress<string>? progress, CancellationToken token)
    {
        var random = new Random(seed);
        var quotes = new List<OptionQuote>();
        foreach (int days in Days)
        {
            double t = days / 365.0;
            double df = SyntheticMarketDataProvider.DiscountCurve.Discount(t);
            double rate = -Math.Log(df) / t;
            double forward = Spot * Math.Exp(-DividendYield * t) / df;
            var slice = TrueSlice(t, forward);

            for (double strike = 70; strike <= 130; strike += 2.5)
            {
                double vol = Math.Sqrt(slice.TotalVariance(Math.Log(strike / forward)) / t);
                foreach (bool isCall in new[] { true, false })
                {
                    double price = BlackScholes.Evaluate(new(forward, strike, t, vol, rate, rate), isCall).Price;
                    double spread = Math.Max(0.02, 0.02 * price);
                    double mid = price + (random.NextDouble() - 0.5) * 0.2 * spread;
                    quotes.Add(new OptionQuote(asOf.AddDays(days), strike, isCall, mid - spread / 2, mid + spread / 2));
                }
            }
        }
        return Task.FromResult(new OptionChain(request.Symbol, asOf, Spot, ExerciseStyle.European, [.. quotes]));
    }
}
