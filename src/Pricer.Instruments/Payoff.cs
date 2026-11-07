using System.Collections.Immutable;

namespace Pricer.Instruments;

public enum BinaryOp
{
    Add,
    Subtract,
    Multiply,
    Max,
    Min,
}

/// <summary>An expression evaluated on each path at an event date.</summary>
public abstract record PayoffExpr
{
    public abstract bool IsContinuous { get; }

    public static implicit operator PayoffExpr(double value) => new ConstantExpr(value);
    public static PayoffExpr operator +(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Add, a, b);
    public static PayoffExpr operator -(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Subtract, a, b);
    public static PayoffExpr operator *(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Multiply, a, b);
    public static PayoffExpr Max(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Max, a, b);
    public static PayoffExpr Min(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Min, a, b);
}

public sealed record ConstantExpr(double Value) : PayoffExpr
{
    public override bool IsContinuous => true;
}

public sealed record SpotExpr(int Asset = 0) : PayoffExpr
{
    public override bool IsContinuous => true;
}

/// <summary>A per-path variable kept between events: running average, knock-in flag, memorised coupon.</summary>
public sealed record SlotExpr(int Index) : PayoffExpr
{
    public override bool IsContinuous => true;
}

public sealed record BinaryExpr(BinaryOp Op, PayoffExpr Left, PayoffExpr Right) : PayoffExpr
{
    public override bool IsContinuous => Left.IsContinuous && Right.IsContinuous;
}

/// <summary>1 when Argument is positive or zero, else 0. Scale sizes the smoothing used for pathwise greeks.</summary>
public sealed record StepExpr(PayoffExpr Argument, double Scale) : PayoffExpr
{
    public override bool IsContinuous => false;
}

/// <summary>A payoff is a dated list of events. Structured products need no engine of their own:
/// an autocall is Pay and Terminate on the same condition at each observation.</summary>
public abstract record PayoffEvent(DateOnly Date)
{
    public abstract bool IsContinuous { get; }
}

public sealed record StoreEvent(DateOnly Date, int Slot, PayoffExpr Value) : PayoffEvent(Date)
{
    public override bool IsContinuous => Value.IsContinuous;
}

public sealed record PayEvent(DateOnly Date, PayoffExpr Amount) : PayoffEvent(Date)
{
    public override bool IsContinuous => Amount.IsContinuous;
}

/// <summary>Ends the paths where Condition is 1.</summary>
public sealed record TerminateEvent(DateOnly Date, PayoffExpr Condition) : PayoffEvent(Date)
{
    public override bool IsContinuous => Condition.IsContinuous;
}

public sealed record PayoffProgram(ImmutableArray<PayoffEvent> Events, int Slots)
{
    public bool IsContinuous => Events.All(e => e.IsContinuous);
}

/// <param name="Slots">Variables as of the end of the valuation date.</param>
/// <param name="Realised">Cash paid strictly before the valuation date, per unit of trade.</param>
/// <param name="DueToday">Cash paid on the valuation date; still part of the value.</param>
/// <param name="Remaining">Events left to simulate; none when the trade has ended.</param>
public sealed record ReplayResult(double[] Slots, double Realised, double DueToday, ImmutableArray<PayoffEvent> Remaining);

/// <summary>Runs a program over its past: this is the whole of the trade's life cycle, with no logic
/// specific to a product. A fixing missing on the valuation date falls back on the current spot.</summary>
public static class PayoffReplay
{
    public static ReplayResult Run(
        PayoffProgram program, ImmutableArray<string> underlyings, DateOnly valuation, TradeState? state, Func<string, double> spot)
    {
        var fixings = (state?.Fixings ?? []).ToDictionary(f => (f.Underlying, f.Date), f => f.Level);
        double[] slots = new double[program.Slots];
        double realised = 0, dueToday = 0;
        bool alive = true;

        double Level(string underlying, DateOnly date)
        {
            if (fixings.TryGetValue((underlying, date), out double level)) return level;
            return date == valuation
                ? spot(underlying)
                : throw new InvalidOperationException($"Missing fixing: {underlying} on {date:yyyy-MM-dd}.");
        }

        double Evaluate(PayoffExpr expr, DateOnly date) => expr switch
        {
            ConstantExpr c => c.Value,
            SpotExpr s => Level(underlyings[s.Asset], date),
            SlotExpr s => slots[s.Index],
            StepExpr s => Evaluate(s.Argument, date) >= 0 ? 1 : 0,
            BinaryExpr b => Apply(b.Op, Evaluate(b.Left, date), Evaluate(b.Right, date)),
            _ => throw new NotSupportedException(expr.GetType().Name),
        };

        var ordered = program.Events.OrderBy(e => e.Date).ToArray();
        int next = 0;
        for (; next < ordered.Length && ordered[next].Date <= valuation && alive; next++)
        {
            switch (ordered[next])
            {
                case StoreEvent e: slots[e.Slot] = Evaluate(e.Value, e.Date); break;
                case TerminateEvent e: alive = Evaluate(e.Condition, e.Date) == 0; break;
                case PayEvent e:
                    double amount = Evaluate(e.Amount, e.Date);
                    if (e.Date < valuation) realised += amount;
                    else dueToday += amount;
                    break;
            }
        }
        return new ReplayResult(slots, realised, dueToday, alive ? [.. ordered[next..]] : []);
    }

