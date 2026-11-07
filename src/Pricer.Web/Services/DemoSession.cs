using Pricer.Engines;
using Pricer.Models;
using Pricer.Presentation;
using Pricer.Risk;

namespace Pricer.Web.Services;

/// <summary>The market chosen in the header, pushed to both pages. Same wiring as the desktop MainViewModel.</summary>
public sealed class DemoSession
{
    private readonly CaptureCatalog _catalog;
    private Task? _initialization;

    public DemoSession(CaptureCatalog catalog)
    {
        _catalog = catalog;
        ModelLab.HestonFitted += Pricer.UseHeston;
        // Kept here so that a page shows the last chart again when the visitor comes back to it.
        Pricer.ProfileChanged += profile => { Profile = profile; Changed?.Invoke(); };
        ModelLab.FitChanged += fit => { Fit = fit; Changed?.Invoke(); };
    }

    public event Action? Changed;

    /// <summary>The browser runs on one thread: a quarter of the desktop paths and half the profile points keep
    /// path-dependent products interactive. Visitors can still raise the paths in the engine form.</summary>
    private static readonly PricerOptions BrowserOptions = new()
    {
        MonteCarlo = new MonteCarloEngineConfig { Paths = 1 << 14 },
        ProfilePoints = 41,
    };

    public PricerViewModel Pricer { get; } = new(BrowserOptions);
    public ModelLabViewModel ModelLab { get; } = new();
    public IReadOnlyList<CaptureIndexEntry> Captures { get; private set; } = [];
    public CaptureIndexEntry? Capture { get; private set; }
    public string Underlying { get; private set; } = "";
    public string? Error { get; private set; }
    public ProfileData? Profile { get; private set; }
    public SliceFit? Fit { get; private set; }

    public Task InitializeAsync() => _initialization ??= LoadFirstAsync();

    public async Task SelectAsync(CaptureIndexEntry entry, string? underlying = null)
    {
        try
        {
            var capture = await _catalog.LoadAsync(entry);
            string name = underlying is not null && entry.Underlyings.Contains(underlying) ? underlying : entry.Underlyings[0];
            var report = capture.Reports.First(r => r.Underlying == name);
            Capture = entry;
            Underlying = name;
            Error = null;
            Fit = null;
            Pricer.UseMarket(capture.Snapshot, name);
            ModelLab.Use(capture.Snapshot, name, report);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        {
            Error = $"Could not load {entry.Label}: {e.Message}";
        }
        Changed?.Invoke();
    }

    /// <summary>Opens on real market data when there is some: it makes a better first impression than a synthetic market.</summary>
    private async Task LoadFirstAsync()
    {
        try
        {
            Captures = await _catalog.ListAsync();
        }
        catch (HttpRequestException e)
        {
            Error = $"Could not load the capture list: {e.Message}";
            Changed?.Invoke();
            return;
        }
        var first = Captures.FirstOrDefault(c => c.Source != "Synthetic") ?? Captures.FirstOrDefault();
        if (first is not null) await SelectAsync(first);
    }
}
