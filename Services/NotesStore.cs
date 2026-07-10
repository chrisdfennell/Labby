using Labby.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>Markdown notes persisted in the shared SQLite file.</summary>
public sealed class NotesStore(IOptions<HistoryOptions> options, IHostEnvironment env)
{
    private readonly string _dbPath = Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

    public sealed record Note(long Id, string Title, string Content, DateTimeOffset UpdatedAt, bool Pinned);

    public async Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var notes = new List<Note>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, content, updated_at, pinned FROM notes ORDER BY pinned DESC, updated_at DESC";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            notes.Add(new Note(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).ToLocalTime(), reader.GetInt64(4) != 0));
        }
        return notes;
    }

    /// <summary>The single pinned note shown on the dashboard, if any.</summary>
    public async Task<Note?> GetPinnedAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, content, updated_at, pinned FROM notes WHERE pinned = 1 LIMIT 1";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new Note(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).ToLocalTime(), true);
    }

    /// <summary>Pins one note to the dashboard (unpinning any other) or unpins it.</summary>
    public async Task SetPinnedAsync(long id, bool pinned, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = pinned
            ? "UPDATE notes SET pinned = 0; UPDATE notes SET pinned = 1 WHERE id = $id"
            : "UPDATE notes SET pinned = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> SaveAsync(long? id, string title, string content, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        if (id is { } existing)
        {
            cmd.CommandText = "UPDATE notes SET title = $t, content = $c, updated_at = $u WHERE id = $id; SELECT $id;";
            cmd.Parameters.AddWithValue("$id", existing);
        }
        else
        {
            cmd.CommandText = "INSERT INTO notes (title, content, updated_at) VALUES ($t, $c, $u); SELECT last_insert_rowid();";
        }
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
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
                CREATE TABLE IF NOT EXISTS notes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    content TEXT NOT NULL,
                    updated_at INTEGER NOT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            // Databases created before the pinned column existed migrate in place.
            var migrate = conn.CreateCommand();
            migrate.CommandText = "ALTER TABLE notes ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0";
            try
            {
                await migrate.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException)
            {
                // duplicate column — already migrated
            }
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