    private static double Apply(BinaryOp op, double a, double b) => op switch
    {
        BinaryOp.Add => a + b,
        BinaryOp.Subtract => a - b,
        BinaryOp.Multiply => a * b,
        BinaryOp.Max => Math.Max(a, b),
        BinaryOp.Min => Math.Min(a, b),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
}

public static class PayoffCompiler
{
    /// <summary>Broadie-Glasserman-Kou: a daily check misses crossings, so the barrier moves towards spot to mimic continuous monitoring.</summary>
    private const double MonitoringShift = 0.5826;
    private const double DaysPerYear = 365.0;

    /// <param name="vol">Lognormal vol, used only to correct the barrier for discrete monitoring.</param>
    public static PayoffProgram Compile(
        Instrument instrument, DateOnly valuation, double spot, double vol, TradeState? state = null) => instrument switch
    {
        EuropeanOption o => new([new PayEvent(o.Expiry, Vanilla(o))], 0),
        DigitalOption o => new([new PayEvent(o.Expiry, Digital(o))], 0),
        AsianOption o => Asian(o),
        BarrierOption o => Barrier(o, valuation, spot, vol, state?.BarrierBreached ?? false),
        ReverseConvertible n => ReverseConvertibleProgram(n),
        Autocall n => AutocallProgram(n),
        Phoenix n => PhoenixProgram(n),
        _ => throw new NotSupportedException($"No path payoff for {instrument.GetType().Name}."),
    };

    /// <summary>Value on the expiry date itself, where no simulation or grid is needed.</summary>
    public static double AtExpiry(OptionInstrument option, double spot) => option switch
    {
        DigitalOption d => d.Sign * (spot - d.Strike) >= 0 ? d.Cash : 0,
        BarrierOption b when b.IsOut == b.IsBreachedAt(spot) => 0,
        _ => Math.Max(option.Sign * (spot - option.Strike), 0),
    };

    private static PayoffExpr Vanilla(OptionInstrument o) => PayoffExpr.Max(o.Sign * (new SpotExpr() - o.Strike), 0.0);

    private static PayoffExpr Digital(DigitalOption o) =>
        (PayoffExpr)o.Cash * new StepExpr((PayoffExpr)o.Sign * (new SpotExpr() - o.Strike), o.Strike);

    private static PayoffProgram Asian(AsianOption o)
    {
        if (o.Fixings < 1) throw new ArgumentException("At least one fixing is required.");

        var events = ImmutableArray.CreateBuilder<PayoffEvent>();
        var slot = new SlotExpr(0);
        double span = o.Expiry.DayNumber - o.AveragingStart.DayNumber;
        for (int i = 0; i < o.Fixings; i++)
        {
            double offset = o.Fixings == 1 ? span : span * i / (o.Fixings - 1);
            var date = o.AveragingStart.AddDays((int)Math.Round(offset));
            events.Add(new StoreEvent(date, 0, slot + new SpotExpr()));
        }
        var average = (PayoffExpr)(1.0 / o.Fixings) * slot;
        events.Add(new PayEvent(o.Expiry, PayoffExpr.Max(o.Sign * (average - o.Strike), 0.0)));
        return new(events.ToImmutable(), 1);
    }

