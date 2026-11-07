using System.Collections.Concurrent;
using IBApi;

namespace Pricer.Data.Ibkr;

public sealed record MarketTicks(double Bid, double Ask, double Last, double Close);

public sealed record AccountPosition(Contract Contract, double Quantity);

public sealed record OptionParameters(string Exchange, string TradingClass, string Multiplier, HashSet<string> Expirations, HashSet<double> Strikes);

/// <summary>Task-based wrapper around the callback API. Read-only: it never sends orders.</summary>
public sealed class IbkrConnection : DefaultEWrapper, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    // Delayed-data notices and connection status messages are not failures of a request.
    private static readonly HashSet<int> Notices = [2104, 2106, 2107, 2108, 2119, 2158, 10167, 10090];

    private sealed class Pending
    {
        public TaskCompletionSource<object?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double Bid, Ask, Last, Close;
        public int ConId;
        public List<OptionParameters> Parameters { get; } = [];
    }

    private readonly EReaderMonitorSignal _signal = new();
    private readonly EClientSocket _client;
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextRequestId = 1000;
    private string? _lastConnectionError;
    private readonly List<AccountPosition> _positions = [];
    private TaskCompletionSource? _positionsDone;

    private IbkrConnection() => _client = new EClientSocket(this, _signal);

    public static async Task<IbkrConnection> ConnectAsync(CaptureSettings settings, CancellationToken token)
    {
        var connection = new IbkrConnection();
        connection._client.eConnect(settings.Host, settings.Port, settings.ClientId);
        if (!connection._client.IsConnected())
        {
            throw new IOException(
                $"Cannot connect to TWS on {settings.Host}:{settings.Port}. Check that TWS is running and the socket API is enabled. {connection._lastConnectionError}");
        }

        var reader = new EReader(connection._client, connection._signal);
        reader.Start();
        new Thread(() =>
        {
            while (connection._client.IsConnected())
            {
                connection._signal.waitForSignal();
                reader.processMsgs();
            }
        }) { IsBackground = true, Name = "IbkrReader" }.Start();

        await connection._ready.Task.WaitAsync(ConnectTimeout, token);
        connection._client.reqMarketDataType(settings.MarketDataType);
        return connection;
    }

    public async Task<int> ContractIdAsync(Contract contract, TimeSpan timeout, CancellationToken token)
    {
        var (id, pending) = Register();
        _client.reqContractDetails(id, contract);
        await Await(id, pending, timeout, token);
        return pending.ConId != 0 ? pending.ConId : throw new InvalidOperationException($"Contract not found: {contract.Symbol}.");
    }

    public async Task<IReadOnlyList<OptionParameters>> OptionParametersAsync(
        string symbol, string secType, int conId, TimeSpan timeout, CancellationToken token)
    {
        var (id, pending) = Register();
        _client.reqSecDefOptParams(id, symbol, "", secType, conId);
        await Await(id, pending, timeout, token);
        return pending.Parameters;
    }

    /// <summary>One market data snapshot. On timeout, returns whatever ticks already arrived.</summary>
    public async Task<MarketTicks> SnapshotAsync(Contract contract, TimeSpan timeout, CancellationToken token)
    {
        var (id, pending) = Register();
        _client.reqMktData(id, contract, "", true, false, []);
        try
        {
            await Await(id, pending, timeout, token);
        }
        catch (TimeoutException)
        {
            _client.cancelMktData(id);
        }
        return new MarketTicks(pending.Bid, pending.Ask, pending.Last, pending.Close);
    }

    public async Task<IReadOnlyList<AccountPosition>> PositionsAsync(TimeSpan timeout, CancellationToken token)
    {
        _positions.Clear();
        _positionsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.reqPositions();
        try
        {
            await _positionsDone.Task.WaitAsync(timeout, token);
        }
        finally
        {
            _client.cancelPositions();
        }
        return [.. _positions];
    }

    public void Dispose()
    {
        if (_client.IsConnected()) _client.eDisconnect();
        _signal.issueSignal();
    }

    private (int, Pending) Register()
    {
        int id = Interlocked.Increment(ref _nextRequestId);
        var pending = new Pending();
        _pending[id] = pending;
        return (id, pending);
    }

    private async Task Await(int id, Pending pending, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            await pending.Done.Task.WaitAsync(timeout, token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void Complete(int id)
    {
        if (_pending.TryGetValue(id, out var pending)) pending.Done.TrySetResult(null);
    }

    public override void nextValidId(int orderId) => _ready.TrySetResult();

    public override void position(string account, Contract contract, decimal pos, double avgCost) =>
        _positions.Add(new AccountPosition(contract, (double)pos));

    public override void positionEnd() => _positionsDone?.TrySetResult();

    public override void error(Exception e) => _lastConnectionError = e.Message;

    public override void error(string str) => _lastConnectionError = str;

    public override void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        if (Notices.Contains(errorCode)) return;
        if (id < 0)
        {
            _lastConnectionError = $"{errorCode}: {errorMsg}";
            return;
        }
        // A missing option strike (200) or absent data simply leaves the quote empty.
        Complete(id);
    }

    public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (price <= 0 || !_pending.TryGetValue(tickerId, out var pending)) return;
        switch (field)
        {
            case TickType.BID or TickType.DELAYED_BID: pending.Bid = price; break;
            case TickType.ASK or TickType.DELAYED_ASK: pending.Ask = price; break;
            case TickType.LAST or TickType.DELAYED_LAST: pending.Last = price; break;
            case TickType.CLOSE or TickType.DELAYED_CLOSE: pending.Close = price; break;
        }
    }

    public override void tickSnapshotEnd(int tickerId) => Complete(tickerId);

    public override void contractDetails(int reqId, ContractDetails contractDetails)
    {
        if (_pending.TryGetValue(reqId, out var pending)) pending.ConId = contractDetails.Contract.ConId;
    }

    public override void contractDetailsEnd(int reqId) => Complete(reqId);

    public override void securityDefinitionOptionParameter(
        int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier,
        HashSet<string> expirations, HashSet<double> strikes)
    {
        if (_pending.TryGetValue(reqId, out var pending))
        {
            pending.Parameters.Add(new OptionParameters(exchange, tradingClass, multiplier, expirations, strikes));
        }
    }

    public override void securityDefinitionOptionParameterEnd(int reqId) => Complete(reqId);
}
