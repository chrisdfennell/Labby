using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Labby.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// The kids' chore portal: what is due on which days, who ticked it off, what it
/// was worth, and what has been cashed out. The roster itself lives in
/// <see cref="FamilyCalendarStore"/> so a kid is added to the house once.
/// </summary>
public sealed class ChoreStore(IOptions<HistoryOptions> options, IHostEnvironment env)
{
    private readonly string _dbPath = Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>Failed PIN attempts, so a 4-digit PIN can't be brute-forced by a sibling.</summary>
    private readonly Dictionary<long, (int Failures, DateTimeOffset? LockedUntil)> _attempts = [];
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(1);

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

    /// <summary>Mon-first weekday mask: "1111111" is every day, "1111100" weekdays only.</summary>
    public const string EveryDay = "1111111";
    public const string Weekdays = "1111100";
    public const string Weekends = "0000011";

    public sealed record Chore
    {
        public long Id { get; init; }
        public long MemberId { get; init; }
        public string Title { get; init; } = "";
        public string Icon { get; init; } = "";
        public int Points { get; init; } = 5;
        public string Days { get; init; } = EveryDay;
        /// <summary>When true the tick only pays out after a parent approves it.</summary>
        public bool NeedsApproval { get; init; }
        public bool Active { get; init; } = true;
        public DateOnly CreatedOn { get; init; }

        public bool FallsOn(DateOnly date) =>
            Days.Length == 7 && Days[((int)date.DayOfWeek + 6) % 7] == '1';

        /// <summary>"every day", "weekdays", "Mon, Thu" — how the schedule reads to a parent.</summary>
        public string ScheduleLabel => Days switch
        {
            EveryDay => "every day",
            Weekdays => "weekdays",
            Weekends => "weekends",
            _ => string.Join(", ", DayNames.Where((_, i) => i < Days.Length && Days[i] == '1')) is { Length: > 0 } named
                ? named
                : "never",
        };
    }

    public static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>A chore as it stands for one particular day.</summary>
    public sealed record ChoreToday(Chore Chore, DateOnly Day, string? Status, DateTimeOffset? DoneAt)
    {
        public bool IsDone => Status == "done";
        public bool IsPending => Status == "pending";
        public bool IsOpen => Status is null;
    }

    public sealed record Completion(
        long Id, long ChoreId, long MemberId, string Title, string Icon,
        DateOnly Day, DateTimeOffset DoneAt, string Status, int Points);

    public sealed record KidSummary(
        long MemberId, int Balance, int EarnedThisWeek, int Streak,
        int DueToday, int DoneToday, int PendingToday)
    {
        public bool AllDoneToday => DueToday > 0 && DoneToday + PendingToday >= DueToday;
        public int OpenToday => Math.Max(0, DueToday - DoneToday - PendingToday);
    }

    // ── PINs ────────────────────────────────────────────────────────────

    /// <summary>Whether this kid can sign in yet (a kid with no PIN set cannot).</summary>
    public async Task<bool> HasPinAsync(long memberId, CancellationToken ct = default) =>
        await GetPinHashAsync(memberId, ct) is not null;

    public async Task SetPinAsync(long memberId, string pin, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO kid_pins (member_id, pin_hash, updated_at) VALUES ($m, $h, $u)
            ON CONFLICT(member_id) DO UPDATE SET pin_hash = $h, updated_at = $u
            """;
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.Parameters.AddWithValue("$h", HashPin(pin));
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
        lock (_attempts)
            _attempts.Remove(memberId);
    }

    public async Task ClearPinAsync(long memberId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM kid_pins WHERE member_id = $m";
        cmd.Parameters.AddWithValue("$m", memberId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public sealed record PinResult(bool Ok, string? Error);

    /// <summary>Checks a PIN, with a short lockout after repeated misses.</summary>
    public async Task<PinResult> CheckPinAsync(long memberId, string pin, CancellationToken ct = default)
    {
        lock (_attempts)
        {
            if (_attempts.TryGetValue(memberId, out var state) &&
                state.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
            {
                var seconds = (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds);
                return new PinResult(false, $"Too many tries — wait {seconds}s.");
            }
        }

        var stored = await GetPinHashAsync(memberId, ct);
        if (stored is null)
            return new PinResult(false, "No PIN set yet — ask a parent.");

        if (!VerifyPin(pin, stored))
        {
            lock (_attempts)
            {
                var failures = (_attempts.TryGetValue(memberId, out var s) ? s.Failures : 0) + 1;
                _attempts[memberId] = failures >= MaxAttempts
                    ? (0, DateTimeOffset.UtcNow + LockoutTime)
                    : (failures, null);
            }
            return new PinResult(false, "That PIN didn't work.");
        }

        lock (_attempts)
            _attempts.Remove(memberId);
        return new PinResult(true, null);
    }

    private async Task<string?> GetPinHashAsync(long memberId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pin_hash FROM kid_pins WHERE member_id = $m";
        cmd.Parameters.AddWithValue("$m", memberId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // A 4-digit PIN is guessable by design; hashing keeps it out of plain sight
    // in the shared database rather than pretending it is a real secret.
    private const int Iterations = 100_000;

    private static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPin(string pin, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
            return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // ── Chores ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Chore>> GetChoresAsync(long? memberId = null, bool includeInactive = false,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var chores = new List<Chore>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, member_id, title, icon, points, days, needs_approval, active, created_on
            FROM chores
            WHERE ($m IS NULL OR member_id = $m) {(includeInactive ? "" : "AND active = 1")}
            ORDER BY sort_order, id
            """;
        cmd.Parameters.AddWithValue("$m", (object?)memberId ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            chores.Add(new Chore
            {
                Id = reader.GetInt64(0),
                MemberId = reader.GetInt64(1),
                Title = reader.GetString(2),
                Icon = reader.GetString(3),
                Points = reader.GetInt32(4),
                Days = reader.GetString(5),
                NeedsApproval = reader.GetInt64(6) != 0,
                Active = reader.GetInt64(7) != 0,
                CreatedOn = ParseDate(reader.GetString(8)),
            });
        }
        return chores;
    }

