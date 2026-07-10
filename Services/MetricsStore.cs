using Labby.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Shared SQLite storage for the monitoring metrics: miner stats, NAS vitals,
/// volume usage, ping RTTs, and speedtest results. Writes are fire-and-log —
/// metric history must never take a poller down.
/// </summary>
public sealed class MetricsStore(IOptions<HistoryOptions> options, IHostEnvironment env, ILogger<MetricsStore> logger)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(35);

    private readonly string _dbPath = Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private long _writes;

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

    public sealed record Point(DateTimeOffset At, double? Value);
    public sealed record SpeedtestResult(DateTimeOffset At, double DownMbps, double UpMbps, double PingMs);

    /// <summary>Bucket-averages a series down to a chartable size. Nulls stay null (gaps).</summary>
    public static IReadOnlyList<Point> Downsample(IReadOnlyList<Point> points, int maxPoints = 360)
    {
        if (points.Count <= maxPoints)
            return points;
        var bucketSize = (int)Math.Ceiling(points.Count / (double)maxPoints);
        var result = new List<Point>(maxPoints);
        for (var i = 0; i < points.Count; i += bucketSize)
        {
            var bucket = points.Skip(i).Take(bucketSize).ToList();
            var values = bucket.Select(p => p.Value).OfType<double>().ToList();
            result.Add(new Point(bucket[bucket.Count / 2].At, values.Count > 0 ? values.Average() : null));
        }
        return result;
    }

    // ── writes ───────────────────────────────────────────────────────────

    public Task WriteMinerAsync(string name, DateTimeOffset at, double? hashRateMhs, long accepted, long rejected, CancellationToken ct) =>
        SafeWriteAsync("INSERT OR IGNORE INTO miner_history (name, at, hashrate_mhs, accepted, rejected) VALUES ($a, $b, $c, $d, $e)",
            [("$a", name), ("$b", at.ToUnixTimeSeconds()), ("$c", (object?)hashRateMhs ?? DBNull.Value), ("$d", accepted), ("$e", rejected)], ct);

    public Task WriteNasAsync(DateTimeOffset at, double? cpuPercent, double? ramPercent, double? cpuTempC, double? systemTempC, CancellationToken ct) =>
        SafeWriteAsync("INSERT OR IGNORE INTO nas_history (at, cpu, ram, cpu_temp, sys_temp) VALUES ($a, $b, $c, $d, $e)",
            [("$a", at.ToUnixTimeSeconds()), ("$b", (object?)cpuPercent ?? DBNull.Value), ("$c", (object?)ramPercent ?? DBNull.Value),
             ("$d", (object?)cpuTempC ?? DBNull.Value), ("$e", (object?)systemTempC ?? DBNull.Value)], ct);

    public Task WriteVolumeAsync(string label, DateTimeOffset at, double usedPercent, CancellationToken ct) =>
        SafeWriteAsync("INSERT OR IGNORE INTO volume_history (label, at, used_percent) VALUES ($a, $b, $c)",
            [("$a", label), ("$b", at.ToUnixTimeSeconds()), ("$c", usedPercent)], ct);

    public Task WritePingAsync(string host, DateTimeOffset at, double? rttMs, CancellationToken ct) =>
        SafeWriteAsync("INSERT OR IGNORE INTO ping_history (host, at, rtt_ms) VALUES ($a, $b, $c)",
            [("$a", host), ("$b", at.ToUnixTimeSeconds()), ("$c", (object?)rttMs ?? DBNull.Value)], ct);

    public Task WriteSpeedtestAsync(DateTimeOffset at, double downMbps, double upMbps, double pingMs, CancellationToken ct) =>
        SafeWriteAsync("INSERT OR IGNORE INTO speedtest_history (at, down_mbps, up_mbps, ping_ms) VALUES ($a, $b, $c, $d)",
            [("$a", at.ToUnixTimeSeconds()), ("$b", downMbps), ("$c", upMbps), ("$d", pingMs)], ct);

    // ── reads ────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<Point>> GetMinerSeriesAsync(string name, DateTimeOffset from, CancellationToken ct = default) =>
        QuerySeriesAsync("SELECT at, hashrate_mhs FROM miner_history WHERE name = $key AND at >= $from ORDER BY at", name, from, ct);

    public Task<IReadOnlyList<Point>> GetNasSeriesAsync(string column, DateTimeOffset from, CancellationToken ct = default)
    {
        // column comes from our own render code, but keep it allow-listed anyway.
        if (column is not ("cpu" or "ram" or "cpu_temp" or "sys_temp"))
            throw new ArgumentOutOfRangeException(nameof(column));
        return QuerySeriesAsync($"SELECT at, {column} FROM nas_history WHERE at >= $from ORDER BY at", null, from, ct);
    }

    public Task<IReadOnlyList<Point>> GetPingSeriesAsync(string host, DateTimeOffset from, CancellationToken ct = default) =>
        QuerySeriesAsync("SELECT at, rtt_ms FROM ping_history WHERE host = $key AND at >= $from ORDER BY at", host, from, ct);

    public async Task<IReadOnlyList<SpeedtestResult>> GetSpeedtestsAsync(DateTimeOffset from, CancellationToken ct = default)
    {
        var results = new List<SpeedtestResult>();
        if (!File.Exists(_dbPath))
            return results;
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT at, down_mbps, up_mbps, ping_ms FROM speedtest_history WHERE at >= $from ORDER BY at";
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SpeedtestResult(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).ToLocalTime(),
                reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
        }
        return results;
    }

    /// <summary>Days until the volume hits 100% at the average fill rate of the observed window; null if shrinking or unknown.</summary>
    public async Task<double?> EstimateVolumeFullDaysAsync(string label, CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath))
            return null;
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(at), MAX(at),
                   (SELECT used_percent FROM volume_history WHERE label = $l ORDER BY at LIMIT 1),
                   (SELECT used_percent FROM volume_history WHERE label = $l ORDER BY at DESC LIMIT 1)
            FROM volume_history WHERE label = $l
            """;
        cmd.Parameters.AddWithValue("$l", label);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
            return null;
        var spanDays = (reader.GetInt64(1) - reader.GetInt64(0)) / 86400.0;
        var growth = reader.GetDouble(3) - reader.GetDouble(2);
        if (spanDays < 2 || growth <= 0.05)
            return null; // too little data, or not growing
        return Math.Round((100 - reader.GetDouble(3)) / (growth / spanDays));
    }

    // ── plumbing ─────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Point>> QuerySeriesAsync(string sql, string? key, DateTimeOffset from, CancellationToken ct)
    {
        var points = new List<Point>();
        if (!File.Exists(_dbPath))
            return points;
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (key is not null)
            cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            points.Add(new Point(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).ToLocalTime(),
                reader.IsDBNull(1) ? null : reader.GetDouble(1)));
        }
        return points;
    }

    private async Task SafeWriteAsync(string sql, (string Name, object Value)[] parameters, CancellationToken ct)
    {
        try
        {
            await EnsureInitializedAsync(ct);
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync(ct);

            if (Interlocked.Increment(ref _writes) % 5000 == 0)
                await PruneAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Metric write failed");
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS miner_history (
                    name TEXT NOT NULL, at INTEGER NOT NULL, hashrate_mhs REAL, accepted INTEGER, rejected INTEGER,
                    PRIMARY KEY (name, at));
                CREATE TABLE IF NOT EXISTS nas_history (
                    at INTEGER PRIMARY KEY, cpu REAL, ram REAL, cpu_temp REAL, sys_temp REAL);
                CREATE TABLE IF NOT EXISTS volume_history (
                    label TEXT NOT NULL, at INTEGER NOT NULL, used_percent REAL, PRIMARY KEY (label, at));
                CREATE TABLE IF NOT EXISTS ping_history (
                    host TEXT NOT NULL, at INTEGER NOT NULL, rtt_ms REAL, PRIMARY KEY (host, at));
                CREATE TABLE IF NOT EXISTS speedtest_history (
                    at INTEGER PRIMARY KEY, down_mbps REAL, up_mbps REAL, ping_ms REAL);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        var cutoff = (DateTimeOffset.Now - Retention).ToUnixTimeSeconds();
        cmd.CommandText = """
            DELETE FROM miner_history WHERE at < $c;
            DELETE FROM nas_history WHERE at < $c;
            DELETE FROM ping_history WHERE at < $c;
            DELETE FROM volume_history WHERE at < $c2;
            DELETE FROM speedtest_history WHERE at < $c2;
            """;
        cmd.Parameters.AddWithValue("$c", cutoff);
        cmd.Parameters.AddWithValue("$c2", (DateTimeOffset.Now - TimeSpan.FromDays(400)).ToUnixTimeSeconds());
        var removed = await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Pruned {Rows} old metric rows", removed);
    }
}
