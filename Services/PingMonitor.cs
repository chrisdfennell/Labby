using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Pings the configured hosts every 60 seconds, keeps the latest RTT for the
/// Network page, and logs every sample to the metrics store.
/// </summary>
public sealed class PingMonitor(MetricsStore store, IOptions<NetworkOptions> options, ILogger<PingMonitor> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, double?> _latest = new();

    /// <summary>Latest RTT per host name; null value = last ping failed.</summary>
    public IReadOnlyDictionary<string, double?> Latest => _latest;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.PingHosts.Count == 0)
        {
            logger.LogInformation("No Network:PingHosts configured; ping monitor idle");
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await Task.WhenAll(options.Value.PingHosts.Select(h => ProbeAsync(h, stoppingToken)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ping cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProbeAsync(PingHost host, CancellationToken ct)
    {
        double? rtt = null;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host.Host, TimeSpan.FromSeconds(2), cancellationToken: ct);
            if (reply.Status == IPStatus.Success)
                rtt = reply.RoundtripTime;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ping to {Host} failed", host.Host);
        }

        _latest[host.Name] = rtt;
        await store.WritePingAsync(host.Name, DateTimeOffset.Now, rtt, ct);
    }
}
