using System.Globalization;
using Labby.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// The household's own calendar: people, their events, and the recurrence rules
/// that turn one row into a birthday every year or trash night every week.
/// Everything is stored in local wall-clock terms — a 6pm soccer practice stays
/// at 6pm regardless of what the server thinks its offset is.
/// </summary>
public sealed class FamilyCalendarStore(IOptions<HistoryOptions> options, IHostEnvironment env)
{
    private readonly string _dbPath = Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

    /// <summary>Colours offered when adding a person, tuned for the dark console theme.</summary>
    public static readonly string[] Palette =
        ["#2dd4a7", "#4aa8ff", "#f0b429", "#f4587a", "#b98aff", "#ff9f6e", "#5ce8c4", "#8fd14f"];

    public static readonly string[] Repeats = ["none", "daily", "weekly", "biweekly", "monthly", "yearly"];

    /// <summary>
    /// A person in the house. <paramref name="Avatar"/> and <paramref name="IsKid"/>
    /// only matter to the chores portal; the calendar ignores them.
    /// </summary>
    public sealed record FamilyMember(long Id, string Name, string Color, string Avatar = "", bool IsKid = false)
    {
        /// <summary>Falls back to the first letter when no avatar emoji is set.</summary>
        public string Face => string.IsNullOrEmpty(Avatar)
            ? (Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?")
            : Avatar;
    }

    public sealed record FamilyEvent
    {
        public long Id { get; init; }
        public string Title { get; init; } = "";
        public string Icon { get; init; } = "";
        public string Notes { get; init; } = "";
        public string Location { get; init; } = "";
        public long? MemberId { get; init; }
        public DateOnly StartDate { get; init; }
        /// <summary>Null for an all-day event.</summary>
        public TimeOnly? StartTime { get; init; }
        public DateOnly EndDate { get; init; }
        public TimeOnly? EndTime { get; init; }
        public string Repeat { get; init; } = "none";
        public DateOnly? RepeatUntil { get; init; }

        public bool AllDay => StartTime is null;
        /// <summary>How many extra days the event runs past its start date.</summary>
        public int SpanDays => Math.Max(0, EndDate.DayNumber - StartDate.DayNumber);
    }

    /// <summary>One day of one (possibly recurring, possibly multi-day) event.</summary>
    public sealed record FamilyOccurrence(FamilyEvent Event, DateOnly Date, DateOnly OccurrenceStart, FamilyMember? Member)
    {
        public bool IsFirstDay => Date == OccurrenceStart;
        public bool IsMultiDay => Event.SpanDays > 0;

        /// <summary>"6:00 PM", "6:00 PM–7:30 PM", or empty for an all-day event.</summary>
        public string TimeLabel
        {
            get
            {
                if (Event.StartTime is not { } start)
                    return "";
                if (!IsFirstDay)
                    return "";
                var label = start.ToString("h:mm tt", CultureInfo.InvariantCulture);
                if (Event.EndTime is { } end && !IsMultiDay && end != start)
                    label += "–" + end.ToString("h:mm tt", CultureInfo.InvariantCulture);
                return label;
            }
        }

        /// <summary>"6p", "6:30p" — the grid cells are too narrow for a full range.</summary>
        public string ShortTimeLabel
        {
            get
            {
                if (Event.StartTime is not { } start || !IsFirstDay)
                    return "";
                var suffix = start.Hour < 12 ? "a" : "p";
                var hour = start.Hour % 12 == 0 ? 12 : start.Hour % 12;
                return start.Minute == 0 ? $"{hour}{suffix}" : $"{hour}:{start.Minute:00}{suffix}";
            }
        }

        /// <summary>Sort key that keeps all-day events above timed ones.</summary>
        public TimeOnly SortTime => Event.StartTime ?? TimeOnly.MinValue;
    }

