using System.Globalization;
using IBApi;
using Pricer.Market;

namespace Pricer.Data.Ibkr;

/// <summary>Captures a filtered chain: IBKR limits concurrent lines and message rate, so only the
/// expiries nearest to fixed tenors and strikes around spot are requested.</summary>
public sealed class IbkrOptionChainSource(CaptureSettings settings) : IOptionChainSource
{
    private const string DateFormat = "yyyyMMdd";
    private const int MaxConcurrent = 80;
    private const int StrikesPerExpiry = 25;
    private static readonly TimeSpan PacingDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuoteTimeout = TimeSpan.FromSeconds(15);

    public string Name => "IBKR";

    public async Task<OptionChain> FetchAsync(
        ChainRequest request, DateOnly asOf, IProgress<string>? progress, CancellationToken token)
    {
        using var connection = await IbkrConnection.ConnectAsync(settings, token);

        var underlying = new Contract
        {
            Symbol = request.Symbol, SecType = request.SecType, Exchange = request.Exchange, Currency = request.Currency,
        };
        int conId = await connection.ContractIdAsync(underlying, LookupTimeout, token);
        double spot = Spot(await connection.SnapshotAsync(underlying, QuoteTimeout, token), request.Symbol);
        progress?.Report($"{request.Symbol}: spot {spot:F2}");

        var parameters = await connection.OptionParametersAsync(request.Symbol, request.SecType, conId, LookupTimeout, token);
        var contracts = SelectContracts(request, parameters, spot, asOf);
        progress?.Report($"{request.Symbol}: {contracts.Count} options to quote");

        using var gate = new SemaphoreSlim(MaxConcurrent);
        int done = 0;
        var tasks = new List<Task<OptionQuote?>>();
        foreach (var contract in contracts)
        {
            await gate.WaitAsync(token);
            tasks.Add(Quote(contract));
            await Task.Delay(PacingDelay, token);
        }

        async Task<OptionQuote?> Quote(Contract contract)
        {
            try
            {
                var ticks = await connection.SnapshotAsync(contract, QuoteTimeout, token);
                int count = Interlocked.Increment(ref done);
                if (count % 50 == 0) progress?.Report($"{request.Symbol}: {count}/{contracts.Count}");
                if (ticks.Bid <= 0 || ticks.Ask <= 0) return null;
                var expiry = DateOnly.ParseExact(contract.LastTradeDateOrContractMonth, DateFormat, CultureInfo.InvariantCulture);
                return new OptionQuote(expiry, contract.Strike, contract.Right == "C", ticks.Bid, ticks.Ask);
            }
            finally
            {
                gate.Release();
            }
        }

        var quotes = (await Task.WhenAll(tasks)).OfType<OptionQuote>().ToList();
        if (quotes.Count == 0)
        {
            throw new InvalidOperationException(
                $"{request.Symbol}: no bid/ask received on {contracts.Count} options (market closed or data not permitted).");
        }
        progress?.Report($"{request.Symbol}: {quotes.Count}/{contracts.Count} quotes received");
        return new OptionChain(request.Symbol, asOf, spot, request.Exercise, [.. quotes]);
    }

    private static double Spot(MarketTicks ticks, string symbol)
    {
        if (ticks.Bid > 0 && ticks.Ask > 0) return (ticks.Bid + ticks.Ask) / 2;
        if (ticks.Last > 0) return ticks.Last;
        if (ticks.Close > 0) return ticks.Close;
        throw new InvalidOperationException($"{symbol}: no price received for the underlying.");
    }

    private static List<Contract> SelectContracts(
        ChainRequest request, IReadOnlyList<OptionParameters> parameters, double spot, DateOnly asOf)
    {
        var smart = parameters.Where(p => p.Exchange == "SMART" && p.Multiplier == "100").ToList();
        if (smart.Count == 0) throw new InvalidOperationException($"{request.Symbol}: no SMART chain.");

        // The same expiry can exist in several trading classes (SPX, SPXW): prefer the standard one.
        var expiries = smart.OrderBy(p => p.TradingClass == request.Symbol ? 1 : 0)
            .SelectMany(p => p.Expirations.Select(e => (Date: DateOnly.ParseExact(e, DateFormat, CultureInfo.InvariantCulture), Text: e, Class: p)))
            .GroupBy(e => e.Date)
            .ToDictionary(g => g.Key, g => g.Last());

        var contracts = new List<Contract>();
        foreach (var date in ChainFilter.Expiries(expiries.Keys, asOf))
        {
            var expiry = expiries[date];
            double t = (date.DayNumber - asOf.DayNumber) / 365.0;
            var strikes = expiry.Class.Strikes
                .Where(k => ChainFilter.IsNearSpot(k, spot, t))
                .Order()
                .ToArray();
            double step = Math.Max(1.0, strikes.Length / (double)StrikesPerExpiry);

            for (double i = 0; i < strikes.Length; i += step)
            {
                foreach (string right in new[] { "C", "P" })
                {
                    contracts.Add(new Contract
                    {
                        Symbol = request.Symbol,
                        SecType = "OPT",
                        Exchange = "SMART",
                        Currency = request.Currency,
                        LastTradeDateOrContractMonth = expiry.Text,
                        Strike = strikes[(int)i],
                        Right = right,
                        Multiplier = expiry.Class.Multiplier,
                        TradingClass = expiry.Class.TradingClass,
                    });
                }
            }
        }
        return contracts;
    }
}
