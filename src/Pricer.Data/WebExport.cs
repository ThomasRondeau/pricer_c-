using System.Collections.Immutable;
using System.Text.Json;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Data;

/// <summary>Writes captures as static JSON files for the web demo: an index plus one file per capture.</summary>
public static class WebExport
{
    public const string IndexFile = "index.json";

    private static readonly string[] SyntheticUnderlyings = [SyntheticMarketDataProvider.DefaultUnderlying, "SYNTH2"];

    /// <summary>Two underlyings from the synthetic SVI source, so that worst-of products have a basket.</summary>
    public static async Task<StoredCapture> SyntheticAsync(DateOnly asOf, CancellationToken token)
    {
        var curve = SyntheticMarketDataProvider.DiscountCurve;
        var underlyings = ImmutableSortedDictionary<string, UnderlyingMarket>.Empty;
        var reports = ImmutableArray.CreateBuilder<CalibrationReport>();
        for (int i = 0; i < SyntheticUnderlyings.Length; i++)
        {
            var chain = await new SyntheticOptionChainSource(seed: 7 + i).FetchAsync(new ChainRequest(SyntheticUnderlyings[i]), asOf, null, token);
            var built = SurfaceBuilder.Build(chain, curve);
            underlyings = underlyings.SetItem(chain.Underlying, built.Market);
            reports.Add(built.Report);
        }
        return new StoredCapture(new MarketSnapshot(asOf, curve, underlyings), reports.ToImmutable());
    }

    /// <summary>Recalibrates every stored chain of a capture with the current code, so that reports and
    /// warnings match this version. Chains that no longer calibrate are left out.</summary>
    public static StoredCapture Rebuild(SnapshotStore store, long id, ICollection<string> skipped)
    {
        var stored = store.Load(id);
        var underlyings = ImmutableSortedDictionary<string, UnderlyingMarket>.Empty;
        var reports = ImmutableArray.CreateBuilder<CalibrationReport>();
        foreach (string name in store.ChainUnderlyings(id))
        {
            try
            {
                var built = SurfaceBuilder.Build(store.LoadChain(id, name), stored.Snapshot.DiscountCurve);
                underlyings = underlyings.SetItem(name, built.Market);
                reports.Add(built.Report);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                skipped.Add($"{name}: {e.Message}");
            }
        }
        return new StoredCapture(stored.Snapshot with { Underlyings = underlyings }, reports.ToImmutable());
    }

    public static IReadOnlyList<CaptureIndexEntry> Write(string directory, IEnumerable<(string Label, string Source, StoredCapture Capture)> captures)
    {
        Directory.CreateDirectory(directory);
        var entries = new List<CaptureIndexEntry>();
        foreach (var (label, source, capture) in captures)
        {
            string file = $"capture-{entries.Count + 1}.json";
            File.WriteAllText(Path.Combine(directory, file), JsonSerializer.Serialize(capture));
            entries.Add(new CaptureIndexEntry(file, label, capture.Snapshot.AsOf, source, [.. capture.Snapshot.Underlyings.Keys]));
        }
        File.WriteAllText(Path.Combine(directory, IndexFile), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        return entries;
    }
}
