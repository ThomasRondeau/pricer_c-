using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pricer.Book;
using Pricer.Core;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Presentation;

public enum ProductKind
{
    EuropeanOption,
    AmericanOption,
    Digital,
    Barrier,
    Asian,
    ReverseConvertible,
    Autocall,
    Phoenix,
    WorstOf,
    Forward,
    Stock,
}

public enum ModelKind
{
    BlackScholes,
    LocalVol,
    Heston,
}

public enum EngineKind
{
    Analytic,
    Pde,
    MonteCarlo,
}

/// <summary>Manual overrides, layered over the selected snapshot.</summary>
public sealed record MarketInputs
{
    [Parameter("Spot", Group = "Market", Min = 0.2, Max = 3, Relative = true)]
    public double Spot { get; init; }

    [Parameter("Vol shift", Group = "Market", Unit = "abs", Min = -0.15, Max = 0.3)]
    public double VolShift { get; init; }

    [Parameter("Dividend shift", Group = "Market", Unit = "abs", Min = -0.05, Max = 0.05)]
    public double DividendShift { get; init; }

    [Parameter("Curve shift", Group = "Market", Unit = "abs", Min = -0.03, Max = 0.03)]
    public double RateShift { get; init; }

    public MarketSnapshot ApplyTo(MarketSnapshot snapshot, string underlying)
    {
        var market = snapshot.Underlying(underlying);
        return snapshot.WithUnderlying(underlying, new UnderlyingMarket(
            Spot, market.DividendCurve.Shifted(DividendShift), market.Vol.Shifted(VolShift))) with
        {
            DiscountCurve = snapshot.DiscountCurve.Shifted(RateShift),
        };
    }
}

public sealed record PositionInputs
{
    [Parameter("Quantity", Group = "Position", Min = -1000, Max = 1000)]
    public double Quantity { get; init; } = 1;
}

/// <summary>A simplified past for a trade in life: all past fixings at one level.</summary>
public sealed record LifeInputs
{
    [Parameter("Past fixings level", Group = "Trade lifecycle", Unit = "× initial", Min = 0.2, Max = 2)]
    public double PastLevel { get; init; } = 1;

    [Parameter("Barrier already breached", Group = "Trade lifecycle")]
    public bool BarrierBreached { get; init; }

    public TradeState State(Instrument instrument, DateOnly asOf, Func<string, double> spot)
    {
        if (instrument is not (StructuredNote or AsianOption)) return new TradeState { BarrierBreached = BarrierBreached };

        var dates = PayoffCompiler.Compile(instrument, asOf, 0, 0).Events.Select(e => e.Date).Where(d => d < asOf).Distinct();
        return new TradeState
        {
            BarrierBreached = BarrierBreached,
            Fixings = [.. dates.SelectMany(date => instrument.Underlyings.Select(
                name => new FixingRecord(name, date, PastLevel * Initial(instrument, name, spot))))],
        };
    }

    private static double Initial(Instrument instrument, string name, Func<string, double> spot) => instrument switch
    {
        WorstOfPhoenix worst => worst.Basket.First(l => l.Underlying == name).InitialFixing,
        StructuredNote note => note.InitialFixing,
        _ => spot(name),
    };
}

/// <summary>Calculation defaults chosen by the host: a single-threaded browser needs lighter settings than the desktop.</summary>
public sealed record PricerOptions
{
    public MonteCarloEngineConfig MonteCarlo { get; init; } = new();
    public int ProfilePoints { get; init; } = 81;
}

/// <summary>A greek next to its bump-and-reprice control. When the two conventions differ (a local vol grid delta
/// is a model greek, the bump a sticky-strike market greek), the gap measures smile dynamics, not an error.</summary>
public sealed record GreekRow(
    string Measure, double Value, double? Error, string Method, string Convention, double BumpValue, string BumpConvention, double Gap)
{
    public const string ConventionNote = "Different conventions: the gap is the effect of smile dynamics, not a numerical error.";

    public bool SameConvention => Convention == BumpConvention;

    public string GapNote => SameConvention ? "" : $"{Convention} vs {BumpConvention}";
}

public sealed record EngineRow(string Engine, double Pv, double? Error, double Milliseconds);

public sealed record ProfileData(double[] Spots, double[] Values);

public sealed partial class PricerViewModel : ObservableObject
{
    private const int ProductForm = 0;
    private const int PositionForm = 1;
    private const int MarketForm = 2;
    private const int ModelForm = 3;
    private const int EngineForm = 4;
    private const int RiskForm = 5;
    private const int LifeForm = 6;
    private const int PreviewDivisor = 16;
    private const int MinPreviewPaths = 1024;
    private const int PreviewBatches = 8;

