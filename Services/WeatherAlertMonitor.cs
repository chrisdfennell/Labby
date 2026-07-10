using System.Text.Json;

namespace Labby.Services;

/// <summary>
/// Polls the National Weather Service for active alerts at the station's
/// location (free API, no key) every 10 minutes. New alerts push through the
/// notifier once; the Weather page shows whatever is currently active.
/// </summary>
public sealed class WeatherAlertMonitor(IHttpClientFactory httpFactory, AmbientWeatherClient weather, AlertNotifier alerts, ILogger<WeatherAlertMonitor> logger)
    : BackgroundService
{
    public const string HttpClientName = "nws";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    private readonly HashSet<string> _notified = [];

    public sealed record WeatherAlert(string Id, string Event, string Headline, string Severity, DateTimeOffset? Ends);

    public IReadOnlyList<WeatherAlert> Active { get; private set; } = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!weather.IsConfigured)
        {
            logger.LogInformation("Weather alerts idle (Ambient Weather not configured)");
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
                logger.LogWarning(ex, "NWS alert check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (await weather.GetCurrentAsync(ct) is not { StationLat: { } lat, StationLon: { } lon })
            return;

        var http = httpFactory.CreateClient(HttpClientName);
        using var doc = JsonDocument.Parse(await http.GetStringAsync(
            $"https://api.weather.gov/alerts/active?point={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct));

        var active = new List<WeatherAlert>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var p = feature.GetProperty("properties");
            active.Add(new WeatherAlert(
                Str(p, "id"),
                Str(p, "event"),
                Str(p, "headline"),
                Str(p, "severity"),
                DateTimeOffset.TryParse(Str(p, "ends"), out var ends) ? ends : null));
        }
        Active = active;

        foreach (var alert in active)
        {
            if (_notified.Add(alert.Id))
                await alerts.SendAsync($"⛈️ {alert.Event}: {alert.Headline}", ct);
        }
        // Forget resolved ids so a re-issued alert notifies again.
        _notified.RemoveWhere(id => active.All(a => a.Id != id));
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
