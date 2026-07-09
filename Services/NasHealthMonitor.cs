using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Watches NAS vitals every 15 minutes and pushes webhook alerts when something
/// crosses a line: a disk's SMART health leaves "Good", a volume passes the
/// used-% threshold, or the CPU runs hot. Each condition alerts once when it
/// appears and once when it clears.
/// </summary>
public sealed class NasHealthMonitor(QnapClient qnap, AlertNotifier alerts, IOptions<AlertOptions> options, ILogger<NasHealthMonitor> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private static readonly string[] HealthyDiskStates = ["", "good", "ok", "normal", "ready"];

    private readonly Dictionary<string, string> _active = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!qnap.IsConfigured || !alerts.IsEnabled)
        {
            logger.LogInformation("NAS health alerts idle ({Reason})",
                !qnap.IsConfigured ? "QNAP not configured" : "no Alerts:WebhookUrl");
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // NAS unreachable — the dashboard tile covers that; don't flap health alerts.
                logger.LogWarning(ex, "NAS health check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var current = new Dictionary<string, string>();

        foreach (var disk in await qnap.GetDisksAsync(ct))
        {
            if (!HealthyDiskStates.Contains(disk.Health.Trim().ToLowerInvariant()))
                current[$"disk:{disk.Slot}"] = $"disk {disk.Slot} ({disk.Model}) SMART health is \"{disk.Health}\"";
        }

        if (options.Value.VolumeFullPercent > 0)
        {
            foreach (var volume in await qnap.GetVolumesAsync(ct))
            {
                if (volume.UsedPercent >= options.Value.VolumeFullPercent)
                    current[$"volume:{volume.Label}"] = $"volume \"{volume.Label}\" is {volume.UsedPercent:0.#}% full";
            }
        }

        if (options.Value.CpuTempC > 0)
        {
            var info = await qnap.GetSystemInfoAsync(ct);
            if (info.CpuTempC is { } cpu && cpu >= options.Value.CpuTempC)
                current["cpu-temp"] = $"CPU is at {cpu:0}°C";
        }

        foreach (var (key, description) in current)
        {
            if (_active.TryAdd(key, description))
                await alerts.SendAsync($"🟠 NAS warning: {description}", ct);
        }

        foreach (var key in _active.Keys.Where(k => !current.ContainsKey(k)).ToList())
        {
            var description = _active[key];
            _active.Remove(key);
            await alerts.SendAsync($"🟢 NAS cleared: {description}", ct);
        }
    }
}