    /// <summary>A small bump on a payoff with jumps measures noise, not a sensitivity.</summary>
    private const double NoteSpotBump = 0.01;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(60);
    private static readonly ImmutableArray<Measure> AllMeasures = [.. Enum.GetValues<Measure>()];

    private static readonly Dictionary<Type, EngineKind> EngineKindOf = new()
    {
        [typeof(AnalyticEngineConfig)] = EngineKind.Analytic,
        [typeof(PdeEngineConfig)] = EngineKind.Pde,
        [typeof(MonteCarloEngineConfig)] = EngineKind.MonteCarlo,
    };

    private readonly EngineRegistry _registry = EngineRegistry.Default();
    private readonly PricerOptions _options;
    private readonly RiskCalculator _risk;
    private HestonConfig _heston = new();
    private MarketSnapshot _snapshot;
    private string _underlying = SyntheticMarketDataProvider.DefaultUnderlying;
    private CancellationTokenSource? _pending;

    [ObservableProperty] private ProductKind _productKind = ProductKind.EuropeanOption;
    [ObservableProperty] private IReadOnlyList<ModelKind> _availableModels = [ModelKind.BlackScholes, ModelKind.LocalVol, ModelKind.Heston];
    [ObservableProperty] private ModelKind _selectedModel = ModelKind.BlackScholes;
    [ObservableProperty] private IReadOnlyList<EngineKind> _availableEngines = [EngineKind.Analytic, EngineKind.Pde, EngineKind.MonteCarlo];
    [ObservableProperty] private EngineKind _selectedEngine = EngineKind.Analytic;
    [ObservableProperty] private string _pvText = "";
    [ObservableProperty] private string _marketLabel = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _isBusy;

    public PricerViewModel(PricerOptions? options = null)
    {
        _options = options ?? new PricerOptions();
        _risk = new RiskCalculator(_registry);
        _snapshot = new SyntheticMarketDataProvider().GetSnapshot(DateOnly.FromDateTime(DateTime.Today));
        Forms = [];
        for (int i = 0; i <= LifeForm; i++) Forms.Add(NewForm(i));
        foreach (var form in Forms) form.Changed += Schedule;
        UpdateMarketLabel();
        Schedule();
    }

    public event Action<ProfileData>? ProfileChanged;
    public event Action<Position>? PositionRequested;

    public Array ProductKinds { get; } = Enum.GetValues<ProductKind>();
    public ObservableCollection<ParameterForm> Forms { get; }
    public ObservableCollection<GreekRow> Greeks { get; } = [];
    public ObservableCollection<EngineRow> Engines { get; } = [];

    public void UseMarket(MarketSnapshot snapshot, string underlying)
    {
        _snapshot = snapshot;
        _underlying = underlying;
        Replace(ProductForm);
        Replace(MarketForm);
        UpdateMarketLabel();
        Schedule();
    }

    [RelayCommand]
    private void AddToBook()
    {
        var instrument = (Instrument)Forms[ProductForm].Record;
        var state = ((LifeInputs)Forms[LifeForm].Record).State(instrument, _snapshot.AsOf, name => _snapshot.Underlying(name).Spot);
        var model = SelectedModel switch
        {
            ModelKind.LocalVol => ModelChoice.LocalVol,
            ModelKind.Heston => ModelChoice.Heston,
            _ => ModelChoice.BlackScholes,
        };
        PositionRequested?.Invoke(new Position(
            $"L-{Guid.NewGuid():N}"[..10], instrument, ((PositionInputs)Forms[PositionForm].Record).Quantity,
            Tag: "Pricer", State: state, Model: model));
        Status = "Added to the book.";
    }

    /// <summary>The calibrated parameters take over from the defaults for the Heston model.</summary>
    public void UseHeston(HestonConfig config)
    {
        _heston = config;
        if (SelectedModel != ModelKind.Heston) return;
        Replace(ModelForm);
        Schedule();
    }

    private Type InstrumentType => Forms[ProductForm].Record.GetType();

    private IEnumerable<EngineKind> EnginesFor(ModelKind model) =>
        _registry.ConfigsFor(InstrumentType, ModelType(model)).Select(c => EngineKindOf[c]).Order();

    partial void OnProductKindChanged(ProductKind value)
    {
        Replace(ProductForm);
        Replace(RiskForm);
        AvailableModels = [.. Enum.GetValues<ModelKind>().Where(m => EnginesFor(m).Any())];
        if (AvailableModels.Contains(SelectedModel)) RefreshEngines();
        else SelectedModel = AvailableModels[0];
    }

    partial void OnSelectedModelChanged(ModelKind value)
    {
        Replace(ModelForm);
        RefreshEngines();
    }

