using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pricer.Core;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Data;

public sealed record SnapshotInfo(long Id, DateOnly AsOf, DateTime CapturedAt, string Source, string Hash, string Underlyings);

/// <summary>SQLite store for captured snapshots, with raw chains and calibration reports.</summary>
public sealed class SnapshotStore : IMarketDataProvider
{
    private readonly string _connection;

    public SnapshotStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = Open();
        db.Execute("""
            CREATE TABLE IF NOT EXISTS snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT, as_of TEXT NOT NULL, captured_at TEXT NOT NULL,
                source TEXT NOT NULL, hash TEXT NOT NULL, snapshot_json TEXT NOT NULL, reports_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chains (
                snapshot_id INTEGER NOT NULL REFERENCES snapshots(id), underlying TEXT NOT NULL,
                chain_json TEXT NOT NULL, PRIMARY KEY (snapshot_id, underlying));
            """);
    }

    public long Save(MarketSnapshot snapshot, IEnumerable<OptionChain> chains, IEnumerable<CalibrationReport> reports, string source)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        long id = db.ExecuteScalar<long>("""
            INSERT INTO snapshots (as_of, captured_at, source, hash, snapshot_json, reports_json)
            VALUES (@AsOf, @CapturedAt, @Source, @Hash, @Snapshot, @Reports);
            SELECT last_insert_rowid();
            """, new
        {
            AsOf = snapshot.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CapturedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Source = source,
            Hash = ContentHash.Of(snapshot),
            Snapshot = JsonSerializer.Serialize(snapshot),
            Reports = JsonSerializer.Serialize(reports.ToArray()),
        }, transaction);

        foreach (var chain in chains)
        {
            db.Execute(
                "INSERT INTO chains (snapshot_id, underlying, chain_json) VALUES (@Id, @Underlying, @Json)",
                new { Id = id, chain.Underlying, Json = JsonSerializer.Serialize(chain) }, transaction);
        }
        transaction.Commit();
        return id;
    }

    public IReadOnlyList<SnapshotInfo> List()
    {
        using var db = Open();
        return db.Query<(long Id, string AsOf, string CapturedAt, string Source, string Hash, string Json)>(
                "SELECT id, as_of, captured_at, source, hash, snapshot_json FROM snapshots ORDER BY id DESC")
            .Select(r => new SnapshotInfo(
                r.Id, DateOnly.Parse(r.AsOf, CultureInfo.InvariantCulture),
                DateTime.Parse(r.CapturedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.Source, r.Hash, string.Join(", ", Deserialize<MarketSnapshot>(r.Json).Underlyings.Keys)))
            .ToList();
    }

    public StoredCapture Load(long id)
    {
        using var db = Open();
        var row = db.QuerySingle<(string Snapshot, string Reports)>(
            "SELECT snapshot_json, reports_json FROM snapshots WHERE id = @id", new { id });
        return new StoredCapture(Deserialize<MarketSnapshot>(row.Snapshot), Deserialize<ImmutableArray<CalibrationReport>>(row.Reports));
    }

    public IReadOnlyList<string> ChainUnderlyings(long id)
    {
        using var db = Open();
        return [.. db.Query<string>("SELECT underlying FROM chains WHERE snapshot_id = @id ORDER BY underlying", new { id })];
    }

    public OptionChain LoadChain(long id, string underlying)
    {
        using var db = Open();
        return Deserialize<OptionChain>(db.QuerySingle<string>(
            "SELECT chain_json FROM chains WHERE snapshot_id = @id AND underlying = @underlying", new { id, underlying }));
    }

    /// <summary>Latest capture on or before the date.</summary>
    public MarketSnapshot GetSnapshot(DateOnly asOf)
    {
        using var db = Open();
        var json = db.QueryFirstOrDefault<string>(
            "SELECT snapshot_json FROM snapshots WHERE as_of <= @date ORDER BY as_of DESC, id DESC LIMIT 1",
            new { date = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        return json is null
            ? throw new KeyNotFoundException($"No snapshot on or before {asOf:yyyy-MM-dd}.")
            : Deserialize<MarketSnapshot>(json);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException($"Empty {typeof(T).Name}.");
}
