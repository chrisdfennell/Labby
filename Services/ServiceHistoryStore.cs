using Labby.Models;
using Labby.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Persists service probe results and outage windows to the shared SQLite file so
/// the Uptime page has real history across restarts. Storage problems are logged
/// and swallowed — history must never take down the health monitor.
/// </summary>
public sealed class ServiceHistoryStore(IOptions<HistoryOptions> options, IHostEnvironment env, ILogger<ServiceHistoryStore> logger)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(35);

    private readonly string _dbPath = Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private long _cycles;

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

    public sealed record ServiceUptime(string Service, double? Percent24h, double? Percent7d, IReadOnlyDictionary<DateOnly, double> Days);
    public sealed record OutageRecord(string Service, DateTimeOffset Started, DateTimeOffset? Ended, string? Error);

    public async Task RecordCycleAsync(IReadOnlyList<ServiceStatus> statuses, CancellationToken ct)
    {
        try
        {
            await EnsureInitializedAsync(ct);
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            foreach (var s in statuses)
            {
                if (s.IsUp is not { } up || s.CheckedAt is not { } at)
                    continue;
                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO service_probes (service, at, up, latency_ms) VALUES ($s, $at, $up, $lat)";
                cmd.Parameters.AddWithValue("$s", s.Name);
                cmd.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$up", up ? 1 : 0);
                cmd.Parameters.AddWithValue("$lat", (object?)s.LatencyMs ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);

            // ~once a day at the 30s poll cadence.
            if (Interlocked.Increment(ref _cycles) % 2880 == 0)
                await PruneAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording service history failed");
        }
    }

    /// <summary>Opens an outage on an up→down flip, closes the open one on down→up.</summary>
    public async Task RecordTransitionAsync(string service, bool up, DateTimeOffset at, string? error, CancellationToken ct)
    {
        try
        {
            await EnsureInitializedAsync(ct);
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            if (up)
            {
                cmd.CommandText = "UPDATE service_outages SET ended = $at WHERE service = $s AND ended IS NULL";
            }
            else
            {
                cmd.CommandText = """
                    UPDATE service_outages SET ended = $at WHERE service = $s AND ended IS NULL;
                    INSERT INTO service_outages (service, started, error) VALUES ($s, $at, $err);
                    """;
                cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            }
            cmd.Parameters.AddWithValue("$s", service);
            cmd.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording outage transition failed");
        }
    }

    /// <summary>Uptime per service: 24h window, 7d window, and per-day percentages for the bar strip.</summary>
    public async Task<IReadOnlyDictionary<string, ServiceUptime>> GetUptimesAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, ServiceUptime>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_dbPath))
            return result;

        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var windows = new Dictionary<string, (double? P24, double? P7)>();
        foreach (var (hours, is24) in ((int, bool)[])[(24, true), (168, false)])
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT service, AVG(up) * 100 FROM service_probes WHERE at >= $from GROUP BY service";
            cmd.Parameters.AddWithValue("$from", DateTimeOffset.Now.AddHours(-hours).ToUnixTimeSeconds());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var service = reader.GetString(0);
                var pct = Math.Round(reader.GetDouble(1), 2);
                var current = windows.TryGetValue(service, out var w) ? w : (null, null);
                windows[service] = is24 ? (pct, current.Item2) : (current.Item1, pct);
            }
        }

        var daysCmd = conn.CreateCommand();
        daysCmd.CommandText = """
            SELECT service, strftime('%Y-%m-%d', at, 'unixepoch', 'localtime') AS day, AVG(up) * 100
            FROM service_probes WHERE at >= $from GROUP BY service, day
            """;
        daysCmd.Parameters.AddWithValue("$from", DateTimeOffset.Now.AddDays(-30).ToUnixTimeSeconds());
        var days = new Dictionary<string, Dictionary<DateOnly, double>>();
        await using (var reader = await daysCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var service = reader.GetString(0);
                if (!days.TryGetValue(service, out var perDay))
                    days[service] = perDay = [];
                perDay[DateOnly.Parse(reader.GetString(1))] = Math.Round(reader.GetDouble(2), 2);
            }
        }

        foreach (var service in windows.Keys.Union(days.Keys))
        {
            windows.TryGetValue(service, out var w);
            result[service] = new ServiceUptime(service, w.P24, w.P7,
                days.TryGetValue(service, out var perDay) ? perDay : new Dictionary<DateOnly, double>());
        }
        return result;
    }

    public async Task<IReadOnlyList<OutageRecord>> GetRecentOutagesAsync(int limit = 20, CancellationToken ct = default)
    {
        var outages = new List<OutageRecord>();
        if (!File.Exists(_dbPath))
            return outages;

        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT service, started, ended, error FROM service_outages ORDER BY started DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            outages.Add(new OutageRecord(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime(),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).ToLocalTime(),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return outages;
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
                CREATE TABLE IF NOT EXISTS service_probes (
                    service    TEXT NOT NULL,
                    at         INTEGER NOT NULL,
                    up         INTEGER NOT NULL,
                    latency_ms INTEGER,
                    PRIMARY KEY (service, at)
                );
                CREATE TABLE IF NOT EXISTS service_outages (
                    id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    service TEXT NOT NULL,
                    started INTEGER NOT NULL,
                    ended   INTEGER,
                    error   TEXT
                );
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
        cmd.CommandText = "DELETE FROM service_probes WHERE at < $cutoff; DELETE FROM service_outages WHERE started < $cutoff AND ended IS NOT NULL;";
        cmd.Parameters.AddWithValue("$cutoff", (DateTimeOffset.Now - Retention).ToUnixTimeSeconds());
        var removed = await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Pruned {Rows} old service history rows", removed);
    }
}
