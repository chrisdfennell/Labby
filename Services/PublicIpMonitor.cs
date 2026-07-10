using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Checks the WAN IP every 15 minutes (api.ipify.org) and alerts when it
/// changes — the classic "why did my port forward stop working" catch. The last
/// seen IP persists so changes across restarts are still noticed.
/// </summary>
public sealed class PublicIpMonitor(IHttpClientFactory httpFactory, MetricsStore metrics, AlertNotifier alerts, IOptions<NetworkOptions> options, ILogger<PublicIpMonitor> logger)
    : BackgroundService
{
    public const string HttpClientName = "public-ip";
    private const string KvKey = "public-ip";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    public string? Current { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WatchPublicIp)
        {
            logger.LogInformation("Public IP watch disabled (Network:WatchPublicIp)");
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
                logger.LogWarning(ex, "Public IP check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        var ip = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim();
        if (ip.Length == 0)
            return;

        Current = ip;
        CheckedAt = DateTimeOffset.Now;

        var previous = await metrics.GetValueAsync(KvKey, ct);
        if (previous is not null && previous != ip)
            await alerts.SendAsync($"🌍 Public IP changed: {previous} → {ip}", ct);
        if (previous != ip)
            await metrics.SetValueAsync(KvKey, ip, ct);
    }
}
