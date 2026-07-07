namespace Labby.Models;

public sealed record NasSystemInfo
{
    public string? ModelName { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? ServerName { get; init; }
    public double? CpuUsagePercent { get; init; }
    public double? TotalMemoryMb { get; init; }
    public double? FreeMemoryMb { get; init; }
    public double? UsedMemoryPercent =>
        TotalMemoryMb is > 0 && FreeMemoryMb is not null
            ? Math.Round((TotalMemoryMb.Value - FreeMemoryMb.Value) / TotalMemoryMb.Value * 100, 1)
            : null;
    public TimeSpan? Uptime { get; init; }
    public double? CpuTempC { get; init; }
    public double? SystemTempC { get; init; }
}

public sealed record NasVolume
{
    public string Label { get; init; } = "";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercent => TotalBytes > 0 ? Math.Round(UsedBytes / (double)TotalBytes * 100, 1) : 0;
}

public sealed record NasDisk
{
    public string Slot { get; init; } = "";
    public string Model { get; init; } = "";
    public string Capacity { get; init; } = "";
    public double? TempC { get; init; }
    public string Health { get; init; } = "";
}

public sealed record FileEntry
{
    public string Name { get; init; } = "";
    public bool IsFolder { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset? Modified { get; init; }
}

public sealed record ContainerInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public string State { get; init; } = "";
    public string Type { get; init; } = "docker";
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);
}

public sealed record ServiceStatus
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string Icon { get; init; } = "🧩";
    public string? Description { get; init; }
    public bool? IsUp { get; init; }
    public long? LatencyMs { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public string? Error { get; init; }
}

public sealed record WeatherReading
{
    public DateTimeOffset ObservedAt { get; init; }
    public string? StationName { get; init; }
    public double? TempF { get; init; }
    public double? FeelsLikeF { get; init; }
    public double? DewPointF { get; init; }
    public double? HumidityPercent { get; init; }
    public double? IndoorTempF { get; init; }
    public double? IndoorHumidityPercent { get; init; }
    public double? WindSpeedMph { get; init; }
    public double? WindGustMph { get; init; }
    public double? WindDirDegrees { get; init; }
    public double? HourlyRainIn { get; init; }
    public double? DailyRainIn { get; init; }
    public double? BarometerInHg { get; init; }
    public double? Uv { get; init; }
    public double? SolarRadiationWm2 { get; init; }

    public string WindDirCompass => WindDirDegrees is not double d
        ? "—"
        : new[] { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" }
            [(int)Math.Round(((d % 360) + 360) % 360 / 22.5) % 16];
}