    // ── People ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FamilyMember>> GetMembersAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var members = new List<FamilyMember>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, color, avatar, is_kid FROM family_members ORDER BY name COLLATE NOCASE";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            members.Add(new FamilyMember(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4) != 0));
        }
        return members;
    }

    /// <summary>The kids, for the chores portal.</summary>
    public async Task<IReadOnlyList<FamilyMember>> GetKidsAsync(CancellationToken ct = default) =>
        (await GetMembersAsync(ct)).Where(m => m.IsKid).ToList();

    public async Task<FamilyMember?> GetMemberAsync(long id, CancellationToken ct = default) =>
        (await GetMembersAsync(ct)).FirstOrDefault(m => m.Id == id);

    /// <summary>Sets the chores-portal fields; the calendar's name and colour are untouched.</summary>
    public async Task SetKidProfileAsync(long id, string avatar, bool isKid, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE family_members SET avatar = $a, is_kid = $k WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$a", avatar);
        cmd.Parameters.AddWithValue("$k", isKid ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> SaveMemberAsync(long? id, string name, string color, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        if (id is { } existing)
        {
            cmd.CommandText = "UPDATE family_members SET name = $n, color = $c WHERE id = $id; SELECT $id;";
            cmd.Parameters.AddWithValue("$id", existing);
        }
        else
        {
            cmd.CommandText = "INSERT INTO family_members (name, color) VALUES ($n, $c); SELECT last_insert_rowid();";
        }
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$c", color);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Removes a person; their events survive as unassigned.</summary>
    public async Task DeleteMemberAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE family_events SET member_id = NULL WHERE member_id = $id;
            DELETE FROM family_members WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Events ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FamilyEvent>> GetEventsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var events = new List<FamilyEvent>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, icon, notes, location, member_id,
                   start_date, start_time, end_date, end_time, repeat, repeat_until
            FROM family_events
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(new FamilyEvent
            {
                Id = reader.GetInt64(0),
                Title = reader.GetString(1),
                Icon = reader.GetString(2),
                Notes = reader.GetString(3),
                Location = reader.GetString(4),
                MemberId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                StartDate = ParseDate(reader.GetString(6)),
                StartTime = reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
                EndDate = ParseDate(reader.GetString(8)),
                EndTime = reader.IsDBNull(9) ? null : ParseTime(reader.GetString(9)),
                Repeat = reader.GetString(10),
                RepeatUntil = reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)),
            });
        }
        return events;
    }

    /// <summary>
    /// Every day an event touches between <paramref name="from"/> and
    /// <paramref name="to"/> inclusive, recurrences expanded, sorted for display.
    /// </summary>
    public async Task<IReadOnlyList<FamilyOccurrence>> GetOccurrencesAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var events = await GetEventsAsync(ct);
        var members = (await GetMembersAsync(ct)).ToDictionary(m => m.Id);
        var occurrences = new List<FamilyOccurrence>();
        foreach (var ev in events)
        {
            var member = ev.MemberId is { } mid ? members.GetValueOrDefault(mid) : null;
            foreach (var start in OccurrenceStarts(ev, from, to))
            {
                for (var offset = 0; offset <= ev.SpanDays; offset++)
                {
                    var day = start.AddDays(offset);
                    if (day >= from && day <= to)
                        occurrences.Add(new FamilyOccurrence(ev, day, start, member));
                }
            }
        }
        return occurrences
            .OrderBy(o => o.Date)
            .ThenBy(o => o.Event.AllDay ? 0 : 1)
            .ThenBy(o => o.SortTime)
            .ThenBy(o => o.Event.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The next <paramref name="days"/> days of events, today included.</summary>
    public async Task<IReadOnlyList<FamilyOccurrence>> GetUpcomingAsync(int days = 14, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var all = await GetOccurrencesAsync(today, today.AddDays(days), ct);
        // A multi-day event that started before today still counts as running today.
        return all.Where(o => o.IsFirstDay || o.Date == today).ToList();
    }

    public async Task<long> SaveEventAsync(FamilyEvent ev, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        if (ev.Id > 0)
        {
            cmd.CommandText = """
                UPDATE family_events SET title = $t, icon = $i, notes = $n, location = $l, member_id = $m,
                    start_date = $sd, start_time = $st, end_date = $ed, end_time = $et,
                    repeat = $r, repeat_until = $ru
                WHERE id = $id;
                SELECT $id;
                """;
            cmd.Parameters.AddWithValue("$id", ev.Id);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO family_events
                    (title, icon, notes, location, member_id, start_date, start_time, end_date, end_time,
                     repeat, repeat_until, created_at)
                VALUES ($t, $i, $n, $l, $m, $sd, $st, $ed, $et, $r, $ru, $created);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        cmd.Parameters.AddWithValue("$t", ev.Title.Trim());
        cmd.Parameters.AddWithValue("$i", ev.Icon);
        cmd.Parameters.AddWithValue("$n", ev.Notes);
        cmd.Parameters.AddWithValue("$l", ev.Location);
        cmd.Parameters.AddWithValue("$m", (object?)ev.MemberId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sd", Format(ev.StartDate));
        cmd.Parameters.AddWithValue("$st", ev.StartTime is { } st ? Format(st) : DBNull.Value);
        cmd.Parameters.AddWithValue("$ed", Format(ev.EndDate < ev.StartDate ? ev.StartDate : ev.EndDate));
        cmd.Parameters.AddWithValue("$et", ev.StartTime is not null && ev.EndTime is { } et ? Format(et) : DBNull.Value);
        cmd.Parameters.AddWithValue("$r", Repeats.Contains(ev.Repeat) ? ev.Repeat : "none");
        cmd.Parameters.AddWithValue("$ru", ev.RepeatUntil is { } ru ? Format(ru) : DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task DeleteEventAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM family_events WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Ends a recurring series the day before <paramref name="date"/> rather than
    /// deleting its history — "we stopped doing this" instead of "it never happened".
    /// </summary>
    public async Task EndSeriesAsync(long id, DateOnly date, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE family_events SET repeat_until = $ru WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ru", Format(date.AddDays(-1)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Recurrence ──────────────────────────────────────────────────────

    /// <summary>
    /// Start dates of every occurrence that could overlap the window. A window
    /// day can be covered by an occurrence that began up to SpanDays earlier, so
    /// the search starts that far back.
    /// </summary>
    private static IEnumerable<DateOnly> OccurrenceStarts(FamilyEvent ev, DateOnly from, DateOnly to)
    {
        var windowStart = from.AddDays(-ev.SpanDays);
        var last = ev.RepeatUntil is { } until && until < to ? until : to;

        switch (ev.Repeat)
        {
            case "daily" or "weekly" or "biweekly":
            {
                var step = ev.Repeat switch { "daily" => 1, "weekly" => 7, _ => 14 };
                // Jump straight to the first occurrence in range instead of walking years of them.
                var behind = windowStart.DayNumber - ev.StartDate.DayNumber;
                var skip = behind > 0 ? (behind + step - 1) / step : 0;
                for (var d = ev.StartDate.AddDays(skip * step); d <= last; d = d.AddDays(step))
                    yield return d;
                break;
            }
            case "monthly" or "yearly":
            {
                var months = ev.Repeat == "monthly" ? 1 : 12;
                var behind = (windowStart.Year - ev.StartDate.Year) * 12 + windowStart.Month - ev.StartDate.Month;
                var skip = behind > 0 ? behind / months : 0;
                for (var i = skip; ; i++)
                {
                    var d = ev.StartDate.AddMonths(i * months);
                    if (d > last)
                        break;
                    if (d >= windowStart)
                        yield return d;
                }
                break;
            }
            default:
            {
                if (ev.StartDate >= windowStart && ev.StartDate <= to)
                    yield return ev.StartDate;
                break;
            }
        }
    }

    // ── Storage plumbing ────────────────────────────────────────────────

    private static string Format(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Format(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static TimeOnly ParseTime(string s) => TimeOnly.ParseExact(s, "HH:mm", CultureInfo.InvariantCulture);

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
                CREATE TABLE IF NOT EXISTS family_members (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    color TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS family_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    icon TEXT NOT NULL DEFAULT '',
                    notes TEXT NOT NULL DEFAULT '',
                    location TEXT NOT NULL DEFAULT '',
                    member_id INTEGER NULL,
                    start_date TEXT NOT NULL,
                    start_time TEXT NULL,
                    end_date TEXT NOT NULL,
                    end_time TEXT NULL,
                    repeat TEXT NOT NULL DEFAULT 'none',
                    repeat_until TEXT NULL,
                    created_at INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_family_events_start ON family_events (start_date);
                """;
            await cmd.ExecuteNonQueryAsync(ct);

            // Rosters created before the chores portal existed migrate in place.
            foreach (var column in (string[])
                     ["avatar TEXT NOT NULL DEFAULT ''", "is_kid INTEGER NOT NULL DEFAULT 0"])
            {
                var migrate = conn.CreateCommand();
                migrate.CommandText = $"ALTER TABLE family_members ADD COLUMN {column}";
                try
                {
                    await migrate.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException)
                {
                    // duplicate column — already migrated
                }
            }
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
