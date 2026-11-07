using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pricer.Presentation;
using Pricer.Book;
using Pricer.Data;
using Pricer.Engines;

namespace Pricer.App.ViewModels;

/// <summary>One line of the explain table, for a group or a trade.</summary>
public sealed record PnlLine(string Key, PnlRow Row, string Status)
{
    public double Total => Row.Total;
    public double Time => Row[PnlFactor.Time];
    public double Events => Row[PnlFactor.Events];
    public double Spot => Row[PnlFactor.Spot];
    public double Dividends => Row[PnlFactor.Dividends];
    public double VolLevel => Row[PnlFactor.VolLevel];
    public double VolShape => Row[PnlFactor.VolShape];
    public double Rates => Row[PnlFactor.Rates];
    public double Composition => Row[PnlFactor.Composition];
    public double Residual => Row[PnlFactor.Residual];
    public double GreekResidual => Row.Greeks[(int)PnlFactor.Residual];
}

/// <summary>A factor under both methods, for the side-by-side table.</summary>
public sealed record FactorLine(string Factor, double Waterfall, double Greeks, double Gap);

public sealed partial class PnlViewModel : ObservableObject
{
    private readonly BookStore _book;
    private readonly SnapshotStore _snapshots;
    private PnlExplain? _explain;

    [ObservableProperty] private RunInfo? _from;
    [ObservableProperty] private RunInfo? _to;
    [ObservableProperty] private BookGrouping _grouping = BookGrouping.Underlying;
    [ObservableProperty] private PnlLine? _selectedGroup;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _status = "";

    public PnlViewModel(BookStore book, SnapshotStore snapshots)
    {
        _book = book;
        _snapshots = snapshots;
        Reload();
    }

    public event Action<PnlRow>? WaterfallChanged;

    public Array Groupings { get; } = Enum.GetValues<BookGrouping>();
    public ParameterForm Form { get; } = new("Explain", new PnlConfig());
    public ObservableCollection<RunInfo> Runs { get; } = [];
    public ObservableCollection<PnlLine> Groups { get; } = [];
    public ObservableCollection<PnlLine> Trades { get; } = [];
    public ObservableCollection<FactorLine> Factors { get; } = [];

    /// <summary>Starts from the latest run against the one before it.</summary>
    public void Reload()
    {
        Runs.Clear();
        foreach (var run in _book.Runs()) Runs.Add(run);
        To = Runs.FirstOrDefault();
        From = Runs.Skip(1).FirstOrDefault();
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Explain(CancellationToken token)
    {
        if (From is not { } from || To is not { } to)
        {
            Status = "Two runs are required.";
            return;
        }

        var config = (PnlConfig)Form.Record;
        var progress = new Progress<RunProgress>(p => Status = $"Explain : {p.Done}/{p.Total} trades");
        try
        {
            var explain = await Task.Run(() =>
            {
                var (left, right) = (_book.Load(from.Id), _book.Load(to.Id));
                return new PnlExplainer(EngineRegistry.Default()).ExplainAsync(
                    left, _snapshots.Load(left.SnapshotId).Snapshot, right, _snapshots.Load(right.SnapshotId).Snapshot, config, progress, token);
            }, token);

            _explain = explain;
            Warnings = string.Join(Environment.NewLine, explain.Warnings);
            Refresh();
            Status = $"From {explain.From:yyyy-MM-dd} to {explain.To:yyyy-MM-dd}, {explain.Trades.Length} trades.";
        }
        catch (OperationCanceledException)
        {
            Status = "Explain cancelled.";
        }
        catch (ArgumentException e)
        {
            Status = e.Message;
        }
    }

    partial void OnGroupingChanged(BookGrouping value) => Refresh();

    partial void OnSelectedGroupChanged(PnlLine? value)
    {
        Trades.Clear();
        if (value is null || _explain is null) return;

        foreach (var trade in _explain.Trades.Where(t => BookAggregator.Key(t.Position, Grouping) == value.Key))
        {
            Trades.Add(new PnlLine(trade.Id, PnlAggregator.Row(trade.Id, [trade]), trade.Error ?? ""));
        }
        Show(value.Row);
    }

    private void Refresh()
    {
        Groups.Clear();
        if (_explain is null) return;

        var total = PnlAggregator.Row("Book", _explain.Trades);
        Summary = $"PnL {total.Total:N2}, residual {total[PnlFactor.Residual]:N2} in full revaluation, "
            + $"{total.Greeks[(int)PnlFactor.Residual]:N2} from greeks.";
        foreach (var row in PnlAggregator.Group(_explain.Trades, Grouping))
        {
            Groups.Add(new PnlLine(row.Key, row, row.Failed == 0 ? "" : $"{row.Failed} failed"));
        }
        SelectedGroup = Groups.FirstOrDefault();
    }

    private void Show(PnlRow row)
    {
        Factors.Clear();
        foreach (var factor in Enum.GetValues<PnlFactor>())
        {
            double waterfall = row.Waterfall[(int)factor];
            double greeks = row.Greeks[(int)factor];
            Factors.Add(new FactorLine(factor.ToString(), waterfall, greeks, waterfall - greeks));
        }
        WaterfallChanged?.Invoke(row);
    }
}
