using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pricer.Data;
using Pricer.Data.Ibkr;
using Pricer.Data.Yahoo;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.App.ViewModels;

public sealed record SliceRow(string Expiry, double T, double Forward, int Points, double RmseVolPoints, string Butterfly, SliceReport Report);

public sealed partial class MarketViewModel : ObservableObject
{
    private readonly SnapshotStore _store = new(CaptureSettings.DatabasePath);
    private readonly CaptureSettings _settings = CaptureSettings.Load();
    private StoredCapture? _capture;

    [ObservableProperty] private SnapshotInfo? _selectedCapture;
    [ObservableProperty] private CalibrationReport? _selectedReport;
    [ObservableProperty] private SliceRow? _selectedSlice;
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _status = "";

    /// <summary>The snapshot and underlying now selected, for the pricing screen.</summary>
    public event Action<MarketSnapshot, string>? MarketSelected;
    public event Action<SliceReport>? SmileChanged;
    public event Action<SviVolSurface>? SurfaceChanged;

    public ObservableCollection<SnapshotInfo> Captures { get; } = [];
    public ObservableCollection<CalibrationReport> Reports { get; } = [];
    public ObservableCollection<SliceRow> Slices { get; } = [];

    [RelayCommand(IncludeCancelCommand = true)]
    private Task CaptureIbkr(CancellationToken token) => Run(new IbkrOptionChainSource(_settings), _settings, token);

    [RelayCommand]
    private async Task CaptureYahoo()
    {
        using var source = new YahooOptionChainSource();
        try
        {
            await Run(source, await source.PrepareAsync(_settings, default), default);
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private Task CaptureSynthetic() =>
        Run(new SyntheticOptionChainSource(), _settings with { Watchlist = [new ChainRequest("SYNTH")] }, default);

    private async Task Run(IOptionChainSource source, CaptureSettings settings, CancellationToken token)
    {
        var progress = new Progress<string>(message => Status = message);
        try
        {
            var capture = new SnapshotCapture(source, _store);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var outcome = await Task.Run(() => capture.RunAsync(settings, today, progress, token), token);
            Status = outcome.Failures.Length == 0
                ? $"Snapshot {outcome.SnapshotId} saved."
                : $"Snapshot {outcome.SnapshotId} partial. {string.Join(" ", outcome.Failures)}";
            Reload();
        }
        catch (OperationCanceledException)
        {
            Status = "Capture cancelled.";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException)
        {
            Status = e.Message;
        }
    }

    public void Reload()
    {
        Captures.Clear();
        foreach (var info in _store.List()) Captures.Add(info);
        SelectedCapture = Captures.FirstOrDefault();
    }

    partial void OnSelectedCaptureChanged(SnapshotInfo? value)
    {
        _capture = value is null ? null : _store.Load(value.Id);
        Reports.Clear();
        foreach (var report in _capture?.Reports ?? []) Reports.Add(report);
        SelectedReport = Reports.FirstOrDefault();
    }

    partial void OnSelectedReportChanged(CalibrationReport? value)
    {
        Slices.Clear();
        Warnings = "";
        if (value is null || _capture is null) return;

        foreach (var s in value.Slices)
        {
            Slices.Add(new SliceRow(
                s.Expiry.ToString("yyyy-MM-dd"), s.Slice.T, s.Slice.Forward, s.Points.Length, s.RmseVol * 100,
                s.MinButterfly >= 0 ? "OK" : "Violation", s));
        }
        Warnings = string.Join(Environment.NewLine, value.Warnings);
        SelectedSlice = Slices.FirstOrDefault();

        if (_capture.Snapshot.Underlying(value.Underlying).Vol is SviVolSurface surface) SurfaceChanged?.Invoke(surface);
        MarketSelected?.Invoke(_capture.Snapshot, value.Underlying);
    }

    partial void OnSelectedSliceChanged(SliceRow? value)
    {
        if (value is not null) SmileChanged?.Invoke(value.Report);
    }
}
