using System.Collections.Immutable;
using System.Text.Json;
using Pricer.Data;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Tests;

public class WebExportTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 30);

    [Fact]
    public async Task ExportedCaptureReadsBackAndPricesIdentically()
    {
        var capture = await WebExport.SyntheticAsync(AsOf, CancellationToken.None);
        string directory = Path.Combine(Path.GetTempPath(), $"pricer-web-{Guid.NewGuid():N}");
        try
        {
            var entry = Assert.Single(WebExport.Write(directory, [("Synthetic", "Synthetic", capture)]));
            var index = JsonSerializer.Deserialize<List<CaptureIndexEntry>>(File.ReadAllText(Path.Combine(directory, WebExport.IndexFile)));
            Assert.Equal(entry, Assert.Single(index!), new IndexComparer());
            var read = JsonSerializer.Deserialize<StoredCapture>(File.ReadAllText(Path.Combine(directory, entry.File)))!;

            Assert.Equal(["SYNTH", "SYNTH2"], read.Snapshot.Underlyings.Keys);
            Assert.Equal(capture.Reports.Length, read.Reports.Length);
            Assert.Equal(Price(capture.Snapshot), Price(read.Snapshot));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Local vol through the PDE reads the whole SVI surface, so any loss in the round trip shows up.</summary>
    private static double Price(MarketSnapshot snapshot)
    {
        var option = new EuropeanOption { Underlying = "SYNTH", Right = OptionRight.Call, Strike = 100, Expiry = AsOf.AddYears(1) };
        var request = new PricingRequest(
            new Trade("W", option, 1), snapshot, new LocalVolConfig(), new PdeEngineConfig(), AsOf, ImmutableArray<Measure>.Empty);
        return new RiskCalculator(EngineRegistry.Default()).Compute(request, new RiskConfig()).Pv;
    }

    private sealed class IndexComparer : IEqualityComparer<CaptureIndexEntry>
    {
        public bool Equals(CaptureIndexEntry? x, CaptureIndexEntry? y) =>
            x!.File == y!.File && x.Label == y.Label && x.AsOf == y.AsOf && x.Underlyings.SequenceEqual(y.Underlyings);

        public int GetHashCode(CaptureIndexEntry obj) => obj.File.GetHashCode();
    }
}
