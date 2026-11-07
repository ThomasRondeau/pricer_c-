using System.Collections.Immutable;

namespace Pricer.Market;

public interface IMarketDataProvider
{
    MarketSnapshot GetSnapshot(DateOnly asOf);
}

/// <summary>Fixed market for tests and offline work; identical on every call.</summary>
public sealed class SyntheticMarketDataProvider : IMarketDataProvider
{
    public const string DefaultUnderlying = "SYNTH";

    public static YieldCurve DiscountCurve { get; } =
        new([0.0, 1.0, 2.0, 5.0, 10.0], [0.030, 0.031, 0.032, 0.034, 0.036]);

    public MarketSnapshot GetSnapshot(DateOnly asOf) => new(
        asOf,
        DiscountCurve,
        ImmutableSortedDictionary<string, UnderlyingMarket>.Empty
            .Add(DefaultUnderlying, new UnderlyingMarket(100.0, YieldCurve.Flat(0.02), new FlatVolSurface(0.20))));
}
