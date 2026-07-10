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
    ServiceHistoryStore serviceHistory,
    AlertNotifier alerts,
    IOptions<AlertOptions> options,
    ILogger<DigestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hour = options.Value.DigestHour;
        if (hour is < 0 or > 23 || !alerts.IsEnabled)
        {
            logger.LogInformation("Morning digest idle ({Reason})",
                hour is < 0 or > 23 ? "Alerts:DigestHour not set" : "no alert channel configured");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var next = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset);
            if (next <= now)
                next = next.AddDays(1);
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
}
