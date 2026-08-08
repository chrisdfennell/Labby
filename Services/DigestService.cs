using System.Text;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Sends a good-morning digest at the configured hour: current conditions,
/// sunrise/sunset, today's releases, and anything that broke overnight.
/// Enable with Alerts:DigestHour (e.g. 8); -1 disables.
/// </summary>
public sealed class DigestService(
    AmbientWeatherClient weather,
    MediaHub media,
    FamilyCalendarStore family,
    ServiceHistoryStore serviceHistory,
    AlertNotifier alerts,
    IOptions<AlertOptions> options,
    ILogger<DigestService> logger) : BackgroundService
{
    /// <summary>
    /// Why the scheduled digest isn't running, or null when it is armed. The
    /// Settings page shows this — a digest that is simply switched off looks
    /// identical to a broken one otherwise.
    /// </summary>
    public string? DisabledReason =>
        options.Value.DigestHour is < 0 or > 23 ? "Alerts:DigestHour is not set (0-23)"
        : !alerts.IsEnabled ? "no alert channel is configured"
        : null;

    /// <summary>When the next digest is due, once the schedule is running.</summary>
    public DateTimeOffset? NextRunAt { get; private set; }

    /// <summary>The configured local hour, for display.</summary>
    public int Hour => options.Value.DigestHour;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hour = options.Value.DigestHour;
        if (DisabledReason is { } reason)
        {
            logger.LogInformation("Morning digest idle ({Reason})", reason);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var next = NextRun(hour, now);
            NextRunAt = next;
            logger.LogInformation("Morning digest scheduled for {Next:yyyy-MM-dd HH:mm zzz}", next);
            await Task.Delay(next - now, stoppingToken);

            try
            {
                await alerts.SendAsync(await BuildDigestAsync(stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Morning digest failed");
            }
        }
    }

    /// <summary>
    /// The next time the clock reads <paramref name="hour"/>:00 locally. The offset
    /// is taken at the target instant, not "now" — building tomorrow's time with
    /// today's offset lands an hour out on each daylight-saving changeover.
    /// </summary>
    internal static DateTimeOffset NextRun(int hour, DateTimeOffset now)
    {
        var candidate = LocalAt(now.LocalDateTime.Date.AddHours(hour));
        return candidate > now ? candidate : LocalAt(now.LocalDateTime.Date.AddDays(1).AddHours(hour));
    }

    private static DateTimeOffset LocalAt(DateTime local) =>
        new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    public async Task<string> BuildDigestAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder("☀️ Good morning!");

        try
        {
            if (await weather.GetCurrentAsync(ct) is { } reading)
            {
                sb.Append($" {reading.TempF:0}°F");
                if (reading.HumidityPercent is { } hum)
                    sb.Append($", {hum:0}% humidity");
                if (reading is { StationLat: { } lat, StationLon: { } lon })
                {
                    var sun = SolarMath.For(DateOnly.FromDateTime(DateTime.Today), lat, lon, TimeZoneInfo.Local);
                    if (sun is { Sunrise: { } rise, Sunset: { } set })
                        sb.Append($". 🌅 {rise:HH:mm} → 🌇 {set:HH:mm}");
                }
                sb.Append('.');
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Digest weather section failed");
        }

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var events = await family.GetOccurrencesAsync(today, today.AddDays(1), ct);
            foreach (var day in events.GroupBy(o => o.Date))
            {
                var label = day.Key == today ? "Today" : "Tomorrow";
                sb.Append($"\n👨‍👩‍👧 {label}: {string.Join(", ", day.Take(6).Select(Describe))}");
                if (day.Count() > 6)
                    sb.Append($" (+{day.Count() - 6} more)");
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Digest family calendar section failed");
        }

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var releases = await media.GetCalendarAsync(today, today, ct);
            sb.Append(releases.Count > 0
                ? $"\n📅 Today: {string.Join(", ", releases.Take(6).Select(r => $"{r.Title} {r.Detail}".Trim()))}"
                  + (releases.Count > 6 ? $" (+{releases.Count - 6} more)" : "")
                : "\n📅 Nothing releasing today.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Digest calendar section failed");
        }

        try
        {
            var since = DateTimeOffset.Now.AddHours(-24);
            var outages = (await serviceHistory.GetRecentOutagesAsync(20, ct))
                .Where(o => o.Started >= since || (o.Ended is null))
                .ToList();
            sb.Append(outages.Count == 0
                ? "\n🟢 All services quiet overnight."
                : $"\n🔴 Last 24h: {string.Join("; ", outages.Take(4).Select(o =>
                    $"{o.Service} {(o.Ended is { } end ? Format.ShortDuration(end - o.Started) : "still down")}"))}");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Digest outage section failed");
        }

        return sb.ToString();
    }

    /// <summary>"⚽ 6p Soccer practice (Ada)" — compact enough for a push notification.</summary>
    private static string Describe(FamilyCalendarStore.FamilyOccurrence occurrence)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(occurrence.Event.Icon))
            sb.Append(occurrence.Event.Icon).Append(' ');
        if (occurrence.ShortTimeLabel is { Length: > 0 } time)
            sb.Append(time).Append(' ');
        sb.Append(occurrence.Event.Title);
        if (occurrence.Member is { } member)
            sb.Append($" ({member.Name})");
        return sb.ToString();
    }
}
