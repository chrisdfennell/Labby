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
public sealed class ServiceHealthMonitor(IHttpClientFactory httpFactory, IOptions<DashboardOptions> options, AlertNotifier alerts, ServiceHistoryStore historyStore, ILogger<ServiceHealthMonitor> logger)
    : BackgroundService
{
    public const string HttpClientName = "health-probe";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>~1 hour of samples at the 30s poll cadence.</summary>
    private const int HistoryLength = 120;

    private readonly ConcurrentDictionary<string, ServiceStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, List<ProbeSample>> _history = new();

    /// <summary>Failed probes since the last good one, per service — the flap filter.</summary>
    private readonly ConcurrentDictionary<string, int> _failures = new();

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
                await historyStore.RecordCycleAsync(Snapshot, stoppingToken);
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

        var now = DateTimeOffset.Now;
        var latency = up ? stopwatch.ElapsedMilliseconds : (long?)null;

        _statuses.TryGetValue(service.Name, out var previous);

        // One bad probe is nearly always a blip — a dropped connection, a timeout, a
        // half-open socket answering garbage — and calling that an outage produces a
        // DOWN/UP alert pair a poll apart. Hold the previous state until the failure
        // repeats; a service that is really down still trips within a cycle or two.
        var threshold = Math.Max(1, service.FailuresBeforeDown ?? options.Value.FailuresBeforeDown);
        var failures = up ? 0 : _failures.GetValueOrDefault(service.Name) + 1;
        _failures[service.Name] = failures;

        var reportedUp = up ? true
            : failures >= threshold ? false
            : previous?.IsUp; // still in the grace window — null only before the first good probe
        if (!up && reportedUp is not false)
        {
            logger.LogInformation("{Service} probe {Failures}/{Threshold} failed, holding state: {Error}",
                service.Name, failures, threshold, error);
        }

        // Only this poller writes the list, but pages read snapshots concurrently.
        var history = _history.GetOrAdd(service.Name, _ => new List<ProbeSample>(HistoryLength));
        ProbeSample[] samples;
        lock (history)
        {
            history.Add(new ProbeSample(now, up, latency));
            if (history.Count > HistoryLength)
                history.RemoveAt(0);
            samples = [.. history];
        }

        _statuses[service.Name] = new ServiceStatus
        {
            Name = service.Name,
            Url = service.Url,
            Icon = service.Icon,
            Description = service.Description,
            Mac = service.Mac,
            IsUp = reportedUp,
            // Keep the last good latency while holding state, so a suppressed blip
            // doesn't blank the tile for a cycle.
            LatencyMs = up ? latency : reportedUp == true ? previous?.LatencyMs : null,
            CheckedAt = now,
            Error = reportedUp == false ? error : null,
            History = samples,
            UptimePercent = Math.Round(samples.Count(s => s.Up) * 100.0 / samples.Length, 1),
            // Both sides are nullable now, so null == null would match with no `previous` to read.
            StateSince = previous is not null && previous.IsUp == reportedUp ? previous.StateSince : now,
        };

        // Alert on transitions only — the first-ever probe of a service stays quiet.
        if (previous?.IsUp is { } wasUp && reportedUp is { } nowUp && wasUp != nowUp)
        {
            await historyStore.RecordTransitionAsync(service.Name, nowUp, now, error, ct);
            var message = nowUp
                ? $"🟢 {service.Name} is back UP ({latency}ms) after {Format.ShortDuration(now - (previous.StateSince ?? now))} down"
                : $"🔴 {service.Name} is DOWN — {error ?? "no response"}" + (threshold > 1 ? $" ({threshold} checks in a row)" : "");
            await alerts.SendAsync(message, ct);
        }
    }

    private static ServiceStatus ToPendingStatus(MonitoredService service) => new()
    {
        Name = service.Name,
        Url = service.Url,
        Icon = service.Icon,
        Description = service.Description,
        Mac = service.Mac,
        IsUp = null,
    };
}
