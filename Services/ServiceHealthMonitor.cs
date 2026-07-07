using System.Collections.Concurrent;
using System.Diagnostics;
using Labby.Models;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Background poller for the services configured under Dashboard:Services.
/// Pages read <see cref="Snapshot"/> and subscribe to <see cref="Changed"/> for
/// live tile updates.
/// </summary>
public sealed class ServiceHealthMonitor(IHttpClientFactory httpFactory, IOptions<DashboardOptions> options, ILogger<ServiceHealthMonitor> logger)
    : BackgroundService
{
    public const string HttpClientName = "health-probe";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, ServiceStatus> _statuses = new();

    public event Action? Changed;

    public IReadOnlyList<ServiceStatus> Snapshot =>
        options.Value.Services
            .Select(s => _statuses.TryGetValue(s.Name, out var status) ? status : ToPendingStatus(s))
            .ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Services.Count == 0)
        {
            logger.LogInformation("No services configured under Dashboard:Services; health monitor idle");
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await Task.WhenAll(options.Value.Services.Select(s => ProbeAsync(s, stoppingToken)));
                Changed?.Invoke();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Health poll cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProbeAsync(MonitoredService service, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(service.HealthUrl) ? service.Url : service.HealthUrl;
        var stopwatch = Stopwatch.StartNew();
        bool up;
        string? error = null;

        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
            // Any HTTP answer (including 401/403 from auth-protected apps) means the service is alive.
            up = (int)response.StatusCode < 500;
            if (!up)
                error = $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            up = false;
            error = ex is TaskCanceledException ? "Timed out" : ex.GetBaseException().Message;
        }

        _statuses[service.Name] = new ServiceStatus
        {
            Name = service.Name,
            Url = service.Url,
            Icon = service.Icon,
            Description = service.Description,
            IsUp = up,
            LatencyMs = up ? stopwatch.ElapsedMilliseconds : null,
            CheckedAt = DateTimeOffset.Now,
            Error = error,
        };
    }

    private static ServiceStatus ToPendingStatus(MonitoredService service) => new()
    {
        Name = service.Name,
        Url = service.Url,
        Icon = service.Icon,
        Description = service.Description,
        IsUp = null,
    };
}
