using Pricer.Core;

namespace Pricer.Risk;

public enum GreekPreference
{
    /// <summary>Use the engine's own greeks, bump what is missing.</summary>
    BestAvailable,

    /// <summary>Bump everything, e.g. to check an engine against the reference method.</summary>
    ForceBump,
}

public sealed record RiskConfig
{
    [Parameter("Greek method", Group = "Risk")]
    public GreekPreference Preference { get; init; } = GreekPreference.BestAvailable;

    [Parameter("Spot bump", Group = "Risk", Unit = "rel", Min = 0.0001, Max = 0.05)]
    public double SpotBump { get; init; } = 0.001;

    [Parameter("Vol bump", Group = "Risk", Unit = "abs", Min = 0.0001, Max = 0.02)]
    public double VolBump { get; init; } = 0.001;

    [Parameter("Rate bump", Group = "Risk", Unit = "abs", Min = 0.00001, Max = 0.01)]
    public double RateBump { get; init; } = 0.0001;
}
