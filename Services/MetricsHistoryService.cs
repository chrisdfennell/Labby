using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Logs miner stats and NAS vitals every 5 minutes (volume usage hourly) into
/// the metrics store, and alerts when a miner goes offline / stops hashing or
/// comes back.
/// </summary>
public sealed class MetricsHistoryService(
    MinerClient miners,
    QnapClient qnap,
    MetricsStore store,
    AlertNotifier alerts,
    IOptions<DashboardOptions> options,
    ILogger<MetricsHistoryService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, bool> _minerDown = [];
    private int _cycles;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Miners.Count == 0 && !qnap.IsConfigured)
        {
            logger.LogInformation("No miners or QNAP configured; metrics logger idle");
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await LogCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metrics cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task LogCycleAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;

        if (options.Value.Miners.Count > 0)
        {
            foreach (var miner in await miners.GetStatusAsync(ct))
            {
                var down = miner.Error is not null || (miner.HashRateMhs ?? 0) <= 0;
                if (!down)
                    await store.WriteMinerAsync(miner.Name, now, miner.HashRateMhs, miner.Accepted, miner.Rejected, ct);
                else
                    await store.WriteMinerAsync(miner.Name, now, 0, miner.Accepted, miner.Rejected, ct);

                // Alert on flips only; first observation stays quiet.
                if (_minerDown.TryGetValue(miner.Name, out var wasDown) && wasDown != down)
                {
                    await alerts.SendAsync(down
                        ? $"⛏️🔴 Miner {miner.Name} is {(miner.Error is not null ? "offline" : "not hashing")}{(miner.Error is not null ? $" — {miner.Error}" : "")}"
                        : $"⛏️🟢 Miner {miner.Name} is hashing again ({miner.HashRateDisplay})", ct);
                }
                _minerDown[miner.Name] = down;
            }
        }

        if (qnap.IsConfigured)
        {
            try
            {
                var info = await qnap.GetSystemInfoAsync(ct);
                await store.WriteNasAsync(now, info.CpuUsagePercent, info.UsedMemoryPercent, info.CpuTempC, info.SystemTempC, ct);

                if (_cycles % 12 == 0) // hourly at the 5-minute cadence
                {
                    foreach (var volume in await qnap.GetVolumesAsync(ct))
                        await store.WriteVolumeAsync(volume.Label, now, volume.UsedPercent, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "NAS metrics fetch failed");
            }
        }

        _cycles++;
    }
}
