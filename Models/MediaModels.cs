namespace Labby.Models;

/// <summary>Active Plex streams, via Tautulli's get_activity.</summary>
public sealed record NowPlayingSnapshot
{
    public IReadOnlyList<PlexSession> Sessions { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record PlexSession
{
    public string User { get; init; } = "";
    public string Title { get; init; } = "";
    public string Player { get; init; } = "";
    public string State { get; init; } = "";
    public double ProgressPercent { get; init; }
    /// <summary>"direct play", "copy", or "transcode".</summary>
    public string Decision { get; init; } = "";
}

/// <summary>Active downloads across qBittorrent and NZBGet.</summary>
public sealed record DownloadsSnapshot
{
    public IReadOnlyList<DownloadItem> Items { get; init; } = [];
    public long DownloadBps { get; init; }
    public long UploadBps { get; init; }
    public string? QbitError { get; init; }
    public string? NzbgetError { get; init; }
}

public sealed record DownloadItem
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public double ProgressPercent { get; init; }
    public long SizeBytes { get; init; }
    public long SpeedBps { get; init; }
    public TimeSpan? Eta { get; init; }
    public string State { get; init; } = "";
}

/// <summary>Upcoming releases from the Sonarr and Radarr calendars.</summary>
public sealed record UpcomingSnapshot
{
    public IReadOnlyList<UpcomingItem> Items { get; init; } = [];
    public string? SonarrError { get; init; }
    public string? RadarrError { get; init; }
}

public sealed record UpcomingItem
{
    public string Title { get; init; } = "";
    /// <summary>Episode code + name for TV, release type for movies.</summary>
    public string Detail { get; init; } = "";
    public DateTimeOffset At { get; init; }
    public string Source { get; init; } = "";
    public bool HasFile { get; init; }
}

/// <summary>Latest additions to the Plex libraries.</summary>
public sealed record RecentlyAddedSnapshot
{
    public IReadOnlyList<RecentItem> Items { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record RecentItem
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public DateTimeOffset AddedAt { get; init; }
}

/// <summary>Pending Overseerr requests.</summary>
public sealed record RequestsSnapshot
{
    public IReadOnlyList<MediaRequest> Requests { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record MediaRequest
{
    public string Title { get; init; } = "";
    public string Type { get; init; } = "";
    public string RequestedBy { get; init; } = "";
    public DateTimeOffset At { get; init; }
}
