using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Pricer.Core;

namespace Pricer.Instruments;

public enum OptionRight
{
    Call,
    Put,
}

public enum BarrierKind
{
    DownOut,
    DownIn,
    UpOut,
    UpIn,
}

[JsonPolymorphic]
[JsonDerivedType(typeof(Stock), "stock")]
[JsonDerivedType(typeof(EquityForward), "forward")]
[JsonDerivedType(typeof(EuropeanOption), "european")]
[JsonDerivedType(typeof(AmericanOption), "american")]
[JsonDerivedType(typeof(DigitalOption), "digital")]
[JsonDerivedType(typeof(BarrierOption), "barrier")]
[JsonDerivedType(typeof(AsianOption), "asian")]
[JsonDerivedType(typeof(ReverseConvertible), "reverse-convertible")]
[JsonDerivedType(typeof(Autocall), "autocall")]
[JsonDerivedType(typeof(Phoenix), "phoenix")]
[JsonDerivedType(typeof(WorstOfPhoenix), "worst-of-phoenix")]
public abstract record Instrument
{
    public required string Underlying { get; init; }

    /// <summary>All underlyings, in the order of the asset indices of the payoff.</summary>
    [JsonIgnore]
    public virtual ImmutableArray<string> Underlyings => [Underlying];
}

/// <summary>A product priced from paths or grids rather than from spot alone.</summary>
public interface IDatedProduct
{
    DateOnly Expiry { get; }

    /// <summary>Strike at which a single-vol model reads the surface.</summary>
    double ReferenceStrike { get; }
}

/// <summary>An option with a single strike and expiry.</summary>
public abstract record OptionInstrument : Instrument, IDatedProduct
{
    [Parameter("Type", Group = "Product")]
    public required OptionRight Right { get; init; }

    [Parameter("Strike", Group = "Product", Min = 0.2, Max = 3, Relative = true)]
    public required double Strike { get; init; }

    [Parameter("Expiry", Group = "Product")]
    public required DateOnly Expiry { get; init; }

    public double Sign => Right == OptionRight.Call ? 1 : -1;

    [JsonIgnore]
    public double ReferenceStrike => Strike;
}

public sealed record Stock : Instrument;

public sealed record EquityForward : Instrument
{
    [Parameter("Strike", Group = "Product", Min = 0.2, Max = 3, Relative = true)]
    public required double Strike { get; init; }

    [Parameter("Expiry", Group = "Product")]
    public required DateOnly Expiry { get; init; }
}

public sealed record EuropeanOption : OptionInstrument;

public sealed record AmericanOption : OptionInstrument;

/// <summary>Pays Cash at expiry if the option finishes in the money.</summary>
public sealed record DigitalOption : OptionInstrument
{
    [Parameter("Payout", Group = "Product", Min = 0, Max = 1000)]
    public double Cash { get; init; } = 1;
}

/// <summary>Continuously monitored, without rebate.</summary>
public sealed record BarrierOption : OptionInstrument
{
    [Parameter("Barrier", Group = "Product", Min = 0.2, Max = 3, Relative = true)]
    public required double Barrier { get; init; }

    [Parameter("Barrier type", Group = "Product")]
    public required BarrierKind Kind { get; init; }

    public bool IsUp => Kind is BarrierKind.UpOut or BarrierKind.UpIn;
    public bool IsOut => Kind is BarrierKind.DownOut or BarrierKind.UpOut;
    public bool IsBreachedAt(double spot) => IsUp ? spot >= Barrier : spot <= Barrier;
}

/// <summary>Arithmetic average of equally spaced fixings, from AveragingStart to Expiry included.</summary>
public sealed record AsianOption : OptionInstrument
{
    [Parameter("Averaging start", Group = "Product")]
    public required DateOnly AveragingStart { get; init; }

    [Parameter("Number of fixings", Group = "Product", Min = 1, Max = 260)]
    public int Fixings { get; init; } = 12;
}

