using Labby.Models;
using Labby.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Logs one weather reading every few minutes into a small SQLite file so the
/// Weather page can chart history across restarts. Readings are keyed by the
/// station's own observation time, so re-polling the same report is a no-op.
/// </summary>
public sealed class WeatherHistoryService(
    AmbientWeatherClient weather,
    IOptions<HistoryOptions> options,
    IHostEnvironment env,
    ILogger<WeatherHistoryService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly string _dbPath = Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath);

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!weather.IsConfigured)
        {
            logger.LogInformation("Ambient Weather not configured; weather history logger idle");
            return;
        }

        try
        {
            await InitializeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open weather history database at {Path}; history disabled", _dbPath);
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                if (await weather.GetCurrentAsync(stoppingToken) is { } reading)
                    await InsertAsync(reading, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Weather history sample failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        // WAL lets the page read while the logger writes.
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS weather (
                observed_at   INTEGER PRIMARY KEY,
                temp_f        REAL,
                feels_like_f  REAL,
                humidity      REAL,
                barometer     REAL,
                wind_mph      REAL,
                wind_gust_mph REAL,
                daily_rain_in REAL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Weather history database ready at {Path}", _dbPath);
    }

    private async Task InsertAsync(WeatherReading r, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO weather
                (observed_at, temp_f, feels_like_f, humidity, barometer, wind_mph, wind_gust_mph, daily_rain_in)
            VALUES ($at, $temp, $feels, $hum, $baro, $wind, $gust, $rain);
            """;
        cmd.Parameters.AddWithValue("$at", r.ObservedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$temp", (object?)r.TempF ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$feels", (object?)r.FeelsLikeF ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hum", (object?)r.HumidityPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$baro", (object?)r.BarometerInHg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wind", (object?)r.WindSpeedMph ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gust", (object?)r.WindGustMph ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rain", (object?)r.DailyRainIn ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Readings since <paramref name="from"/>, oldest first.</summary>
    public async Task<IReadOnlyList<WeatherPoint>> GetSinceAsync(DateTimeOffset from, CancellationToken ct = default)
    {
        var results = new List<WeatherPoint>();
        if (!File.Exists(_dbPath))
            return results;

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT observed_at, temp_f, feels_like_f, humidity, barometer, wind_mph, wind_gust_mph, daily_rain_in
            FROM weather WHERE observed_at >= $from ORDER BY observed_at;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new WeatherPoint
            {
                At = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).ToLocalTime(),
                TempF = NullableDouble(reader, 1),
                FeelsLikeF = NullableDouble(reader, 2),
                HumidityPercent = NullableDouble(reader, 3),
                BarometerInHg = NullableDouble(reader, 4),
                WindSpeedMph = NullableDouble(reader, 5),
                WindGustMph = NullableDouble(reader, 6),
                DailyRainIn = NullableDouble(reader, 7),
            });
        }
        return results;
    }

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
