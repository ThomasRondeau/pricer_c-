using Pricer.Core;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Book;

public enum ModelChoice
{
    BlackScholes,
    LocalVol,
    Heston,
}

/// <param name="Source">Local for trades entered here, IBKR for imported ones.</param>
/// <param name="Model">Overrides the run's choice for this trade.</param>
public sealed record Position(
    string Id, Instrument Instrument, double Quantity, string Source = Position.LocalSource, string Tag = "",
    TradeState? State = null, ModelChoice? Model = null)
{
    public const string LocalSource = "Local";

    public Trade ToTrade() => new(Id, Instrument, Quantity, State);
}

/// <summary>Everything a run depends on besides its positions and its snapshot.
/// Scenario shifts apply to every underlying, before any calibration.</summary>
public sealed record RunConfig
{
    [Parameter("Vanilla model", Group = "Run")]
    public ModelChoice VanillaModel { get; init; } = ModelChoice.BlackScholes;

    [Parameter("Exotic model", Group = "Run")]
    public ModelChoice ExoticModel { get; init; } = ModelChoice.LocalVol;

    [Parameter("Structured model", Group = "Run")]
    public ModelChoice NoteModel { get; init; } = ModelChoice.LocalVol;

    [Parameter("Scenario: spot shock", Group = "Run", Unit = "rel", Min = -0.5, Max = 0.5)]
    public double SpotShock { get; init; }

    [Parameter("Scenario: vol shock", Group = "Run", Unit = "abs", Min = -0.1, Max = 0.3)]
    public double VolShock { get; init; }

    /// <summary>A small bump on a payoff with jumps measures noise, not a sensitivity.</summary>
    [Parameter("Spot bump for discontinuous payoffs", Group = "Run", Unit = "rel", Min = 0.001, Max = 0.05)]
    public double DiscontinuousSpotBump { get; init; } = 0.01;

    public PdeEngineConfig Pde { get; init; } = new();
    public MonteCarloEngineConfig MonteCarlo { get; init; } = new();
    public LocalVolConfig LocalVol { get; init; } = new();
    public RiskConfig Risk { get; init; } = new();

    public MarketSnapshot Shocked(MarketSnapshot snapshot)
    {
        if (SpotShock == 0 && VolShock == 0) return snapshot;
        foreach (string name in snapshot.Underlyings.Keys)
        {
            snapshot = new VolBump(name, VolShock).Apply(new SpotBump(name, SpotShock).Apply(snapshot));
        }
        return snapshot;
    }

    public ModelChoice ModelFor(Position position) => position.Model ?? position.Instrument switch
    {
        StructuredNote => NoteModel,
        BarrierOption or DigitalOption or AsianOption => ExoticModel,
        _ => VanillaModel,
    };

    /// <summary>Book greeks must add up across models, so they are always bumped in market space.</summary>
    public RiskConfig RiskFor(Instrument instrument) =>
        Risk with
        {
            Preference = GreekPreference.ForceBump,
            SpotBump = instrument is StructuredNote or BarrierOption or DigitalOption
                ? Math.Max(Risk.SpotBump, DiscontinuousSpotBump)
                : Risk.SpotBump,
        };
}