/// <summary>A note on a performance relative to initial fixings. Barriers and coupons are fractions of
/// the initial fixing and of the notional; below the protection barrier at expiry, the notional follows the performance.</summary>
public abstract record StructuredNote : Instrument, IDatedProduct
{
    [Parameter("Nominal", Group = "Product", Min = 1, Max = 100000)]
    public double Notional { get; init; } = 100;

    [Parameter("Initial fixing", Group = "Product", Min = 0.2, Max = 3, Relative = true)]
    public required double InitialFixing { get; init; }

    [Parameter("Start date", Group = "Product")]
    public required DateOnly Start { get; init; }

    [Parameter("Expiry", Group = "Product")]
    public required DateOnly Expiry { get; init; }

    [Parameter("Period", Group = "Product", Unit = "months", Min = 1, Max = 12)]
    public int PeriodMonths { get; init; } = 3;

    [Parameter("Coupon per period", Group = "Product", Unit = "rel", Min = 0, Max = 0.2)]
    public double Coupon { get; init; } = 0.02;

    [Parameter("Protection barrier", Group = "Product", Unit = "rel", Min = 0.2, Max = 1.2)]
    public double Protection { get; init; } = 0.6;

    [JsonIgnore]
    public double ReferenceStrike => InitialFixing * Protection;

    /// <summary>Dates from Start by whole periods; the last one is always Expiry.</summary>
    public ImmutableArray<DateOnly> Observations()
    {
        if (PeriodMonths < 1) throw new ArgumentException("The period must last at least one month.");
        var dates = ImmutableArray.CreateBuilder<DateOnly>();
        for (int i = 1; Start.AddMonths(i * PeriodMonths) < Expiry; i++) dates.Add(Start.AddMonths(i * PeriodMonths));
        dates.Add(Expiry);
        return dates.ToImmutable();
    }
}

/// <summary>Pays its coupon unconditionally. Called "barrier reverse convertible" with protection under 1.</summary>
public sealed record ReverseConvertible : StructuredNote;

/// <summary>Redeemed early at the first observation above the call barrier, with the coupons of all elapsed periods.</summary>
public sealed record Autocall : StructuredNote
{
    [Parameter("Autocall barrier", Group = "Product", Unit = "rel", Min = 0.5, Max = 1.5)]
    public double CallBarrier { get; init; } = 1;
}

/// <summary>Pays a conditional coupon, with those missed before when memory is on, and can be called.</summary>
public record Phoenix : StructuredNote
{
    [Parameter("Autocall barrier", Group = "Product", Unit = "rel", Min = 0.5, Max = 1.5)]
    public double CallBarrier { get; init; } = 1;

    [Parameter("Coupon barrier", Group = "Product", Unit = "rel", Min = 0.2, Max = 1.2)]
    public double CouponBarrier { get; init; } = 0.7;

    [Parameter("Memory effect", Group = "Product")]
    public bool Memory { get; init; } = true;
}

public sealed record BasketLeg(string Underlying, double InitialFixing);

/// <summary>A Phoenix on the worst performance of a basket. Underlying and InitialFixing repeat the first leg.</summary>
public sealed record WorstOfPhoenix : Phoenix
{
    public required ImmutableArray<BasketLeg> Basket { get; init; }

    [JsonIgnore]
    public override ImmutableArray<string> Underlyings => [.. Basket.Select(l => l.Underlying)];
}

/// <summary>What has happened to a trade so far. Everything else is rebuilt by replaying its payoff on the fixings.</summary>
public sealed record TradeState
{
    public ImmutableArray<FixingRecord> Fixings { get; init; } = [];

    /// <summary>A continuous barrier cannot be replayed from fixings, so its crossing is recorded.</summary>
    public bool BarrierBreached { get; init; }
}

public sealed record FixingRecord(string Underlying, DateOnly Date, double Level);

public sealed record Trade(string Id, Instrument Instrument, double Quantity, TradeState? State = null);
