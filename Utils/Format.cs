namespace Labby;

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    public static string Uptime(TimeSpan? uptime) =>
        uptime is not { } u ? "—"
        : u.Days > 0 ? $"{u.Days}d {u.Hours}h {u.Minutes}m"
        : $"{u.Hours}h {u.Minutes}m";

    /// <summary>Compact "how long ago" style duration: 45s, 12m, 3h 5m, 2d 6h.</summary>
    public static string ShortDuration(TimeSpan d) =>
        d < TimeSpan.Zero ? "0s"
        : d.TotalSeconds < 60 ? $"{(int)d.TotalSeconds}s"
        : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m"
        : d.TotalHours < 24 ? $"{(int)d.TotalHours}h {d.Minutes}m"
        : $"{(int)d.TotalDays}d {d.Hours}h";
}
