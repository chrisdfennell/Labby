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
}