    private static PayoffExpr Performance(StructuredNote note)
    {
        if (note is WorstOfPhoenix worst)
        {
            return worst.Basket.Select((leg, i) => (PayoffExpr)(1 / leg.InitialFixing) * new SpotExpr(i)).Aggregate(PayoffExpr.Min);
        }
        return (PayoffExpr)(1 / note.InitialFixing) * new SpotExpr();
    }

    /// <summary>1 if the performance is at or above a barrier.</summary>
    private static PayoffExpr Above(PayoffExpr performance, double barrier) => new StepExpr(performance - barrier, 1);

    private static PayoffExpr Redemption(StructuredNote note, PayoffExpr performance)
    {
        var safe = Above(performance, note.Protection);
        return (PayoffExpr)note.Notional * (safe + ((PayoffExpr)1.0 - safe) * performance);
    }

    private static PayoffProgram ReverseConvertibleProgram(ReverseConvertible n)
    {
        var events = ImmutableArray.CreateBuilder<PayoffEvent>();
        foreach (var date in n.Observations()) events.Add(new PayEvent(date, n.Notional * n.Coupon));
        events.Add(new PayEvent(n.Expiry, Redemption(n, Performance(n))));
        return new(events.ToImmutable(), 0);
    }

    private static PayoffProgram AutocallProgram(Autocall n)
    {
        var performance = Performance(n);
        var called = Above(performance, n.CallBarrier);
        var dates = n.Observations();
        var events = ImmutableArray.CreateBuilder<PayoffEvent>();
        for (int i = 0; i < dates.Length; i++)
        {
            events.Add(new PayEvent(dates[i], (PayoffExpr)(n.Notional * (1 + (i + 1) * n.Coupon)) * called));
            if (i < dates.Length - 1) events.Add(new TerminateEvent(dates[i], called));
        }
        events.Add(new PayEvent(n.Expiry, ((PayoffExpr)1.0 - called) * Redemption(n, performance)));
        return new(events.ToImmutable(), 0);
    }

    private static PayoffProgram PhoenixProgram(Phoenix n)
    {
        var performance = Performance(n);
        var earned = Above(performance, n.CouponBarrier);
        var called = Above(performance, n.CallBarrier);
        var missed = new SlotExpr(0);
        var dates = n.Observations();
        var events = ImmutableArray.CreateBuilder<PayoffEvent>();
        for (int i = 0; i < dates.Length; i++)
        {
            PayoffExpr periods = n.Memory ? (PayoffExpr)1.0 + missed : 1.0;
            events.Add(new PayEvent(dates[i], (PayoffExpr)(n.Notional * n.Coupon) * earned * periods));
            events.Add(new StoreEvent(dates[i], 0, ((PayoffExpr)1.0 - earned) * ((PayoffExpr)1.0 + missed)));
            if (i < dates.Length - 1)
            {
                events.Add(new PayEvent(dates[i], (PayoffExpr)n.Notional * called));
                events.Add(new TerminateEvent(dates[i], called));
            }
        }
        events.Add(new PayEvent(n.Expiry, Redemption(n, performance)));
        return new(events.ToImmutable(), 1);
    }

    private static PayoffProgram Barrier(BarrierOption o, DateOnly valuation, double spot, double vol, bool breached)
    {
        if (breached || o.IsBreachedAt(spot))
        {
            return o.IsOut ? new([], 0) : new([new PayEvent(o.Expiry, Vanilla(o))], 0);
        }

        double shift = Math.Exp((o.IsUp ? -1 : 1) * MonitoringShift * vol * Math.Sqrt(1 / DaysPerYear));
        double level = o.Barrier * shift;
        var distance = o.IsUp ? new SpotExpr() - level : (PayoffExpr)level - new SpotExpr();
        var hit = new StepExpr(distance, o.Barrier);

        var events = ImmutableArray.CreateBuilder<PayoffEvent>();
        var flag = new SlotExpr(0);
        for (var date = valuation.AddDays(1); date <= o.Expiry; date = date.AddDays(1))
        {
            events.Add(o.IsOut ? new TerminateEvent(date, hit) : new StoreEvent(date, 0, PayoffExpr.Max(flag, hit)));
        }
        events.Add(new PayEvent(o.Expiry, o.IsOut ? Vanilla(o) : flag * Vanilla(o)));
        return new(events.ToImmutable(), o.IsOut ? 0 : 1);
    }
}
