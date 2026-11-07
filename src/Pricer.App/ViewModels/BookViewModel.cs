using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pricer.Presentation;
using Pricer.Book;
using Pricer.Data;
using Pricer.Data.Ibkr;
using Pricer.Engines;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.App.ViewModels;

public sealed record PositionRow(
    Position Position, string Product, string Underlying, string Model, string Engine,
    double? Pv, double? Error, double? CashDelta, double? Vega, string Status)
{
    public string Id => Position.Id;
    public double Quantity => Position.Quantity;
    public string Tag => Position.Tag;
    public string Source => Position.Source;
}

public sealed partial class BookViewModel : ObservableObject
{
    private const int RunForm = 0;
    private const int MonteCarloForm = 1;
    private const int PdeForm = 2;
    private const int LocalVolForm = 3;
    private const int RiskForm = 4;

    private readonly BookStore _store;
    private readonly CaptureSettings _settings = CaptureSettings.Load();
    private IReadOnlyList<Position> _positions = [];
    private RunResult? _run;
    private MarketSnapshot? _snapshot;
    private long _snapshotId;
    private string _underlying = "";

    [ObservableProperty] private BookGrouping _grouping = BookGrouping.Underlying;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private PositionRow? _selectedPosition;
    [ObservableProperty] private string _marketLabel = "Select a snapshot in the Market tab.";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;

    public BookViewModel(BookStore store)
    {
        _store = store;
        Reload();
    }

    public event Action? RunSaved;

    public Array Groupings { get; } = Enum.GetValues<BookGrouping>();
    public ObservableCollection<PositionRow> Rows { get; } = [];
    public ObservableCollection<BookRow> Summary { get; } = [];

    public ObservableCollection<ParameterForm> Forms { get; } =
    [
        new("Run", new RunConfig()),
        new("Monte Carlo", new MonteCarloEngineConfig()),
        new("PDE", new PdeEngineConfig()),
        new("Local vol", new LocalVolConfig()),
        new("Risk (forced bump, market conventions)", new RiskConfig()),
    ];

    public void Use(long snapshotId, MarketSnapshot snapshot, string underlying, string source)
    {
        _snapshotId = snapshotId;
        _snapshot = snapshot;
        _underlying = underlying;
        MarketLabel = $"Snapshot {snapshotId} · {source} · {snapshot.AsOf:yyyy-MM-dd}";
    }

    public void Add(Position position)
    {
        _store.Save([position]);
        Reload();
        Status = $"{position.Id} added to the book.";
    }

    private RunConfig Config => (RunConfig)Forms[RunForm].Record with
    {
        MonteCarlo = (MonteCarloEngineConfig)Forms[MonteCarloForm].Record,
        Pde = (PdeEngineConfig)Forms[PdeForm].Record,
        LocalVol = (LocalVolConfig)Forms[LocalVolForm].Record,
        Risk = (RiskConfig)Forms[RiskForm].Record,
    };

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Run(CancellationToken token)
    {
        if (_snapshot is not { } snapshot)
        {
            Status = "No snapshot selected.";
            return;
        }

        var progress = new Progress<RunProgress>(p => Progress = (double)p.Done / p.Total);
        try
        {
            var engine = new RunEngine(EngineRegistry.Default(), _store);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var config = Config;
            var positions = _positions;
            long snapshotId = _snapshotId;
            var run = await Task.Run(() => engine.ExecuteAsync(positions, snapshot, snapshotId, config, progress, token), token);

            int failed = run.Trades.Count(t => t.Error is not null);
            int cached = run.Trades.Count(t => t.FromCache);
            long id = _store.Save(run, $"{snapshot.AsOf:yyyy-MM-dd} · {run.Trades.Length} trades");
            _run = run;
            Reload();
            Status = $"Run {id}: {run.Trades.Length} trades in {watch.Elapsed.TotalSeconds:F1} s, {cached} from cache, {failed} failed.";
            RunSaved?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Status = "Run cancelled.";
        }
    }

    [RelayCommand]
    private void Demo()
    {
        if (_snapshot is null)
        {
            Status = "No snapshot selected.";
            return;
        }
        _store.Save(DemoBook.For(_snapshot, _underlying));
        Reload();
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedPosition is not { } row) return;
        _store.Delete(row.Id);
        Reload();
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ImportIbkr(CancellationToken token)
    {
        try
        {
            Status = "Reading IBKR positions…";
            var broker = await Task.Run(() => new IbkrPositionProvider(_settings).FetchAsync(token), token);
            var merged = BrokerImport.Merge(_positions, broker, Exercise);
            _store.Replace(merged.Positions);
            Reload();
            string skipped = merged.Skipped.IsEmpty ? "" : $" Skipped: {string.Join(", ", merged.Skipped)}.";
            Status = $"IBKR: {merged.Added} added, {merged.Changed} changed, {merged.Closed} closed.{skipped}";
        }
        catch (OperationCanceledException)
        {
            Status = "Import cancelled.";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException)
        {
            Status = e.Message;
        }
    }

    private ExerciseStyle Exercise(string symbol) =>
        _settings.Watchlist.FirstOrDefault(r => r.Symbol == symbol)?.Exercise ?? ExerciseStyle.American;

    private void Reload()
    {
        _positions = _store.Positions();
        Refresh();
    }

    partial void OnGroupingChanged(BookGrouping value) => Refresh();

    partial void OnFilterChanged(string value) => Refresh();

    private bool Matches(Position position) =>
        Filter.Length == 0
        || new[] { position.Id, position.Tag, position.Source, position.Instrument.GetType().Name, string.Join("+", position.Instrument.Underlyings) }
            .Any(s => s.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    private void Refresh()
    {
        var results = _run?.Trades.ToDictionary(t => t.Position.Id) ?? [];
        Rows.Clear();
        foreach (var position in _positions.Where(Matches))
        {
            Rows.Add(Row(position, results.GetValueOrDefault(position.Id)));
        }

        Summary.Clear();
        var shown = _run?.Trades.Where(t => Matches(t.Position)) ?? [];
        foreach (var row in BookAggregator.Group(shown, Grouping)) Summary.Add(row);
    }

    private static PositionRow Row(Position position, TradeResult? trade)
    {
        string product = position.Instrument.GetType().Name;
        string underlying = string.Join("+", position.Instrument.Underlyings);
        if (trade?.Result is not { } result)
        {
            return new PositionRow(position, product, underlying, "", "", null, null, null, null, trade?.Error ?? "");
        }

        var total = BookAggregator.Group([trade], BookGrouping.Total)[0];
        return new PositionRow(
            position, product, underlying, trade.Model?.GetType().Name.Replace("Config", "") ?? "", result.Engine,
            result.Pv, result.PvStandardError, total.CashDelta, total.Vega, trade.FromCache ? "Cache" : "Computed");
    }
}
