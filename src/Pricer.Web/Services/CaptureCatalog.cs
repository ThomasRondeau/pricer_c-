using System.Net.Http.Json;
using Pricer.Models;

namespace Pricer.Web.Services;

/// <summary>Static market captures shipped under wwwroot/data, exported by <c>Pricer.Capture --export-web</c>.</summary>
public sealed class CaptureCatalog(HttpClient http)
{
    private const string Folder = "data/";
    private readonly Dictionary<string, StoredCapture> _loaded = [];

    public async Task<IReadOnlyList<CaptureIndexEntry>> ListAsync() =>
        await http.GetFromJsonAsync<List<CaptureIndexEntry>>(Folder + "index.json") ?? [];

    public async Task<StoredCapture> LoadAsync(CaptureIndexEntry entry)
    {
        if (_loaded.TryGetValue(entry.File, out var capture)) return capture;
        capture = await http.GetFromJsonAsync<StoredCapture>(Folder + entry.File)
            ?? throw new InvalidDataException($"Empty capture file {entry.File}.");
        _loaded[entry.File] = capture;
        return capture;
    }
}
