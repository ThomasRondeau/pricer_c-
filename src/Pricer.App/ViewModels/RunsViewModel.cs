using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pricer.Book;
using Pricer.Data;

namespace Pricer.App.ViewModels;

/// <summary>History of runs and the diff of any two of them, down to the trade and the parameter.</summary>
public sealed partial class RunsViewModel : ObservableObject
{
    private readonly BookStore _store;

    [ObservableProperty] private RunInfo? _left;
    [ObservableProperty] private RunInfo? _right;
    [ObservableProperty] private TradeDifference? _selectedTrade;
    [ObservableProperty] private string _summary = "";

    public RunsViewModel(BookStore store)
    {
        _store = store;
        Reload();
    }

    public ObservableCollection<RunInfo> Runs { get; } = [];
    public ObservableCollection<string> ConfigChanges { get; } = [];
    public ObservableCollection<TradeDifference> Trades { get; } = [];
    public ObservableCollection<string> Causes { get; } = [];

    /// <summary>Shows the latest run against the one before it.</summary>
    public void Reload()
    {
        Runs.Clear();
        foreach (var run in _store.Runs()) Runs.Add(run);
        Right = Runs.FirstOrDefault();
        Left = Runs.Skip(1).FirstOrDefault();
    }

    partial void OnLeftChanged(RunInfo? value) => Compare();

    partial void OnRightChanged(RunInfo? value) => Compare();

    partial void OnSelectedTradeChanged(TradeDifference? value)
    {
        Causes.Clear();
        foreach (string cause in value?.Causes ?? []) Causes.Add(cause);
    }

    private void Compare()
    {
        ConfigChanges.Clear();
        Trades.Clear();
        Summary = "";
        if (Left is not { } left || Right is not { } right) return;

        var comparison = RunComparer.Compare(_store.Load(left.Id), _store.Load(right.Id));
        foreach (string change in comparison.ConfigChanges) ConfigChanges.Add(change);
        foreach (var trade in comparison.Trades) Trades.Add(trade);
        Summary = $"PV {comparison.LeftPv:N2} → {comparison.RightPv:N2} ({comparison.RightPv - comparison.LeftPv:+#,0.00;-#,0.00;0}), "
            + $"{comparison.Trades.Length} trades differ.";
        SelectedTrade = Trades.FirstOrDefault();
    }
}
