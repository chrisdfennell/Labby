using System.Collections.Concurrent;
using Labby.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// User-assigned labels for LAN devices — a friendly name and a "monitored" flag —
/// persisted in the shared SQLite file and keyed by MAC. This replaces the old
/// hard-coded <c>Network:KnownDevices</c> list: naming and monitoring are now done
/// from the Devices page. An in-memory cache backs the hot read path (the scanner
/// merges labels on every sweep and the page re-reads after each edit).
/// </summary>
public sealed class DeviceStore(IOptions<HistoryOptions> options, IHostEnvironment env)
{
    private readonly string _dbPath = Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DeviceLabel> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

    public sealed record DeviceLabel(string Mac, string? Name, bool Monitored);

    /// <summary>All stored labels. Loads the DB on first call, then serves from cache.</summary>
    public async Task<IReadOnlyDictionary<string, DeviceLabel>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return _cache;
    }

    public async Task SetNameAsync(string mac, string? name, CancellationToken ct = default)
    {
        var key = Normalize(mac);
        var current = await GetOrDefaultAsync(key, ct);
        await UpsertAsync(current with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() }, ct);
    }

    public async Task SetMonitoredAsync(string mac, bool monitored, CancellationToken ct = default)
    {
        var key = Normalize(mac);
        var current = await GetOrDefaultAsync(key, ct);
        await UpsertAsync(current with { Monitored = monitored }, ct);
    }

    /// <summary>Forgets a device's label entirely (it reappears unnamed if still on the network).</summary>
    public async Task DeleteAsync(string mac, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var key = Normalize(mac);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM devices WHERE mac = $mac";
        cmd.Parameters.AddWithValue("$mac", key);
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.TryRemove(key, out _);
    }

    private async Task<DeviceLabel> GetOrDefaultAsync(string key, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        return _cache.TryGetValue(key, out var existing) ? existing : new DeviceLabel(key, null, false);
    }

    private async Task UpsertAsync(DeviceLabel label, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        // A label with no name and not monitored carries no information — drop the row.
        if (label.Name is null && !label.Monitored)
        {
            await DeleteAsync(label.Mac, ct);
            return;
        }

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices (mac, name, monitored, updated_at) VALUES ($mac, $name, $mon, $u)
            ON CONFLICT(mac) DO UPDATE SET name = $name, monitored = $mon, updated_at = $u;
            """;
        cmd.Parameters.AddWithValue("$mac", label.Mac);
        cmd.Parameters.AddWithValue("$name", (object?)label.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mon", label.Monitored ? 1 : 0);
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
        _cache[label.Mac] = label;
    }

    private static string Normalize(string mac) => mac.Replace('-', ':').ToLowerInvariant();

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
            var create = conn.CreateCommand();
            create.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS devices (
                    mac TEXT PRIMARY KEY,
                    name TEXT,
                    monitored INTEGER NOT NULL DEFAULT 0,
                    updated_at INTEGER NOT NULL);
                """;
            await create.ExecuteNonQueryAsync(ct);

            var load = conn.CreateCommand();
            load.CommandText = "SELECT mac, name, monitored FROM devices";
            await using var reader = await load.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var mac = reader.GetString(0);
                _cache[mac] = new DeviceLabel(
                    mac,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2) != 0);
            }
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