    public async Task<long> SaveChoreAsync(Chore chore, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        if (chore.Id > 0)
        {
            cmd.CommandText = """
                UPDATE chores SET member_id = $m, title = $t, icon = $i, points = $p, days = $d,
                    needs_approval = $na, active = $a
                WHERE id = $id;
                SELECT $id;
                """;
            cmd.Parameters.AddWithValue("$id", chore.Id);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO chores (member_id, title, icon, points, days, needs_approval, active, created_on, sort_order)
                VALUES ($m, $t, $i, $p, $d, $na, $a, $created,
                    (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM chores));
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$created", Format(DateOnly.FromDateTime(DateTime.Today)));
        }
        cmd.Parameters.AddWithValue("$m", chore.MemberId);
        cmd.Parameters.AddWithValue("$t", chore.Title.Trim());
        cmd.Parameters.AddWithValue("$i", chore.Icon);
        cmd.Parameters.AddWithValue("$p", Math.Clamp(chore.Points, 0, 1000));
        cmd.Parameters.AddWithValue("$d", chore.Days.Length == 7 ? chore.Days : EveryDay);
        cmd.Parameters.AddWithValue("$na", chore.NeedsApproval ? 1 : 0);
        cmd.Parameters.AddWithValue("$a", chore.Active ? 1 : 0);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Retires a chore but keeps its history in the ledger.</summary>
    public async Task DeactivateChoreAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chores SET active = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── A kid's day ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ChoreToday>> GetDayAsync(long memberId, DateOnly day, CancellationToken ct = default)
    {
        var chores = (await GetChoresAsync(memberId, ct: ct)).Where(c => c.FallsOn(day)).ToList();
        var log = await GetLogAsync(memberId, day, day, ct);
        var byChore = log.ToDictionary(c => c.ChoreId);
        return chores
            .Select(c => byChore.TryGetValue(c.Id, out var done)
                ? new ChoreToday(c, day, done.Status, done.DoneAt)
                : new ChoreToday(c, day, null, null))
            .ToList();
    }

    /// <summary>
    /// Ticks a chore off for the day. Trusted chores credit immediately; the rest
    /// wait for a parent. Re-ticking an already-logged chore is a no-op.
    /// </summary>
    public async Task CompleteAsync(long choreId, DateOnly day, CancellationToken ct = default)
    {
        var chore = (await GetChoresAsync(includeInactive: true, ct: ct)).FirstOrDefault(c => c.Id == choreId);
        if (chore is null || !chore.Active)
            return;

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chore_log (chore_id, member_id, day, done_at, status, points)
            VALUES ($c, $m, $d, $at, $s, $p)
            ON CONFLICT(chore_id, day) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$c", chore.Id);
        cmd.Parameters.AddWithValue("$m", chore.MemberId);
        cmd.Parameters.AddWithValue("$d", Format(day));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$s", chore.NeedsApproval ? "pending" : "done");
        cmd.Parameters.AddWithValue("$p", chore.Points);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Approves a pending tick so its points count.</summary>
    public async Task ApproveAsync(long logId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chore_log SET status = 'done' WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", logId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Removes a tick — used both for "that wasn't really done" and for denying a
    /// pending one. The chore goes back on the kid's list for that day.
    /// </summary>
    public async Task UndoAsync(long logId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chore_log WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", logId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Completion>> GetLogAsync(long? memberId, DateOnly from, DateOnly to,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var log = new List<Completion>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.id, l.chore_id, l.member_id, c.title, c.icon, l.day, l.done_at, l.status, l.points
            FROM chore_log l JOIN chores c ON c.id = l.chore_id
            WHERE ($m IS NULL OR l.member_id = $m) AND l.day >= $from AND l.day <= $to
            ORDER BY l.day DESC, l.done_at DESC
            """;
        cmd.Parameters.AddWithValue("$m", (object?)memberId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", Format(from));
        cmd.Parameters.AddWithValue("$to", Format(to));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            log.Add(new Completion(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                ParseDate(reader.GetString(5)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)).ToLocalTime(),
                reader.GetString(7), reader.GetInt32(8)));
        }
        return log;
    }

    /// <summary>Everything waiting on a parent, newest first.</summary>
    public async Task<IReadOnlyList<Completion>> GetPendingAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return (await GetLogAsync(null, today.AddDays(-30), today, ct))
            .Where(c => c.Status == "pending")
            .ToList();
    }

    // ── Points ──────────────────────────────────────────────────────────

    /// <summary>Records a payout, which subtracts from the running balance.</summary>
    public async Task CashOutAsync(long memberId, int points, string note, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO chore_payouts (member_id, points, note, at) VALUES ($m, $p, $n, $at)";
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.Parameters.AddWithValue("$p", points);
        cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<KidSummary> GetSummaryAsync(long memberId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.Today);

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var earned = conn.CreateCommand();
        earned.CommandText = "SELECT COALESCE(SUM(points), 0) FROM chore_log WHERE member_id = $m AND status = 'done'";
        earned.Parameters.AddWithValue("$m", memberId);
        var total = Convert.ToInt32(await earned.ExecuteScalarAsync(ct));

        var paid = conn.CreateCommand();
        paid.CommandText = "SELECT COALESCE(SUM(points), 0) FROM chore_payouts WHERE member_id = $m";
        paid.Parameters.AddWithValue("$m", memberId);
        var cashedOut = Convert.ToInt32(await paid.ExecuteScalarAsync(ct));

        // "This week" runs from Monday, matching how the calendar grid reads.
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var week = conn.CreateCommand();
        week.CommandText = """
            SELECT COALESCE(SUM(points), 0) FROM chore_log
            WHERE member_id = $m AND status = 'done' AND day >= $from
            """;
        week.Parameters.AddWithValue("$m", memberId);
        week.Parameters.AddWithValue("$from", Format(weekStart));
        var thisWeek = Convert.ToInt32(await week.ExecuteScalarAsync(ct));

        var day = await GetDayAsync(memberId, today, ct);
        var streak = await GetStreakAsync(memberId, ct);

        return new KidSummary(memberId, total - cashedOut, thisWeek, streak,
            day.Count, day.Count(c => c.IsDone), day.Count(c => c.IsPending));
    }

    /// <summary>
    /// Consecutive perfect days, counting back from today. Today only counts once
    /// everything is ticked, so a kid mid-morning sees yesterday's streak intact
    /// rather than a zero. Days with nothing scheduled are skipped, not broken.
    /// </summary>
    public async Task<int> GetStreakAsync(long memberId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var chores = await GetChoresAsync(memberId, ct: ct);
        if (chores.Count == 0)
            return 0;

        var earliest = chores.Min(c => c.CreatedOn);
        var log = await GetLogAsync(memberId, earliest, today, ct);
        var credited = log
            .Where(c => c.Status == "done")
            .Select(c => (c.ChoreId, c.Day))
            .ToHashSet();

        var streak = 0;
        for (var day = today; day >= earliest; day = day.AddDays(-1))
        {
            var due = chores.Where(c => c.FallsOn(day) && c.CreatedOn <= day).ToList();
            if (due.Count == 0)
                continue;
            if (due.All(c => credited.Contains((c.Id, day))))
            {
                streak++;
                continue;
            }
            // An unfinished today is simply not counted yet; any earlier gap ends it.
            if (day == today)
                continue;
            break;
        }
        return streak;
    }

    // ── Storage plumbing ────────────────────────────────────────────────

    private static string Format(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

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
                CREATE TABLE IF NOT EXISTS kid_pins (
                    member_id INTEGER PRIMARY KEY,
                    pin_hash TEXT NOT NULL,
                    updated_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS chores (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    member_id INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    icon TEXT NOT NULL DEFAULT '',
                    points INTEGER NOT NULL DEFAULT 5,
                    days TEXT NOT NULL DEFAULT '1111111',
                    needs_approval INTEGER NOT NULL DEFAULT 0,
                    active INTEGER NOT NULL DEFAULT 1,
                    created_on TEXT NOT NULL,
                    sort_order INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS chore_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    chore_id INTEGER NOT NULL,
                    member_id INTEGER NOT NULL,
                    day TEXT NOT NULL,
                    done_at INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    points INTEGER NOT NULL,
                    UNIQUE(chore_id, day));
                CREATE INDEX IF NOT EXISTS ix_chore_log_member_day ON chore_log (member_id, day);
                CREATE TABLE IF NOT EXISTS chore_payouts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    member_id INTEGER NOT NULL,
                    points INTEGER NOT NULL,
                    note TEXT NOT NULL DEFAULT '',
                    at INTEGER NOT NULL);
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