    private void RefreshEngines()
    {
        AvailableEngines = [.. EnginesFor(SelectedModel)];
        if (AvailableEngines.Contains(SelectedEngine)) Schedule();
        else SelectedEngine = AvailableEngines[0];
    }

    partial void OnSelectedEngineChanged(EngineKind value)
    {
        Replace(EngineForm);
        Schedule();
    }

    private void UpdateMarketLabel() => MarketLabel = $"{_underlying} as of {_snapshot.AsOf:yyyy-MM-dd}";

    private void Replace(int index)
    {
        Forms[index].Changed -= Schedule;
        Forms[index] = NewForm(index);
        Forms[index].Changed += Schedule;
    }

    private ParameterForm NewForm(int index) => index switch
    {
        ProductForm => new("Product", NewInstrument()),
        PositionForm => new("Position", new PositionInputs()),
        MarketForm => new("Market (overrides)", new MarketInputs { Spot = _snapshot.Underlying(_underlying).Spot }),
        ModelForm => new($"Model: {SelectedModel}", NewModel(SelectedModel)),
        EngineForm => new($"Engine: {SelectedEngine}", NewEngine(SelectedEngine)),
        RiskForm => new("Risk", ProductKind >= ProductKind.ReverseConvertible && ProductKind <= ProductKind.WorstOf
            ? new RiskConfig { SpotBump = NoteSpotBump }
            : new RiskConfig()),
        LifeForm => new("Trade lifecycle", new LifeInputs()),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private static Type ModelType(ModelKind kind) => kind switch
    {
        ModelKind.LocalVol => typeof(LocalVolConfig),
        ModelKind.Heston => typeof(HestonConfig),
        _ => typeof(BlackScholesConfig),
    };

    private ModelConfig NewModel(ModelKind kind) => kind switch
    {
        ModelKind.LocalVol => new LocalVolConfig(),
        ModelKind.Heston => _heston,
        _ => new BlackScholesConfig(),
    };

    private EngineConfig NewEngine(EngineKind kind) => kind switch
    {
        EngineKind.Pde => new PdeEngineConfig(),
        EngineKind.MonteCarlo => _options.MonteCarlo,
        _ => new AnalyticEngineConfig(),
    };

    /// <summary>The profile reprices many times, so Monte Carlo runs lighter there.</summary>
    private static EngineConfig Preview(EngineConfig config) => config is MonteCarloEngineConfig mc
        ? mc with { Paths = Math.Max(mc.Paths / PreviewDivisor, MinPreviewPaths), Batches = PreviewBatches }
        : config;

    private Instrument NewInstrument()
    {
        double strike = Math.Round(_snapshot.Underlying(_underlying).Spot);
        var expiry = _snapshot.AsOf.AddYears(1);
        double spot = _snapshot.Underlying(_underlying).Spot;
        var start = _snapshot.AsOf;
        return ProductKind switch
        {
            ProductKind.EuropeanOption => new EuropeanOption
            {
                Underlying = _underlying, Right = OptionRight.Call, Strike = strike, Expiry = expiry,
            },
            ProductKind.AmericanOption => new AmericanOption
            {
                Underlying = _underlying, Right = OptionRight.Put, Strike = strike, Expiry = expiry,
            },
            ProductKind.Digital => new DigitalOption
            {
                Underlying = _underlying, Right = OptionRight.Call, Strike = strike, Expiry = expiry,
            },
            ProductKind.Barrier => new BarrierOption
            {
                Underlying = _underlying, Right = OptionRight.Call, Strike = strike, Expiry = expiry,
                Barrier = Math.Round(0.8 * strike), Kind = BarrierKind.DownOut,
            },
            ProductKind.Asian => new AsianOption
            {
                Underlying = _underlying, Right = OptionRight.Call, Strike = strike, Expiry = expiry,
                AveragingStart = _snapshot.AsOf.AddMonths(1),
            },
            ProductKind.ReverseConvertible => new ReverseConvertible
            {
                Underlying = _underlying, InitialFixing = spot, Start = start, Expiry = expiry, Protection = 0.8,
            },
            ProductKind.Autocall => new Autocall
            {
                Underlying = _underlying, InitialFixing = spot, Start = start, Expiry = start.AddYears(3),
            },
            ProductKind.Phoenix => new Phoenix
            {
                Underlying = _underlying, InitialFixing = spot, Start = start, Expiry = start.AddYears(3),
            },
            ProductKind.WorstOf => new WorstOfPhoenix
            {
                Underlying = _underlying, InitialFixing = spot, Start = start, Expiry = start.AddYears(3),
                Basket = [.. _snapshot.Underlyings.Keys.OrderBy(n => n != _underlying)
                    .Select(n => new BasketLeg(n, _snapshot.Underlying(n).Spot))],
            },
            ProductKind.Forward => new EquityForward { Underlying = _underlying, Strike = strike, Expiry = expiry },
            ProductKind.Stock => new Stock { Underlying = _underlying },
            _ => throw new InvalidOperationException(),
        };
    }

    private sealed record Output(
        PricingResult Best, PricingResult Bumped, ProfileData Profile, ImmutableArray<EngineRow> Engines, string Hash, TimeSpan Elapsed);

    private async void Schedule()
    {
        _pending?.Cancel();
        var pending = _pending = new CancellationTokenSource();
        var token = pending.Token;

        var market = ((MarketInputs)Forms[MarketForm].Record).ApplyTo(_snapshot, _underlying);
        var instrument = (Instrument)Forms[ProductForm].Record;
        var life = (LifeInputs)Forms[LifeForm].Record;
        var state = life.State(instrument, market.AsOf, name => market.Underlying(name).Spot);
        var trade = new Trade("UI", instrument, ((PositionInputs)Forms[PositionForm].Record).Quantity, state);
        var request = new PricingRequest(
            trade, market, (ModelConfig)Forms[ModelForm].Record, (EngineConfig)Forms[EngineForm].Record, market.AsOf, AllMeasures);
        var config = (RiskConfig)Forms[RiskForm].Record;
        string underlying = _underlying;
        var others = AvailableEngines.Where(e => e != SelectedEngine).Select(NewEngine).ToArray();

        IsBusy = true;
        try
        {
            await Task.Delay(Debounce, token);
            var output = await Task.Run(() => CalculateAsync(request, config, underlying, others, token), token);
            Show(output);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            HasError = true;
            Status = e.Message;
        }
        finally
        {
            // A newer edit owns the flag once it has started.
            if (_pending == pending) IsBusy = false;
        }
    }

    /// <summary>Yields between reprices: in a single-threaded browser this lets the page redraw and a new edit cancel the run.</summary>
    private async Task<Output> CalculateAsync(
        PricingRequest request, RiskConfig config, string underlying, EngineConfig[] others, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var best = _risk.Compute(request, config);
        double bestTime = watch.Elapsed.TotalMilliseconds;
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        // When every greek already comes from bumping, the control would only repeat the calculation.
        var bumped = best.Greeks.All(g => g.Method == GreekMethod.BumpReprice)
            ? best
            : _risk.Compute(request, config with { Preference = GreekPreference.ForceBump });

        double spot = request.Market.Underlying(underlying).Spot;
        int points = _options.ProfilePoints;
        var spots = new double[points];
        var values = new double[points];
        var pvOnly = request with { Measures = [] };
        var preview = pvOnly with { Engine = Preview(request.Engine) };
        for (int i = 0; i < points; i++)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            double relative = -0.5 + i / (points - 1.0);
            spots[i] = spot * (1 + relative);
            var shifted = new SpotBump(underlying, relative).Apply(request.Market);
            values[i] = _risk.Compute(preview with { Market = shifted }, config).Pv;
        }

        var engines = ImmutableArray.CreateBuilder<EngineRow>();
        engines.Add(new EngineRow(best.Engine, best.Pv, best.PvStandardError, bestTime));
        foreach (var other in others)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = _risk.Compute(pvOnly with { Engine = other }, config);
            engines.Add(new EngineRow(result.Engine, result.Pv, result.PvStandardError, timer.Elapsed.TotalMilliseconds));
        }

