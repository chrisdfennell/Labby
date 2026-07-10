using System.Text.Json;
using Labby.Models;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Ambient Weather Network REST API (rt.ambientweather.net). Readings are cached
/// for a minute — the API is rate-limited to 1 req/sec per key and stations only
/// report every few minutes anyway.
/// </summary>
public sealed class AmbientWeatherClient(IHttpClientFactory httpFactory, IOptions<AmbientWeatherOptions> options, ILogger<AmbientWeatherClient> logger)
{
    public const string HttpClientName = "ambient-weather";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly AmbientWeatherOptions _options = options.Value;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private WeatherReading? _cached;
    private DateTimeOffset _cachedAt;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<WeatherReading?> GetCurrentAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        await _fetchLock.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            var http = httpFactory.CreateClient(HttpClientName);
            var url = $"v1/devices?applicationKey={Uri.EscapeDataString(_options.ApplicationKey)}&apiKey={Uri.EscapeDataString(_options.ApiKey)}";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));

            var reading = ParseDevices(doc.RootElement);
            if (reading is not null)
            {
                _cached = reading;
                _cachedAt = DateTimeOffset.UtcNow;
            }
            return reading;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) // includes HttpClient timeouts (TaskCanceledException)
        {
            logger.LogWarning(ex, "Ambient Weather fetch failed");
            return _cached; // serve stale data over nothing
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private WeatherReading? ParseDevices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return null;

        JsonElement device = root[0];
        if (!string.IsNullOrWhiteSpace(_options.DeviceMac))
        {
            foreach (var candidate in root.EnumerateArray())
            {
                if (candidate.TryGetProperty("macAddress", out var mac)
                    && string.Equals(mac.GetString(), _options.DeviceMac, StringComparison.OrdinalIgnoreCase))
                {
                    device = candidate;
                    break;
                }
            }
        }

        if (!device.TryGetProperty("lastData", out var data))
            return null;

        var observedAt = DateTimeOffset.UtcNow;
        if (data.TryGetProperty("dateutc", out var dateUtc) && dateUtc.ValueKind == JsonValueKind.Number)
            observedAt = DateTimeOffset.FromUnixTimeMilliseconds(dateUtc.GetInt64());

        string? stationName = null;
        double? lat = null, lon = null;
        if (device.TryGetProperty("info", out var info))
        {
            if (info.TryGetProperty("name", out var name))
                stationName = name.GetString();
            // info.coords.coords = { lat, lon } — used to center the radar map.
            if (info.TryGetProperty("coords", out var outer) && outer.TryGetProperty("coords", out var coords))
            {
                lat = Num(coords, "lat");
                lon = Num(coords, "lon");
            }
        }

        return new WeatherReading
        {
            ObservedAt = observedAt.ToLocalTime(),
            StationName = stationName,
            StationLat = lat,
            StationLon = lon,
            TempF = Num(data, "tempf"),
            FeelsLikeF = Num(data, "feelsLike"),
            DewPointF = Num(data, "dewPoint"),
            HumidityPercent = Num(data, "humidity"),
            IndoorTempF = Num(data, "tempinf"),
            IndoorHumidityPercent = Num(data, "humidityin"),
            WindSpeedMph = Num(data, "windspeedmph"),
            WindGustMph = Num(data, "windgustmph"),
            WindDirDegrees = Num(data, "winddir"),
            HourlyRainIn = Num(data, "hourlyrainin"),
            DailyRainIn = Num(data, "dailyrainin"),
            BarometerInHg = Num(data, "baromrelin"),
            Uv = Num(data, "uv"),
            SolarRadiationWm2 = Num(data, "solarradiation"),
        };
    }

    private static double? Num(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
