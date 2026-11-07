using System.Collections.Immutable;
using System.Text.Json;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Data;

public sealed record CaptureSettings
{
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>7497 is TWS paper trading.</summary>
    public int Port { get; init; } = 7497;

    public int ClientId { get; init; } = 17;

    /// <summary>IBKR market data type. 4 (delayed-frozen) returns the best data the account allows, also after the close.</summary>
    public int MarketDataType { get; init; } = 4;

    public ImmutableArray<ChainRequest> Watchlist { get; init; } =
    [
        new("SPX", "IND", "CBOE", "USD", ExerciseStyle.European),
    ];

    public ImmutableArray<ChainRequest> YahooWatchlist { get; init; } =
    [
        new("^SPX", "IND", "CBOE", "USD", ExerciseStyle.European),
        new("SPY", "STK", "SMART", "USD", ExerciseStyle.American),
    ];

    /// <summary>IBKR provides no discount curve: these are placeholder rates until set by hand.</summary>
    public YieldCurve DiscountCurve { get; init; } = SyntheticMarketDataProvider.DiscountCurve;

    public bool DiscountCurveConfirmed { get; init; }

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pricer");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "pricer.db");

    private static string SettingsPath { get; } = Path.Combine(DataDirectory, "capture-settings.json");

    /// <summary>Creates the settings file with defaults on first use.</summary>
    public static CaptureSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            return JsonSerializer.Deserialize<CaptureSettings>(File.ReadAllText(SettingsPath)) ?? new CaptureSettings();
        }
        var defaults = new CaptureSettings();
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
        return defaults;
    }
}

public sealed record CaptureOutcome(long SnapshotId, ImmutableArray<CalibrationReport> Reports, ImmutableArray<string> Failures);

/// <summary>Fetches each watchlist chain, calibrates it and stores everything as one snapshot.
/// One failing underlying does not lose the others.</summary>
public sealed class SnapshotCapture(IOptionChainSource source, SnapshotStore store)
{
    public async Task<CaptureOutcome> RunAsync(
        CaptureSettings settings, DateOnly asOf, IProgress<string>? progress, CancellationToken token)
    {
        var chains = new List<OptionChain>();
        var reports = new List<CalibrationReport>();
        var failures = new List<string>();
        var underlyings = ImmutableSortedDictionary<string, UnderlyingMarket>.Empty;

        foreach (var request in settings.Watchlist)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var chain = await source.FetchAsync(request, asOf, progress, token);
                chains.Add(chain);
                var built = SurfaceBuilder.Build(chain, settings.DiscountCurve);
                var report = settings.DiscountCurveConfirmed
                    ? built.Report
                    : built.Report with { Warnings = built.Report.Warnings.Add("Default discount curve, not confirmed.") };
                reports.Add(report);
                underlyings = underlyings.SetItem(request.Symbol, built.Market);
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException)
            {
                failures.Add($"{request.Symbol}: {e.Message}");
            }
        }

        if (chains.Count == 0)
        {
            throw new InvalidOperationException("No chain captured. " + string.Join(" ", failures));
        }
        // Raw chains are saved even when calibration failed: they cannot be fetched again.
        var marketDate = chains.Max(c => c.AsOf);
        long id = store.Save(new MarketSnapshot(marketDate, settings.DiscountCurve, underlyings), chains, reports, source.Name);
        return new CaptureOutcome(id, [.. reports], [.. failures]);
    }
}
