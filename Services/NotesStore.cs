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

    public sealed record Note(long Id, string Title, string Content, DateTimeOffset UpdatedAt);

    public async Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var notes = new List<Note>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, content, updated_at FROM notes ORDER BY updated_at DESC";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            notes.Add(new Note(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).ToLocalTime()));
        }
        return notes;
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
                    updated_at INTEGER NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
