using Pricer.Data;
using Pricer.Data.Ibkr;
using Pricer.Data.Yahoo;

// Daily capture, meant for Windows Task Scheduler. Exit code 1 means nothing was stored.
var settings = CaptureSettings.Load();

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    // --export-web <directory> [--ids 1,2] [--as-of yyyy-MM-dd]: static data for the web demo, read-only on the database.
    int export = Array.IndexOf(args, "--export-web");
    if (export >= 0)
    {
        string directory = args.ElementAtOrDefault(export + 1) ?? throw new ArgumentException("--export-web needs a directory.");
        long[] ids = Option("--ids")?.Split(',').Select(long.Parse).ToArray() ?? [];
        var database = new SnapshotStore(CaptureSettings.DatabasePath);
        var captures = new List<(string, string, Pricer.Models.StoredCapture)>();
        var skipped = new List<string>();
        foreach (long id in ids)
        {
            var info = database.List().Single(s => s.Id == id);
            var rebuilt = WebExport.Rebuild(database, id, skipped);
            captures.Add(($"{info.Source} · {info.AsOf:yyyy-MM-dd} · {string.Join(", ", rebuilt.Snapshot.Underlyings.Keys)}", info.Source, rebuilt));
        }
        var asOf = Option("--as-of") is { } date ? DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)
            : captures.Count > 0 ? captures[0].Item3.Snapshot.AsOf : DateOnly.FromDateTime(DateTime.Today);
        captures.Insert(0, ($"Synthetic SVI · {asOf:yyyy-MM-dd}", "Synthetic", await WebExport.SyntheticAsync(asOf, cancel.Token)));

        foreach (var entry in WebExport.Write(directory, captures))
        {
            Console.WriteLine($"{entry.File}: {entry.Label}");
        }
        foreach (string line in skipped) Console.WriteLine($"  skipped {line}");
        return 0;
    }

    using var yahoo = new YahooOptionChainSource();
    IOptionChainSource source = new IbkrOptionChainSource(settings);
    if (args.Contains("--synthetic"))
    {
        source = new SyntheticOptionChainSource();
        settings = settings with { Watchlist = [new ChainRequest("SYNTH")] };
    }
    else if (args.Contains("--yahoo"))
    {
        source = yahoo;
        settings = await yahoo.PrepareAsync(settings, cancel.Token);
    }

    var store = new SnapshotStore(CaptureSettings.DatabasePath);
    var capture = new SnapshotCapture(source, store);
    var progress = new Progress<string>(Console.WriteLine);
    var outcome = await capture.RunAsync(settings, DateOnly.FromDateTime(DateTime.Today), progress, cancel.Token);

    Console.WriteLine($"Snapshot {outcome.SnapshotId} saved to {CaptureSettings.DatabasePath}");
    foreach (var report in outcome.Reports)
    {
        Console.WriteLine($"{report.Underlying}: {report.Slices.Length} expiries");
        foreach (var slice in report.Slices)
        {
            Console.WriteLine($"  {slice.Expiry:yyyy-MM-dd}  F={slice.Slice.Forward,10:F2}  {slice.Points.Length,3} pts  RMSE {slice.RmseVol * 100:F2} vol pts");
        }
        foreach (string warning in report.Warnings) Console.WriteLine($"  ! {warning}");
    }
    foreach (string failure in outcome.Failures) Console.WriteLine($"FAILED {failure}");
    return 0;
}
catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 ? args.ElementAtOrDefault(index + 1) : null;
}
