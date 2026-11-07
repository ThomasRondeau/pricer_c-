using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Pricer.Market;

namespace Pricer.Data.Yahoo;

/// <summary>Delayed quotes from Yahoo's unofficial endpoints, for tests while TWS is unavailable.
/// Not a production feed: no guarantee of service, and the spot and option closes are not synchronous.</summary>
public sealed class YahooOptionChainSource : IOptionChainSource, IDisposable
{
    private const string UserAgent = "Mozilla/5.0";
    private const string QuoteUrl = "https://query1.finance.yahoo.com/v7/finance/quote";
    private const string OptionsUrl = "https://query2.finance.yahoo.com/v7/finance/options/";
    private static readonly TimeSpan Pacing = TimeSpan.FromMilliseconds(300);

    /// <summary>Treasury yield indices, in percent, by maturity in years.</summary>
    private static readonly (string Symbol, double T)[] Treasuries = [("^IRX", 0.25), ("^FVX", 5), ("^TNX", 10), ("^TYX", 30)];

    private readonly HttpClient _http = new(new HttpClientHandler { CookieContainer = new CookieContainer() })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private string? _crumb;

    public YahooOptionChainSource() => _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

    public string Name => "Yahoo";

    public void Dispose() => _http.Dispose();

    /// <summary>The settings for a Yahoo capture: its watchlist, and a curve read from Treasury yields.</summary>
    public async Task<CaptureSettings> PrepareAsync(CaptureSettings settings, CancellationToken token)
    {
        string symbols = string.Join(',', Treasuries.Select(x => x.Symbol));
        using var document = await GetAsync($"{QuoteUrl}?symbols={Uri.EscapeDataString(symbols)}", token);
        var yields = document.RootElement.GetProperty("quoteResponse").GetProperty("result").EnumerateArray()
            .ToDictionary(q => q.GetProperty("symbol").GetString()!, q => q.GetProperty("regularMarketPrice").GetDouble());

        var pillars = Treasuries.Where(x => yields.ContainsKey(x.Symbol)).ToArray();
        if (pillars.Length == 0) throw new InvalidOperationException("Yahoo: no Treasury rate received.");
        return settings with
        {
            Watchlist = settings.YahooWatchlist,
            DiscountCurve = new YieldCurve(
                [.. pillars.Select(x => x.T)],
                [.. pillars.Select(x => ContinuousRate(yields[x.Symbol] / 100))]),
            DiscountCurveConfirmed = true,
        };
    }

    public async Task<OptionChain> FetchAsync(
        ChainRequest request, DateOnly asOf, IProgress<string>? progress, CancellationToken token)
    {
        string url = OptionsUrl + Uri.EscapeDataString(request.Symbol);
        using var first = await GetAsync(url, token);
        var root = ChainRoot(first, request.Symbol);

        var quote = root.GetProperty("quote");
        double spot = quote.GetProperty("regularMarketPrice").GetDouble();
        // Outside market hours the quotes are the last close: pricing them as of today would shift every maturity.
        var marketDate = Date(quote.GetProperty("regularMarketTime").GetInt64());
        progress?.Report($"{request.Symbol}: spot {spot:F2} on {marketDate:yyyy-MM-dd}");

        var listed = root.GetProperty("expirationDates").EnumerateArray().Select(e => e.GetInt64())
            .ToDictionary(Date, e => e);
        var quotes = new List<OptionQuote>();
        foreach (var expiry in ChainFilter.Expiries(listed.Keys, marketDate))
        {
            await Task.Delay(Pacing, token);
            using var page = await GetAsync($"{url}?date={listed[expiry].ToString(CultureInfo.InvariantCulture)}", token);
            double t = (expiry.DayNumber - marketDate.DayNumber) / 365.0;
            quotes.AddRange(ParseQuotes(ChainRoot(page, request.Symbol), expiry)
                .Where(q => ChainFilter.IsNearSpot(q.Strike, spot, t)));
            progress?.Report($"{request.Symbol}: {expiry:yyyy-MM-dd}, {quotes.Count} quotes");
        }

        if (!quotes.Any(q => q.IsTwoSided))
        {
            throw new InvalidOperationException($"{request.Symbol}: no bid/ask quote (market closed or empty quotes).");
        }
        return new OptionChain(request.Symbol, marketDate, spot, request.Exercise, [.. quotes]);
    }

    /// <summary>Reads one expiry of an options response. Missing bids and asks become zero.</summary>
    public static ImmutableArray<OptionQuote> ParseQuotes(JsonElement chainRoot, DateOnly expiry)
    {
        var option = chainRoot.GetProperty("options")[0];
        return [.. Read(option.GetProperty("calls"), true), .. Read(option.GetProperty("puts"), false)];

        IEnumerable<OptionQuote> Read(JsonElement rows, bool isCall) => rows.EnumerateArray().Select(row => new OptionQuote(
            expiry, row.GetProperty("strike").GetDouble(), isCall, Number(row, "bid"), Number(row, "ask")));
    }

    private static double Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    /// <summary>Yields are quoted semi-annually compounded.</summary>
    private static double ContinuousRate(double yield) => 2 * Math.Log(1 + yield / 2);

    private static DateOnly Date(long unixSeconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);

    private static JsonElement ChainRoot(JsonDocument document, string symbol)
    {
        var results = document.RootElement.GetProperty("optionChain").GetProperty("result");
        return results.GetArrayLength() > 0
            ? results[0]
            : throw new InvalidOperationException($"{symbol}: symbol unknown to Yahoo.");
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken token)
    {
        try
        {
            _crumb ??= await CrumbAsync(token);
            string separator = url.Contains('?') ? "&" : "?";
            using var response = await _http.GetAsync($"{url}{separator}crumb={Uri.EscapeDataString(_crumb)}", token);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException($"Yahoo unreachable: {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"Unreadable Yahoo response: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Yahoo is not responding.", e);
        }
    }

    /// <summary>The endpoints require a session cookie and its matching crumb. The cookie page itself answers 404.</summary>
    private async Task<string> CrumbAsync(CancellationToken token)
    {
        using (await _http.GetAsync("https://fc.yahoo.com", token))
        {
        }
        using var response = await _http.GetAsync("https://query1.finance.yahoo.com/v1/test/getcrumb", token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }
}
