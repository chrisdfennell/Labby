namespace Labby.Models;

/// <summary>Active Plex streams, via Tautulli's get_activity.</summary>
public sealed record NowPlayingSnapshot
{
    public IReadOnlyList<PlexSession> Sessions { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record PlexSession
{
    /// <summary>Tautulli session key — used to terminate the stream.</summary>
    public string SessionKey { get; init; } = "";
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
    /// <summary>qBittorrent hash or NZBGet group id — used for pause/resume.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public double ProgressPercent { get; init; }
    public long SizeBytes { get; init; }
    public long SpeedBps { get; init; }
    public TimeSpan? Eta { get; init; }
    public string State { get; init; } = "";
    public bool IsPaused { get; init; }
}

/// <summary>Sonarr/Radarr download queues.</summary>
public sealed record QueueSnapshot
{
    public IReadOnlyList<QueueItem> Items { get; init; } = [];
    public string? SonarrError { get; init; }
    public string? RadarrError { get; init; }
}

public sealed record QueueItem
{
    /// <summary>Queue record id — used to remove/blocklist the item.</summary>
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string Status { get; init; } = "";
    public string? TimeLeft { get; init; }
    public double ProgressPercent { get; init; }
    public long SizeBytes { get; init; }
    /// <summary>Sonarr episode id, for triggering a fresh search after removal.</summary>
    public long? EpisodeId { get; init; }
    /// <summary>Radarr movie id, for triggering a fresh search after removal.</summary>
    public long? MovieId { get; init; }
    /// <summary>Why the download is stuck, when the Arr flags a problem.</summary>
    public string? ErrorMessage { get; init; }
    public bool HasProblem { get; init; }
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

/// <summary>
/// What the library wants but does not have: monitored movies with no file,
/// aired episodes with no file (grouped by season), and movie collections that
/// have been started but not finished.
/// </summary>
public sealed record MissingSnapshot
{
    public IReadOnlyList<MissingMovie> Movies { get; init; } = [];
    /// <summary>Radarr's full missing count, which can exceed the page shown.</summary>
    public int TotalMissingMovies { get; init; }
    public IReadOnlyList<MissingSeason> Seasons { get; init; } = [];
    /// <summary>Sonarr's full missing-episode count, which can exceed the page shown.</summary>
    public int TotalMissingEpisodes { get; init; }
    public IReadOnlyList<CollectionGap> Collections { get; init; } = [];
    public string? SonarrError { get; init; }
    public string? RadarrError { get; init; }
    /// <summary>Collections are a separate Radarr call, so they fail on their own.</summary>
    public string? CollectionsError { get; init; }
}

/// <summary>A movie Radarr monitors but has no file for.</summary>
public sealed record MissingMovie
{
    /// <summary>Radarr movie id, for triggering a search.</summary>
    public long MovieId { get; init; }
    public string Title { get; init; } = "";
    public int? Year { get; init; }
    /// <summary>Earliest known release date across cinema/digital/physical.</summary>
    public DateTimeOffset? ReleasedAt { get; init; }
    /// <summary>Radarr considers it out and grabbable; false means it is still awaited.</summary>
    public bool IsAvailable { get; init; }

    public string Key => $"movie:{MovieId}";
}

/// <summary>One season with aired episodes Sonarr monitors but has no files for.</summary>
public sealed record MissingSeason
{
    /// <summary>Sonarr series id, for triggering a season search.</summary>
    public long SeriesId { get; init; }
    public int SeasonNumber { get; init; }
    public string Series { get; init; } = "";
    public int EpisodeCount { get; init; }
    /// <summary>Episode numbers, abbreviated for display: "E01, E02, E03 +4 more".</summary>
    public string Episodes { get; init; } = "";
    /// <summary>Air date of the most recent missing episode, for ordering.</summary>
    public DateTimeOffset? NewestAirDate { get; init; }

    public string Key => $"season:{SeriesId}:{SeasonNumber}";
}

/// <summary>A movie collection with some films in the library and some not.</summary>
public sealed record CollectionGap
{
    public string Title { get; init; } = "";
    /// <summary>How many films of the collection are already tracked by Radarr.</summary>
    public int OwnedCount { get; init; }
    public IReadOnlyList<CollectionMovie> Missing { get; init; } = [];
}

public sealed record CollectionMovie
{
    public long TmdbId { get; init; }
    public string Title { get; init; } = "";
    public int? Year { get; init; }

    public string Key => $"tmdb:{TmdbId}";
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
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Type { get; init; } = "";
    public string RequestedBy { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>Tautulli watch statistics (last 30 days).</summary>
public sealed record WatchStatsSnapshot
{
    public IReadOnlyList<DateTimeOffset> Days { get; init; } = [];
    public IReadOnlyList<double?> TvPlays { get; init; } = [];
    public IReadOnlyList<double?> MoviePlays { get; init; } = [];
    public IReadOnlyList<TopEntry> TopShows { get; init; } = [];
    public IReadOnlyList<TopEntry> TopMovies { get; init; } = [];
    public IReadOnlyList<TopEntry> TopUsers { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record TopEntry(string Name, long Plays);

/// <summary>Search hits across every configured discovery source, with per-source failures.</summary>
public sealed record SearchSnapshot
{
    public IReadOnlyList<SearchResult> Results { get; init; } = [];
    /// <summary>Pre-formatted "Source: reason" lines for the sources that failed.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>One search result from Overseerr, Radarr, or Sonarr.</summary>
public sealed record SearchResult
{
    /// <summary>TMDB id for Overseerr and Radarr hits; TVDB id for Sonarr hits.</summary>
    public long ExternalId { get; init; }
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public int? Year { get; init; }
    /// <summary>Library status: null = not in library, otherwise "pending"/"processing"/"partial"/"available".</summary>
    public string? Status { get; init; }
    /// <summary>"Overseerr" (request it), or "Radarr"/"Sonarr" (add it straight to the library).</summary>
    public string Source { get; init; } = "Overseerr";
    /// <summary>
    /// Arr hits only: the raw lookup object. Adding POSTs it back with the chosen
    /// root folder and quality profile, so no field the Arr cares about gets lost.
    /// </summary>
    public string? Payload { get; init; }
    /// <summary>Arr hits only: already in that Arr's library.</summary>
    public bool InLibrary { get; init; }

    /// <summary>Stable identity for tracking which rows have been acted on.</summary>
    public string Key => $"{Source}:{Type}:{ExternalId}";
}

/// <summary>Where a Sonarr/Radarr add can land: the root folders and quality profiles it offers.</summary>
public sealed record ArrTargets
{
    public IReadOnlyList<string> RootFolders { get; init; } = [];
    public IReadOnlyList<ArrProfile> QualityProfiles { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record ArrProfile(int Id, string Name);

/// <summary>Prowlarr health messages (empty = all indexers healthy).</summary>
public sealed record IndexerHealthSnapshot
{
    public IReadOnlyList<(string Type, string Message)> Messages { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>ErsatzTV's channel lineup, what each channel is airing, and who is watching.</summary>
public sealed record ErsatzTvSnapshot
{
    public IReadOnlyList<ErsatzTvChannel> Channels { get; init; } = [];

    /// <summary>ErsatzTV's own version string, shown in the card header.</summary>
    public string? Version { get; init; }

    /// <summary>Set when the channel list itself could not be read — the card has nothing to show.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Set when the guide or the session list could not be read. The lineup still
    /// lists; it just has no now/next and cannot say what is being watched.
    /// </summary>
    public string? DetailError { get; init; }

    public int StreamingCount => Channels.Count(c => c.IsStreaming);
}

public sealed record ErsatzTvChannel
{
    /// <summary>The dial number ("5", or "5.1"). ErsatzTV's session and playout routes key off this, not the id.</summary>
    public string Number { get; init; } = "";

    public string Name { get; init; } = "";
    public string StreamingMode { get; init; } = "";

    /// <summary>On air right now per the guide; null when the guide is unreadable or the channel has run dry.</summary>
    public ErsatzTvProgramme? Now { get; init; }
    public ErsatzTvProgramme? Next { get; init; }

    /// <summary>ErsatzTV is transcoding this channel, so somebody has it open.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>The HLS session's state ("active", "idle") when there is one.</summary>
    public string? SessionState { get; init; }
}

/// <summary>One entry from ErsatzTV's XMLTV guide.</summary>
public sealed record ErsatzTvProgramme
{
    public string Title { get; init; } = "";

    /// <summary>Episode title for a series, blank for a movie.</summary>
    public string SubTitle { get; init; } = "";

    /// <summary>On-screen episode number ("S04E24") when the guide carries one.</summary>
    public string Episode { get; init; } = "";

    public DateTimeOffset Start { get; init; }
    public DateTimeOffset Stop { get; init; }

    /// <summary>
    /// How far into the programme it is right now. Read at render time rather than
    /// baked into the snapshot, so the bar keeps creeping between fetches.
    /// </summary>
    public double ProgressPercent
    {
        get
        {
            var total = (Stop - Start).TotalSeconds;
            return total <= 0 ? 0 : Math.Clamp((DateTimeOffset.Now - Start).TotalSeconds / total * 100, 0, 100);
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            var left = Stop - DateTimeOffset.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// "Title Episode · Sub-title", skipping whichever parts the guide omitted. An
    /// ErsatzTV channel is often one show on repeat, so passing the channel name as
    /// <paramref name="omitTitle"/> drops a title that would just say it twice.
    /// </summary>
    public string Describe(string? omitTitle = null)
    {
        var lead = string.Equals(Title, omitTitle, StringComparison.OrdinalIgnoreCase) ? Episode : $"{Title} {Episode}".Trim();
        return string.Join(" · ", new[] { lead, SubTitle }.Where(p => p.Length > 0));
    }
}