        return new Output(
            best, bumped, new ProfileData(spots, values), engines.ToImmutable(), ContentHash.Of(new { request, config }), watch.Elapsed);
    }

    private void Show(Output output)
    {
        PvText = output.Best.PvStandardError is > 0 and var error
            ? $"{output.Best.Pv:N4} ± {error:N4}"
            : output.Best.Pv.ToString("N4");
        HasError = false;
        string realised = output.Best.Realised == 0 ? "" : $" · already paid {output.Best.Realised:N2}";
        Status = $"{output.Best.Engine} · {output.Elapsed.TotalMilliseconds:F1} ms · hash {output.Hash[..12]}{realised}";

        Greeks.Clear();
        foreach (var greek in output.Best.Greeks)
        {
            var bump = output.Bumped.Greek(greek.Measure, greek.Underlying)!;
            Greeks.Add(new GreekRow(
                $"{greek.Measure} {greek.Underlying}".TrimEnd(), greek.Value, greek.StandardError, greek.Method.ToString(), greek.Convention.ToString(),
                bump.Value, bump.Convention.ToString(), greek.Value - bump.Value));
        }

        Engines.Clear();
        foreach (var row in output.Engines) Engines.Add(row);
        ProfileChanged?.Invoke(output.Profile);
    }
}
