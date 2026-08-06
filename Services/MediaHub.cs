using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Labby.Models;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Aggregates the media stack (Tautulli, Sonarr, Radarr, qBittorrent, NZBGet,
/// Overseerr) for the Media page. Every source is optional and every fetch is
/// isolated — one service being down puts an error on its own card, nothing else.
/// </summary>
public sealed class MediaHub(IHttpClientFactory httpFactory, IOptions<MediaOptions> options, ILogger<MediaHub> logger)
{
    public const string HttpClientName = "media";

    private static readonly TimeSpan LiveTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CalendarTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TargetsTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MissingTtl = TimeSpan.FromMinutes(5);

    /// <summary>How many missing rows each Arr is asked for. Totals are reported separately.</summary>
    private const int MissingPageSize = 200;

    private readonly MediaOptions _options = options.Value;
    private readonly Cached<NowPlayingSnapshot> _nowPlaying = new();
    private readonly Cached<DownloadsSnapshot> _downloads = new();
    private readonly Cached<UpcomingSnapshot> _tv = new();
    private readonly Cached<UpcomingSnapshot> _movies = new();
    private readonly Cached<RequestsSnapshot> _requests = new();
    private readonly Cached<RecentlyAddedSnapshot> _recent = new();
    private readonly Cached<QueueSnapshot> _queue = new();
    private readonly Cached<WatchStatsSnapshot> _watchStats = new();
    private readonly Cached<ArrTargets> _radarrTargets = new();
    private readonly Cached<ArrTargets> _sonarrTargets = new();
    private readonly Cached<MissingSnapshot> _missing = new();
    private readonly Cached<ErsatzTvSnapshot> _ersatzTv = new();
    private readonly Cached<Guide> _guide = new();
    private readonly ConcurrentDictionary<string, string> _titleCache = new();
    private string? _qbitSid;

    public bool AnyConfigured => _options.AnyConfigured;
    public bool TautulliConfigured => _options.Tautulli.IsConfigured;
    public bool SonarrConfigured => _options.Sonarr.IsConfigured;
    public bool RadarrConfigured => _options.Radarr.IsConfigured;
    public bool OverseerrConfigured => _options.Overseerr.IsConfigured;
    public bool PlexConfigured => _options.Plex.IsConfigured;
    public bool ProwlarrConfigured => _options.Prowlarr.IsConfigured;
    public bool ErsatzTvConfigured => _options.ErsatzTv.IsConfigured;
    public bool DownloadsConfigured => _options.Qbittorrent.IsConfigured || _options.Nzbget.IsConfigured;

    /// <summary>The search box appears whenever something can answer it.</summary>
    public bool SearchConfigured => OverseerrConfigured || SonarrConfigured || RadarrConfigured;

    /// <summary>Only the Arrs know what is missing, so the gaps section needs one of them.</summary>
    public bool MissingConfigured => SonarrConfigured || RadarrConfigured;

    public Task<NowPlayingSnapshot> GetNowPlayingAsync(CancellationToken ct = default) =>
        GetCachedAsync(_nowPlaying, LiveTtl, FetchNowPlayingAsync, ct);

    public Task<DownloadsSnapshot> GetDownloadsAsync(CancellationToken ct = default) =>
        GetCachedAsync(_downloads, LiveTtl, FetchDownloadsAsync, ct);

    public Task<UpcomingSnapshot> GetUpcomingTvAsync(CancellationToken ct = default) =>
        GetCachedAsync(_tv, CalendarTtl, FetchSonarrCalendarAsync, ct);

    public Task<UpcomingSnapshot> GetUpcomingMoviesAsync(CancellationToken ct = default) =>
        GetCachedAsync(_movies, CalendarTtl, FetchRadarrCalendarAsync, ct);

    public Task<RequestsSnapshot> GetRequestsAsync(CancellationToken ct = default) =>
        GetCachedAsync(_requests, CalendarTtl, FetchRequestsAsync, ct);

    public Task<RecentlyAddedSnapshot> GetRecentlyAddedAsync(CancellationToken ct = default) =>
        GetCachedAsync(_recent, CalendarTtl, FetchRecentlyAddedAsync, ct);

    public Task<QueueSnapshot> GetQueueAsync(CancellationToken ct = default) =>
        GetCachedAsync(_queue, LiveTtl, FetchQueueAsync, ct);

    public Task<WatchStatsSnapshot> GetWatchStatsAsync(CancellationToken ct = default) =>
        GetCachedAsync(_watchStats, CalendarTtl, FetchWatchStatsAsync, ct);

    public Task<MissingSnapshot> GetMissingAsync(CancellationToken ct = default) =>
        GetCachedAsync(_missing, MissingTtl, FetchMissingAsync, ct);

    public Task<ErsatzTvSnapshot> GetErsatzTvAsync(CancellationToken ct = default) =>
        GetCachedAsync(_ersatzTv, LiveTtl, FetchErsatzTvAsync, ct);

    /// <summary>Stops a Plex stream via Tautulli.</summary>
    public async Task TerminateSessionAsync(string sessionKey, CancellationToken ct = default)
    {
        var url = $"{_options.Tautulli.Url.TrimEnd('/')}/api/v2?apikey={Uri.EscapeDataString(_options.Tautulli.ApiKey)}" +
                  $"&cmd=terminate_session&session_key={Uri.EscapeDataString(sessionKey)}" +
                  $"&message={Uri.EscapeDataString("Stream stopped from Labby.")}";
        using var doc = await GetJsonAsync(url, ct);
        var result = doc.RootElement.GetProperty("response");
        if (Str(result, "result") != "success")
            throw new InvalidOperationException(Str(result, "message") is { Length: > 0 } m ? m : "Tautulli refused to terminate the session.");
        _nowPlaying.At = DateTimeOffset.MinValue;
    }

    /// <summary>Sends a magnet/torrent link to qBittorrent or an NZB URL to NZBGet.</summary>
    public async Task AddDownloadAsync(string link, string target, CancellationToken ct = default)
    {
        if (target == "qBittorrent")
        {
            var baseUrl = _options.Qbittorrent.Url.TrimEnd('/');
            var http = httpFactory.CreateClient(HttpClientName);

            async Task<HttpResponseMessage> ActAsync()
            {
                using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("urls", link)]);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/torrents/add") { Content = content };
                if (_qbitSid is { } sid)
                    request.Headers.Add("Cookie", $"SID={sid}");
                return await http.SendAsync(request, ct);
            }

            var response = await ActAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && !string.IsNullOrEmpty(_options.Qbittorrent.Password))
            {
                response.Dispose();
                await QbitLoginAsync(http, baseUrl, ct);
                response = await ActAsync();
            }
            using (response)
            {
                response.EnsureSuccessStatusCode();
            }
        }
        else
        {
            var baseUrl = _options.Nzbget.Url.TrimEnd('/');
            var auth = string.IsNullOrEmpty(_options.Nzbget.Username)
                ? ""
                : $"/{Uri.EscapeDataString(_options.Nzbget.Username)}:{Uri.EscapeDataString(_options.Nzbget.Password)}";
            var http = httpFactory.CreateClient(HttpClientName);
            // append(NZBFilename, Content, Category, Priority, AddToTop, AddPaused, DupeKey, DupeScore, DupeMode)
            // — Content accepts a URL, which NZBGet fetches itself.
            using var content = JsonContent.Create(new
            {
                method = "append",
                @params = new object[] { "", link, "", 0, false, false, "", 0, "SCORE" },
            });
            using var response = await http.PostAsync($"{baseUrl}{auth}/jsonrpc", content, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Number && result.GetInt64() <= 0)
                throw new InvalidOperationException("NZBGet rejected the link (is it a valid NZB URL?).");
        }

        _downloads.At = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Searches every configured discovery source at once: Overseerr (which raises a
    /// request) plus Sonarr and Radarr directly (which add straight to the library).
    /// Sources fail independently — one being down still shows the others' hits.
    /// </summary>
    public async Task<SearchSnapshot> SearchAsync(string query, CancellationToken ct = default)
    {
        // Kick all three off before awaiting any, so the slowest one sets the pace.
        var searches = new (string Source, Task<List<SearchResult>>? Task)[]
        {
            ("Overseerr", _options.Overseerr.IsConfigured ? SearchOverseerrAsync(query, ct) : null),
            ("Radarr", _options.Radarr.IsConfigured ? SearchRadarrAsync(query, ct) : null),
            ("Sonarr", _options.Sonarr.IsConfigured ? SearchSonarrAsync(query, ct) : null),
        };

        var results = new List<SearchResult>();
        var errors = new List<string>();
        foreach (var (source, task) in searches)
        {
            if (task is null)
                continue;
            try
            {
                results.AddRange(await task);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Source} search failed", source);
                errors.Add($"{source}: {Describe(ex)}");
            }
        }
        return new SearchSnapshot { Results = results, Errors = errors };
    }

    private async Task<List<SearchResult>> SearchOverseerrAsync(string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"{_options.Overseerr.Url.TrimEnd('/')}/api/v1/search?page=1&query={Uri.EscapeDataString(query)}",
            ct, apiKey: _options.Overseerr.ApiKey);
        var results = new List<SearchResult>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var type = Str(r, "mediaType");
            if (type is not ("movie" or "tv"))
                continue;
            var date = Str(r, type == "movie" ? "releaseDate" : "firstAirDate");
            var status = r.TryGetProperty("mediaInfo", out var info) ? Num(info, "status") : null;
            results.Add(new SearchResult
            {
                ExternalId = (long)(Num(r, "id") ?? 0),
                Type = type,
                Title = type == "movie" ? Str(r, "title") : Str(r, "name"),
                Year = date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : null,
                Status = status switch { 2 => "pending", 3 => "requested", 4 => "partial", 5 => "available", _ => null },
                Source = "Overseerr",
            });
        }
        return results.Take(12).ToList();
    }

    // Radarr's lookup hits TMDB and returns full movie objects — the same shape
    // POST /movie wants back, so the raw text rides along as the add payload.
    private async Task<List<SearchResult>> SearchRadarrAsync(string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"{_options.Radarr.Url.TrimEnd('/')}/api/v3/movie/lookup?term={Uri.EscapeDataString(query)}",
            ct, apiKey: _options.Radarr.ApiKey);
        var results = new List<SearchResult>();
        foreach (var m in doc.RootElement.EnumerateArray().Take(8))
        {
            // A non-zero id means Radarr already tracks it; hasFile means it is downloaded.
            var inLibrary = (Num(m, "id") ?? 0) > 0;
            results.Add(new SearchResult
            {
                ExternalId = (long)(Num(m, "tmdbId") ?? 0),
                Type = "movie",
                Title = Str(m, "title"),
                Year = Num(m, "year") is { } y and > 0 ? (int)y : null,
                Source = "Radarr",
                Payload = m.GetRawText(),
                InLibrary = inLibrary,
                Status = !inLibrary ? null
                    : m.TryGetProperty("hasFile", out var hf) && hf.ValueKind == JsonValueKind.True ? "downloaded"
                    : "in library",
            });
        }
        return results;
    }

    private async Task<List<SearchResult>> SearchSonarrAsync(string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"{_options.Sonarr.Url.TrimEnd('/')}/api/v3/series/lookup?term={Uri.EscapeDataString(query)}",
            ct, apiKey: _options.Sonarr.ApiKey);
        var results = new List<SearchResult>();
        foreach (var s in doc.RootElement.EnumerateArray().Take(8))
        {
            var inLibrary = (Num(s, "id") ?? 0) > 0;
            var onDisk = s.TryGetProperty("statistics", out var stats) ? Num(stats, "episodeFileCount") ?? 0 : 0;
            results.Add(new SearchResult
            {
                ExternalId = (long)(Num(s, "tvdbId") ?? 0),
                Type = "tv",
                Title = Str(s, "title"),
                Year = Num(s, "year") is { } y and > 0 ? (int)y : null,
                Source = "Sonarr",
                Payload = s.GetRawText(),
                InLibrary = inLibrary,
                Status = !inLibrary ? null : onDisk > 0 ? $"{onDisk:0} episodes" : "in library",
            });
        }
        return results;
    }

    /// <summary>
    /// Root folders and quality profiles offered by one Arr, for the add form.
    /// Both change rarely, so they are cached for half an hour.
    /// </summary>
    public Task<ArrTargets> GetArrTargetsAsync(string source, CancellationToken ct = default) =>
        GetCachedAsync(source == "Radarr" ? _radarrTargets : _sonarrTargets, TargetsTtl,
            token => FetchArrTargetsAsync(source, token), ct);

    private async Task<ArrTargets> FetchArrTargetsAsync(string source, CancellationToken ct)
    {
        var (baseUrl, apiKey) = ArrEndpoint(source);
        try
        {
            using var folders = await GetJsonAsync($"{baseUrl}/api/v3/rootfolder", ct, apiKey);
            using var profiles = await GetJsonAsync($"{baseUrl}/api/v3/qualityprofile", ct, apiKey);
            return new ArrTargets
            {
                RootFolders = folders.RootElement.EnumerateArray()
                    .Select(f => Str(f, "path"))
                    .Where(p => p.Length > 0)
                    .ToList(),
                QualityProfiles = profiles.RootElement.EnumerateArray()
                    .Select(p => new ArrProfile((int)(Num(p, "id") ?? 0), Str(p, "name")))
                    .Where(p => p.Id > 0)
                    .ToList(),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Source} targets fetch failed", source);
            return new ArrTargets { Error = Describe(ex) };
        }
    }

    /// <summary>
    /// Adds a Sonarr/Radarr search hit to that Arr's library, monitored, and kicks
    /// off a search for it. The lookup object is posted back verbatim apart from the
    /// fields the add needs, which is what the Arr UIs themselves do.
    /// </summary>
    public async Task AddToArrAsync(SearchResult result, string rootFolderPath, int qualityProfileId, CancellationToken ct = default)
    {
        if (result.Payload is not { Length: > 0 } payload)
            throw new InvalidOperationException($"{result.Source} did not return an addable record.");
        if (string.IsNullOrWhiteSpace(rootFolderPath))
            throw new InvalidOperationException("Pick a root folder first.");
        if (qualityProfileId <= 0)
            throw new InvalidOperationException("Pick a quality profile first.");

        var body = System.Text.Json.Nodes.JsonNode.Parse(payload)?.AsObject()
                   ?? throw new InvalidOperationException("Unreadable lookup record.");
        body.Remove("id"); // lookup returns 0 for unknown titles; the Arr assigns the real one
        body["qualityProfileId"] = qualityProfileId;
        body["rootFolderPath"] = rootFolderPath;
        body["monitored"] = true;
        if (result.Source == "Radarr")
        {
            body["minimumAvailability"] = "released";
            body["addOptions"] = new System.Text.Json.Nodes.JsonObject { ["searchForMovie"] = true };
        }
        else
        {
            body["seasonFolder"] = true;
            body["languageProfileId"] = 1; // required by Sonarr v3, ignored by v4
            body["addOptions"] = new System.Text.Json.Nodes.JsonObject
            {
                ["monitor"] = "all",
                ["searchForMissingEpisodes"] = true,
                ["searchForCutoffUnmetEpisodes"] = false,
            };
        }

        var (baseUrl, apiKey) = ArrEndpoint(result.Source);
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl}/api/v3/{(result.Source == "Radarr" ? "movie" : "series")}")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ArrErrorAsync(response, ct));

        _queue.At = DateTimeOffset.MinValue; // the search it triggers should show up promptly
    }

    private (string BaseUrl, string ApiKey) ArrEndpoint(string source) =>
        source == "Radarr"
            ? (_options.Radarr.Url.TrimEnd('/'), _options.Radarr.ApiKey)
            : (_options.Sonarr.Url.TrimEnd('/'), _options.Sonarr.ApiKey);

    // Arr rejections come back as [{ "errorMessage": "..." }] or { "message": "..." };
    // the raw status code alone ("400 Bad Request") tells the user nothing.
    private static async Task<string> ArrErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var messages = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => Str(e, "errorMessage"))
                : [Str(doc.RootElement, "message")];
            if (string.Join("; ", messages.Where(m => m.Length > 0)) is { Length: > 0 } detail)
                return detail;
        }
        catch (Exception)
        {
            // fall through to the status line
        }
        return $"rejected the add ({(int)response.StatusCode} {response.ReasonPhrase}).";
    }

    /// <summary>Submits a new request (all seasons for TV).</summary>
    public async Task SubmitRequestAsync(long tmdbId, string mediaType, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.Overseerr.Url.TrimEnd('/')}/api/v1/request")
        {
            Content = mediaType == "tv"
                ? JsonContent.Create(new { mediaType, mediaId = tmdbId, seasons = "all" })
                : JsonContent.Create(new { mediaType, mediaId = tmdbId }),
        };
        request.Headers.Add("X-Api-Key", _options.Overseerr.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        _requests.At = DateTimeOffset.MinValue;
    }

    /// <summary>Prowlarr health messages (indexer failures and warnings).</summary>
    public async Task<IndexerHealthSnapshot> GetIndexerHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync($"{_options.Prowlarr.Url.TrimEnd('/')}/api/v1/health", ct, apiKey: _options.Prowlarr.ApiKey);
            var messages = new List<(string, string)>();
            foreach (var m in doc.RootElement.EnumerateArray())
                messages.Add((Str(m, "type"), Str(m, "message")));
            return new IndexerHealthSnapshot { Messages = messages };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prowlarr fetch failed");
            return new IndexerHealthSnapshot { Error = Describe(ex) };
        }
    }

    /// <summary>Sonarr + Radarr releases for an arbitrary window (the Calendar page).</summary>
    public async Task<IReadOnlyList<UpcomingItem>> GetCalendarAsync(DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        var items = new List<UpcomingItem>();
        if (_options.Sonarr.IsConfigured)
        {
            using var doc = await GetJsonAsync(
                $"{_options.Sonarr.Url.TrimEnd('/')}/api/v3/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&includeSeries=true",
                ct, apiKey: _options.Sonarr.ApiKey);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var series = e.TryGetProperty("series", out var s) ? Str(s, "title") : "";
                items.Add(new UpcomingItem
                {
                    Title = series,
                    Detail = $"S{Num(e, "seasonNumber"):00}E{Num(e, "episodeNumber"):00}",
                    At = Date(e, "airDateUtc") ?? DateTimeOffset.Now,
                    Source = "Sonarr",
                    HasFile = e.TryGetProperty("hasFile", out var hf) && hf.ValueKind == JsonValueKind.True,
                });
            }
        }
        if (_options.Radarr.IsConfigured)
        {
            using var doc = await GetJsonAsync(
                $"{_options.Radarr.Url.TrimEnd('/')}/api/v3/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}",
                ct, apiKey: _options.Radarr.ApiKey);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var releases = new (string Label, DateTimeOffset? At)[]
                {
                    ("cinema", Date(e, "inCinemas")),
                    ("digital", Date(e, "digitalRelease")),
                    ("physical", Date(e, "physicalRelease")),
                };
                foreach (var (label, at) in releases)
                {
                    if (at is { } d && DateOnly.FromDateTime(d.Date) >= start && DateOnly.FromDateTime(d.Date) <= end)
                    {
                        items.Add(new UpcomingItem
                        {
                            Title = Str(e, "title"),
                            Detail = label,
                            At = d,
                            Source = "Radarr",
                            HasFile = e.TryGetProperty("hasFile", out var hf) && hf.ValueKind == JsonValueKind.True,
                        });
                    }
                }
            }
        }
        return items.OrderBy(i => i.At).ToList();
    }

    /// <summary>Approve or decline a pending Overseerr/Seerr request.</summary>
    public async Task ResolveRequestAsync(long requestId, bool approve, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.Overseerr.Url.TrimEnd('/')}/api/v1/request/{requestId}/{(approve ? "approve" : "decline")}");
        request.Headers.Add("X-Api-Key", _options.Overseerr.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        _requests.At = DateTimeOffset.MinValue; // refresh the card on the next read
    }

    /// <summary>Pause or resume a download and drop the cache so the next poll shows it.</summary>
    public async Task SetDownloadPausedAsync(DownloadItem item, bool pause, CancellationToken ct = default)
    {
        if (item.Source == "qBittorrent")
        {
            var baseUrl = _options.Qbittorrent.Url.TrimEnd('/');
            var http = httpFactory.CreateClient(HttpClientName);

            async Task<HttpResponseMessage> ActAsync()
            {
                using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("hashes", item.Id)]);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/torrents/{(pause ? "pause" : "resume")}")
                {
                    Content = content,
                };
                if (_qbitSid is { } sid)
                    request.Headers.Add("Cookie", $"SID={sid}");
                return await http.SendAsync(request, ct);
            }

            var response = await ActAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && !string.IsNullOrEmpty(_options.Qbittorrent.Password))
            {
                response.Dispose();
                await QbitLoginAsync(http, baseUrl, ct);
                response = await ActAsync();
            }
            using (response)
            {
                response.EnsureSuccessStatusCode();
            }
        }
        else if (item.Source == "NZBGet")
        {
            var baseUrl = _options.Nzbget.Url.TrimEnd('/');
            var auth = string.IsNullOrEmpty(_options.Nzbget.Username)
                ? ""
                : $"/{Uri.EscapeDataString(_options.Nzbget.Username)}:{Uri.EscapeDataString(_options.Nzbget.Password)}";
            var http = httpFactory.CreateClient(HttpClientName);
            using var content = JsonContent.Create(new
            {
                method = "editqueue",
                @params = new object[] { pause ? "GroupPause" : "GroupResume", "", new[] { long.Parse(item.Id) } },
            });
            using var response = await http.PostAsync($"{baseUrl}{auth}/jsonrpc", content, ct);
            response.EnsureSuccessStatusCode();
        }

        _downloads.At = DateTimeOffset.MinValue; // force a fresh fetch on the next read
    }

    /// <summary>
    /// Removes a stuck queue item (blocklisting the release so it isn't grabbed
    /// again) and immediately searches for a replacement.
    /// </summary>
    public async Task RetryQueueItemAsync(QueueItem item, CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = item.Source == "Sonarr"
            ? (_options.Sonarr.Url.TrimEnd('/'), _options.Sonarr.ApiKey)
            : (_options.Radarr.Url.TrimEnd('/'), _options.Radarr.ApiKey);
        var http = httpFactory.CreateClient(HttpClientName);

        using (var delete = new HttpRequestMessage(HttpMethod.Delete,
            $"{baseUrl}/api/v3/queue/{item.Id}?removeFromClient=true&blocklist=true"))
        {
            delete.Headers.Add("X-Api-Key", apiKey);
            using var response = await http.SendAsync(delete, ct);
            response.EnsureSuccessStatusCode();
        }

        object? command = item switch
        {
            { Source: "Sonarr", EpisodeId: { } ep } => new { name = "EpisodeSearch", episodeIds = new[] { ep } },
            { Source: "Radarr", MovieId: { } movie } => new { name = "MoviesSearch", movieIds = new[] { movie } },
            _ => null,
        };
        if (command is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/command")
            {
                Content = JsonContent.Create(command),
            };
            request.Headers.Add("X-Api-Key", apiKey);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }

        _queue.At = DateTimeOffset.MinValue;
    }

    // ── Tautulli ─────────────────────────────────────────────────────────

    private async Task<NowPlayingSnapshot> FetchNowPlayingAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{_options.Tautulli.Url.TrimEnd('/')}/api/v2?apikey={Uri.EscapeDataString(_options.Tautulli.ApiKey)}&cmd=get_activity";
            using var doc = await GetJsonAsync(url, ct);
            var sessions = new List<PlexSession>();
            if (doc.RootElement.TryGetProperty("response", out var resp)
                && resp.TryGetProperty("data", out var data)
                && data.TryGetProperty("sessions", out var list))
            {
                foreach (var s in list.EnumerateArray())
                {
                    sessions.Add(new PlexSession
                    {
                        SessionKey = Str(s, "session_key") is { Length: > 0 } key ? key : $"{Num(s, "session_key")}",
                        User = Str(s, "friendly_name") is { Length: > 0 } n ? n : Str(s, "user"),
                        Title = Str(s, "full_title"),
                        Player = Str(s, "player"),
                        State = Str(s, "state"),
                        ProgressPercent = Num(s, "progress_percent") ?? 0,
                        Decision = Str(s, "transcode_decision"),
                    });
                }
            }
            return new NowPlayingSnapshot { Sessions = sessions };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tautulli fetch failed");
            return new NowPlayingSnapshot { Error = Describe(ex) };
        }
    }

    // ── Sonarr / Radarr calendars ────────────────────────────────────────

    private async Task<UpcomingSnapshot> FetchSonarrCalendarAsync(CancellationToken ct)
    {
        try
        {
            var (start, end) = (DateTime.Today, DateTime.Today.AddDays(7));
            var url = $"{_options.Sonarr.Url.TrimEnd('/')}/api/v3/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&includeSeries=true";
            using var doc = await GetJsonAsync(url, ct, apiKey: _options.Sonarr.ApiKey);
            var items = new List<UpcomingItem>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var series = e.TryGetProperty("series", out var s) ? Str(s, "title") : "";
                items.Add(new UpcomingItem
                {
                    Title = series,
                    Detail = $"S{Num(e, "seasonNumber"):00}E{Num(e, "episodeNumber"):00} · {Str(e, "title")}",
                    At = Date(e, "airDateUtc") ?? DateTimeOffset.Now,
                    Source = "Sonarr",
                    HasFile = e.TryGetProperty("hasFile", out var hf) && hf.ValueKind == JsonValueKind.True,
                });
            }
            return new UpcomingSnapshot { Items = items.OrderBy(i => i.At).ToList() };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sonarr fetch failed");
            return new UpcomingSnapshot { SonarrError = Describe(ex) };
        }
    }

    private async Task<UpcomingSnapshot> FetchRadarrCalendarAsync(CancellationToken ct)
    {
        try
        {
            var (start, end) = (DateTime.Today, DateTime.Today.AddDays(30));
            var url = $"{_options.Radarr.Url.TrimEnd('/')}/api/v3/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}";
            using var doc = await GetJsonAsync(url, ct, apiKey: _options.Radarr.ApiKey);
            var items = new List<UpcomingItem>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                // A movie appears on the calendar for whichever release dates fall in the window.
                var releases = new (string Label, DateTimeOffset? At)[]
                {
                    ("In cinemas", Date(e, "inCinemas")),
                    ("Digital release", Date(e, "digitalRelease")),
                    ("Physical release", Date(e, "physicalRelease")),
                };
                var next = releases
                    .Where(r => r.At is { } d && d >= start && d <= end.AddDays(1))
                    .OrderBy(r => r.At)
                    .FirstOrDefault();
                if (next.At is null)
                    continue;
                items.Add(new UpcomingItem
                {
                    Title = $"{Str(e, "title")} ({Num(e, "year"):0})",
                    Detail = next.Label,
                    At = next.At.Value,
                    Source = "Radarr",
                    HasFile = e.TryGetProperty("hasFile", out var hf) && hf.ValueKind == JsonValueKind.True,
                });
            }
            return new UpcomingSnapshot { Items = items.OrderBy(i => i.At).ToList() };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Radarr fetch failed");
            return new UpcomingSnapshot { RadarrError = Describe(ex) };
        }
    }

    // ── Downloads (qBittorrent + NZBGet) ─────────────────────────────────

    private async Task<DownloadsSnapshot> FetchDownloadsAsync(CancellationToken ct)
    {
        var items = new List<DownloadItem>();
        long down = 0, up = 0;
        string? qbitError = null, nzbError = null;

        // Both clients concurrently — a hanging one costs its own timeout, not the sum.
        var qbitTask = _options.Qbittorrent.IsConfigured ? FetchQbittorrentAsync(ct) : null;
        var nzbTask = _options.Nzbget.IsConfigured ? FetchNzbgetAsync(ct) : null;

        if (qbitTask is not null)
        {
            try
            {
                var (qbitItems, qbitDown, qbitUp) = await qbitTask;
                items.AddRange(qbitItems);
                down += qbitDown;
                up += qbitUp;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "qBittorrent fetch failed");
                qbitError = Describe(ex);
            }
        }

        if (nzbTask is not null)
        {
            try
            {
                var (nzbItems, nzbDown) = await nzbTask;
                items.AddRange(nzbItems);
                down += nzbDown;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "NZBGet fetch failed");
                nzbError = Describe(ex);
            }
        }

        return new DownloadsSnapshot
        {
            Items = items.OrderByDescending(i => i.SpeedBps).ToList(),
            DownloadBps = down,
            UploadBps = up,
            QbitError = qbitError,
            NzbgetError = nzbError,
        };
    }

    private async Task<(List<DownloadItem> Items, long Down, long Up)> FetchQbittorrentAsync(CancellationToken ct)
    {
        var baseUrl = _options.Qbittorrent.Url.TrimEnd('/');
        var http = httpFactory.CreateClient(HttpClientName);

        async Task<HttpResponseMessage> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{path}");
            if (_qbitSid is { } sid)
                request.Headers.Add("Cookie", $"SID={sid}");
            return await http.SendAsync(request, ct);
        }

        var response = await GetAsync("/api/v2/torrents/info?filter=downloading");
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && !string.IsNullOrEmpty(_options.Qbittorrent.Password))
        {
            response.Dispose();
            await QbitLoginAsync(http, baseUrl, ct);
            response = await GetAsync("/api/v2/torrents/info?filter=downloading");
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var items = new List<DownloadItem>();
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var eta = (long)(Num(t, "eta") ?? 0);
                var state = Str(t, "state");
                items.Add(new DownloadItem
                {
                    Id = Str(t, "hash"),
                    Name = Str(t, "name"),
                    Source = "qBittorrent",
                    ProgressPercent = Math.Round((Num(t, "progress") ?? 0) * 100, 1),
                    SizeBytes = (long)(Num(t, "size") ?? 0),
                    SpeedBps = (long)(Num(t, "dlspeed") ?? 0),
                    Eta = eta is > 0 and < 8_640_000 ? TimeSpan.FromSeconds(eta) : null,
                    State = state,
                    IsPaused = state.Contains("paused", StringComparison.OrdinalIgnoreCase)
                               || state.Contains("stopped", StringComparison.OrdinalIgnoreCase),
                });
            }

            using var info = await GetAsync("/api/v2/transfer/info");
            info.EnsureSuccessStatusCode();
            using var infoDoc = JsonDocument.Parse(await info.Content.ReadAsStringAsync(ct));
            return (items, (long)(Num(infoDoc.RootElement, "dl_info_speed") ?? 0), (long)(Num(infoDoc.RootElement, "up_info_speed") ?? 0));
        }
    }

    private async Task QbitLoginAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", _options.Qbittorrent.Username),
            new KeyValuePair<string, string>("password", _options.Qbittorrent.Password),
        ]);
        using var response = await http.PostAsync($"{baseUrl}/api/v2/auth/login", content, ct);
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(c => c.StartsWith("SID=", StringComparison.Ordinal))
            : null;
        _qbitSid = cookie?.Split(';')[0]["SID=".Length..]
            ?? throw new InvalidOperationException("qBittorrent login refused (check username/password)");
    }

    private async Task<(List<DownloadItem> Items, long Down)> FetchNzbgetAsync(CancellationToken ct)
    {
        var baseUrl = _options.Nzbget.Url.TrimEnd('/');
        var auth = string.IsNullOrEmpty(_options.Nzbget.Username)
            ? ""
            : $"/{Uri.EscapeDataString(_options.Nzbget.Username)}:{Uri.EscapeDataString(_options.Nzbget.Password)}";

        using var statusDoc = await GetJsonAsync($"{baseUrl}{auth}/jsonrpc/status", ct);
        var result = statusDoc.RootElement.GetProperty("result");
        var rate = (long)(Num(result, "DownloadRate") ?? 0);

        using var groupsDoc = await GetJsonAsync($"{baseUrl}{auth}/jsonrpc/listgroups", ct);
        var items = new List<DownloadItem>();
        foreach (var g in groupsDoc.RootElement.GetProperty("result").EnumerateArray())
        {
            var totalMb = Num(g, "FileSizeMB") ?? 0;
            var remainingMb = Num(g, "RemainingSizeMB") ?? 0;
            var status = Str(g, "Status").ToLowerInvariant();
            items.Add(new DownloadItem
            {
                Id = ((long)(Num(g, "NZBID") ?? 0)).ToString(),
                Name = Str(g, "NZBName"),
                Source = "NZBGet",
                ProgressPercent = totalMb > 0 ? Math.Round((totalMb - remainingMb) / totalMb * 100, 1) : 0,
                SizeBytes = (long)(totalMb * 1024 * 1024),
                SpeedBps = rate, // NZBGet reports one global rate, not per-item
                Eta = rate > 0 ? TimeSpan.FromSeconds(remainingMb * 1024 * 1024 / rate) : null,
                State = status,
                IsPaused = status.Contains("paused"),
            });
        }
        return (items, rate);
    }

    // ── Tautulli watch statistics ────────────────────────────────────────

    private async Task<WatchStatsSnapshot> FetchWatchStatsAsync(CancellationToken ct)
    {
        try
        {
            var baseUrl = $"{_options.Tautulli.Url.TrimEnd('/')}/api/v2?apikey={Uri.EscapeDataString(_options.Tautulli.ApiKey)}";

            using var playsDoc = await GetJsonAsync($"{baseUrl}&cmd=get_plays_by_date&time_range=30", ct);
            var playsData = playsDoc.RootElement.GetProperty("response").GetProperty("data");
            var days = new List<DateTimeOffset>();
            foreach (var c in playsData.GetProperty("categories").EnumerateArray())
            {
                days.Add(DateTimeOffset.TryParse(c.GetString(), out var d) ? d : DateTimeOffset.Now);
            }
            List<double?> SeriesFor(string name)
            {
                foreach (var s in playsData.GetProperty("series").EnumerateArray())
                {
                    if (Str(s, "name").Equals(name, StringComparison.OrdinalIgnoreCase) && s.TryGetProperty("data", out var data))
                        return data.EnumerateArray().Select(v => (double?)v.GetDouble()).ToList();
                }
                return [];
            }

            using var statsDoc = await GetJsonAsync($"{baseUrl}&cmd=get_home_stats&time_range=30&stats_count=5", ct);
            var top = new Dictionary<string, List<TopEntry>>();
            foreach (var stat in statsDoc.RootElement.GetProperty("response").GetProperty("data").EnumerateArray())
            {
                var id = Str(stat, "stat_id");
                if (id is not ("top_tv" or "top_movies" or "top_users") || !stat.TryGetProperty("rows", out var rows))
                    continue;
                top[id] = rows.EnumerateArray()
                    .Select(r => new TopEntry(
                        id == "top_users"
                            ? (Str(r, "friendly_name") is { Length: > 0 } f ? f : Str(r, "user"))
                            : Str(r, "title"),
                        (long)(Num(r, "total_plays") ?? 0)))
                    .Where(e => e.Name.Length > 0)
                    .ToList();
            }

            return new WatchStatsSnapshot
            {
                Days = days,
                TvPlays = SeriesFor("TV"),
                MoviePlays = SeriesFor("Movies"),
                TopShows = top.GetValueOrDefault("top_tv", []),
                TopMovies = top.GetValueOrDefault("top_movies", []),
                TopUsers = top.GetValueOrDefault("top_users", []),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tautulli stats fetch failed");
            return new WatchStatsSnapshot { Error = Describe(ex) };
        }
    }

    // ── Sonarr/Radarr queues ─────────────────────────────────────────────

    private async Task<QueueSnapshot> FetchQueueAsync(CancellationToken ct)
    {
        var items = new List<QueueItem>();
        string? sonarrError = null, radarrError = null;

        if (_options.Sonarr.IsConfigured)
        {
            try
            {
                using var doc = await GetJsonAsync(
                    $"{_options.Sonarr.Url.TrimEnd('/')}/api/v3/queue?pageSize=30&includeSeries=true&includeEpisode=true",
                    ct, apiKey: _options.Sonarr.ApiKey);
                foreach (var r in doc.RootElement.GetProperty("records").EnumerateArray())
                {
                    var series = r.TryGetProperty("series", out var s) ? Str(s, "title") : "";
                    var episode = r.TryGetProperty("episode", out var e)
                        ? $" S{Num(e, "seasonNumber"):00}E{Num(e, "episodeNumber"):00}"
                        : "";
                    items.Add(ToQueueItem(r, "Sonarr", $"{series}{episode}"));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sonarr queue fetch failed");
                sonarrError = Describe(ex);
            }
        }

        if (_options.Radarr.IsConfigured)
        {
            try
            {
                using var doc = await GetJsonAsync(
                    $"{_options.Radarr.Url.TrimEnd('/')}/api/v3/queue?pageSize=30&includeMovie=true",
                    ct, apiKey: _options.Radarr.ApiKey);
                foreach (var r in doc.RootElement.GetProperty("records").EnumerateArray())
                {
                    var movie = r.TryGetProperty("movie", out var m) ? Str(m, "title") : "";
                    items.Add(ToQueueItem(r, "Radarr", movie));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Radarr queue fetch failed");
                radarrError = Describe(ex);
            }
        }

        return new QueueSnapshot
        {
            Items = items.OrderBy(i => i.Status == "completed").ThenBy(i => i.Title).ToList(),
            SonarrError = sonarrError,
            RadarrError = radarrError,
        };
    }

    private static QueueItem ToQueueItem(JsonElement record, string source, string knownTitle)
    {
        var size = Num(record, "size") ?? 0;
        var left = Num(record, "sizeleft") ?? 0;
        // Stuck downloads surface as trackedDownloadStatus "warning"/"error" with
        // statusMessages explaining why (stalled, unpacking failed, import blocked…).
        var trackedStatus = Str(record, "trackedDownloadStatus");
        var problem = Str(record, "errorMessage");
        if (problem.Length == 0 && record.TryGetProperty("statusMessages", out var messages))
        {
            problem = string.Join("; ", messages.EnumerateArray()
                .SelectMany(m => m.TryGetProperty("messages", out var inner)
                    ? inner.EnumerateArray().Select(v => v.GetString() ?? "")
                    : [Str(m, "title")])
                .Where(m => m.Length > 0));
        }
        return new QueueItem
        {
            Id = (long)(Num(record, "id") ?? 0),
            Title = knownTitle is { Length: > 0 } ? knownTitle : Str(record, "title"),
            Source = source,
            Status = Str(record, "status"),
            TimeLeft = Str(record, "timeleft") is { Length: > 0 } t ? t : null,
            ProgressPercent = size > 0 ? Math.Round((size - left) / size * 100, 1) : 0,
            SizeBytes = (long)size,
            EpisodeId = Num(record, "episodeId") is { } ep ? (long)ep : null,
            MovieId = Num(record, "movieId") is { } movie ? (long)movie : null,
            ErrorMessage = problem.Length > 0 ? problem : null,
            HasProblem = trackedStatus is "warning" or "error" || Str(record, "status") is "warning" or "failed",
        };
    }

    // ── Missing / wanted ─────────────────────────────────────────────────

    /// <summary>
    /// Three independent gap questions answered at once: which monitored movies
    /// have no file, which aired episodes have no file, and which movie
    /// collections have been started but never finished. Each fails on its own.
    /// </summary>
    private async Task<MissingSnapshot> FetchMissingAsync(CancellationToken ct)
    {
        // Kick all three off before awaiting, so the slowest one sets the pace.
        var episodesTask = _options.Sonarr.IsConfigured ? FetchMissingEpisodesAsync(ct) : null;
        var moviesTask = _options.Radarr.IsConfigured ? FetchMissingMoviesAsync(ct) : null;
        var gapsTask = _options.Radarr.IsConfigured ? FetchCollectionGapsAsync(ct) : null;

        var (episodes, sonarrError) = await TryFetchAsync(episodesTask, "Sonarr missing", ct);
        var (movies, radarrError) = await TryFetchAsync(moviesTask, "Radarr missing", ct);
        var (gaps, collectionsError) = await TryFetchAsync(gapsTask, "Radarr collections", ct);

        return new MissingSnapshot
        {
            Movies = movies?.Movies ?? [],
            TotalMissingMovies = movies?.Total ?? 0,
            Seasons = episodes?.Seasons ?? [],
            TotalMissingEpisodes = episodes?.Total ?? 0,
            Collections = gaps ?? [],
            SonarrError = sonarrError,
            RadarrError = radarrError,
            CollectionsError = collectionsError,
        };
    }

    private sealed record MissingEpisodesResult(List<MissingSeason> Seasons, int Total);

    private sealed record MissingMoviesResult(List<MissingMovie> Movies, int Total);

    /// <summary>
    /// Sonarr's wanted list is per episode, but a run of missing episodes is one
    /// decision ("go get season 2"), so they are folded into season rows here.
    /// </summary>
    private async Task<MissingEpisodesResult> FetchMissingEpisodesAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"{_options.Sonarr.Url.TrimEnd('/')}/api/v3/wanted/missing?page=1&pageSize={MissingPageSize}" +
            "&sortKey=airDateUtc&sortDirection=descending&includeSeries=true&monitored=true",
            ct, apiKey: _options.Sonarr.ApiKey);

        var rows = new List<(long SeriesId, int Season, string Series, int Episode, DateTimeOffset? Aired)>();
        foreach (var e in doc.RootElement.GetProperty("records").EnumerateArray())
        {
            rows.Add((
                (long)(Num(e, "seriesId") ?? 0),
                (int)(Num(e, "seasonNumber") ?? 0),
                e.TryGetProperty("series", out var s) ? Str(s, "title") : "",
                (int)(Num(e, "episodeNumber") ?? 0),
                Date(e, "airDateUtc")));
        }

        var seasons = rows
            .GroupBy(r => (r.SeriesId, r.Season))
            .Select(g => new MissingSeason
            {
                SeriesId = g.Key.SeriesId,
                SeasonNumber = g.Key.Season,
                Series = g.Select(r => r.Series).FirstOrDefault(t => t.Length > 0) ?? "",
                EpisodeCount = g.Count(),
                Episodes = FormatEpisodes(g.Select(r => r.Episode)),
                NewestAirDate = g.Max(r => r.Aired),
            })
            .OrderByDescending(s => s.NewestAirDate ?? DateTimeOffset.MinValue)
            .ThenBy(s => s.Series)
            .ToList();

        return new MissingEpisodesResult(seasons, (int)(Num(doc.RootElement, "totalRecords") ?? seasons.Sum(s => s.EpisodeCount)));
    }

    private static string FormatEpisodes(IEnumerable<int> episodes)
    {
        var sorted = episodes.Distinct().OrderBy(e => e).ToList();
        var shown = string.Join(", ", sorted.Take(6).Select(e => $"E{e:00}"));
        return sorted.Count > 6 ? $"{shown} +{sorted.Count - 6} more" : shown;
    }

    private async Task<MissingMoviesResult> FetchMissingMoviesAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"{_options.Radarr.Url.TrimEnd('/')}/api/v3/wanted/missing?page=1&pageSize={MissingPageSize}" +
            "&sortKey=digitalRelease&sortDirection=descending&monitored=true",
            ct, apiKey: _options.Radarr.ApiKey);

        var movies = new List<MissingMovie>();
        foreach (var m in doc.RootElement.GetProperty("records").EnumerateArray())
        {
            var released = new[] { Date(m, "inCinemas"), Date(m, "digitalRelease"), Date(m, "physicalRelease") }
                .Where(d => d is not null)
                .Min();
            movies.Add(new MissingMovie
            {
                MovieId = (long)(Num(m, "id") ?? 0),
                Title = Str(m, "title"),
                Year = Num(m, "year") is { } y and > 0 ? (int)y : null,
                ReleasedAt = released,
                IsAvailable = m.TryGetProperty("isAvailable", out var a) && a.ValueKind == JsonValueKind.True,
            });
        }

        // Grabbable titles first; ones still awaiting a release are noise until they land.
        var ordered = movies
            .OrderByDescending(m => m.IsAvailable)
            .ThenByDescending(m => m.ReleasedAt ?? DateTimeOffset.MinValue)
            .ToList();
        return new MissingMoviesResult(ordered, (int)(Num(doc.RootElement, "totalRecords") ?? ordered.Count));
    }

    /// <summary>
    /// Collections Radarr knows about where some films are tracked and some are
    /// not — the "you have 3 of the 5" case. Collections with nothing in them are
    /// franchises Radarr merely learned of and are left out.
    /// </summary>
    private async Task<List<CollectionGap>> FetchCollectionGapsAsync(CancellationToken ct)
    {
        var baseUrl = _options.Radarr.Url.TrimEnd('/');
        using var doc = await GetJsonAsync($"{baseUrl}/api/v3/collection", ct, apiKey: _options.Radarr.ApiKey);
        var collections = doc.RootElement.EnumerateArray().ToList();

        // Radarr marks each film in a collection with isExisting when the library
        // already tracks it. Older builds omit the flag, so fall back to matching
        // against the library's TMDB ids — one extra call, and only when needed.
        var hasExistingFlag = false;
        foreach (var c in collections)
        {
            if (!c.TryGetProperty("movies", out var films))
                continue;
            foreach (var f in films.EnumerateArray())
            {
                hasExistingFlag = f.TryGetProperty("isExisting", out _);
                break;
            }
            break;
        }

        HashSet<long> owned = [];
        if (!hasExistingFlag)
        {
            using var library = await GetJsonAsync($"{baseUrl}/api/v3/movie", ct, apiKey: _options.Radarr.ApiKey);
            owned = library.RootElement.EnumerateArray()
                .Select(m => (long)(Num(m, "tmdbId") ?? 0))
                .Where(id => id > 0)
                .ToHashSet();
        }

        var gaps = new List<CollectionGap>();
        foreach (var c in collections)
        {
            if (!c.TryGetProperty("movies", out var films))
                continue;
            var missing = new List<CollectionMovie>();
            var have = 0;
            foreach (var f in films.EnumerateArray())
            {
                var tmdbId = (long)(Num(f, "tmdbId") ?? 0);
                var inLibrary = hasExistingFlag
                    ? f.TryGetProperty("isExisting", out var existing) && existing.ValueKind == JsonValueKind.True
                    : owned.Contains(tmdbId);
                // Films on the import-exclusion list were turned down on purpose.
                var excluded = f.TryGetProperty("isExcluded", out var ex) && ex.ValueKind == JsonValueKind.True;
                if (inLibrary)
                {
                    have++;
                }
                else if (tmdbId > 0 && !excluded)
                {
                    missing.Add(new CollectionMovie
                    {
                        TmdbId = tmdbId,
                        Title = Str(f, "title"),
                        Year = Num(f, "year") is { } y and > 0 ? (int)y : null,
                    });
                }
            }
            if (have > 0 && missing.Count > 0)
            {
                gaps.Add(new CollectionGap
                {
                    Title = Str(c, "title"),
                    OwnedCount = have,
                    Missing = missing.OrderBy(m => m.Year ?? int.MaxValue).ThenBy(m => m.Title).ToList(),
                });
            }
        }
        return gaps.OrderByDescending(g => g.OwnedCount).ThenBy(g => g.Title).ToList();
    }

    /// <summary>
    /// Asks an Arr to go looking for something it is already monitoring: a whole
    /// Sonarr season when <paramref name="seasonNumber"/> is given, otherwise the
    /// whole series, or a single Radarr movie.
    /// </summary>
    public async Task SearchForMissingAsync(string source, long id, int? seasonNumber = null, CancellationToken ct = default)
    {
        object command;
        if (source == "Radarr")
            command = new { name = "MoviesSearch", movieIds = new[] { id } };
        else if (seasonNumber is { } season)
            command = new { name = "SeasonSearch", seriesId = id, seasonNumber = season };
        else
            command = new { name = "SeriesSearch", seriesId = id };

        var (baseUrl, apiKey) = ArrEndpoint(source);
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/command")
        {
            Content = JsonContent.Create(command),
        };
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ArrErrorAsync(response, ct));

        _queue.At = DateTimeOffset.MinValue; // whatever it grabs should show up promptly
    }

    /// <summary>
    /// Adds a collection film Radarr knows of but has never tracked. The gap list
    /// only carries a TMDB id, so the full record is looked up first and then
    /// added down the same path as a search hit.
    /// </summary>
    public async Task AddMovieByTmdbAsync(long tmdbId, string rootFolderPath, int qualityProfileId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{_options.Radarr.Url.TrimEnd('/')}/api/v3/movie/lookup/tmdb?tmdbId={tmdbId}",
            ct, apiKey: _options.Radarr.ApiKey);
        // Most builds answer with the movie object; some wrap it in a one-item array.
        var record = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().FirstOrDefault()
            : doc.RootElement;
        if (record.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Radarr could not look up tmdb:{tmdbId}.");

        await AddToArrAsync(
            new SearchResult { Source = "Radarr", Type = "movie", ExternalId = tmdbId, Payload = record.GetRawText() },
            rootFolderPath, qualityProfileId, ct);
        _missing.At = DateTimeOffset.MinValue; // the collection gap just closed
    }

    // Each missing lookup is independent — one Arr being down must not blank the others.
    private async Task<(T? Value, string? Error)> TryFetchAsync<T>(Task<T>? task, string what, CancellationToken ct)
        where T : class
    {
        if (task is null)
            return (null, null);
        try
        {
            return (await task, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{What} fetch failed", what);
            return (null, Describe(ex));
        }
    }

    // ── Plex ─────────────────────────────────────────────────────────────

    private async Task<RecentlyAddedSnapshot> FetchRecentlyAddedAsync(CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.Plex.Url.TrimEnd('/')}/library/recentlyAdded");
            request.Headers.Add("X-Plex-Token", _options.Plex.ApiKey);
            request.Headers.Add("Accept", "application/json"); // Plex defaults to XML
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            var items = new List<RecentItem>();
            if (doc.RootElement.TryGetProperty("MediaContainer", out var container)
                && container.TryGetProperty("Metadata", out var metadata))
            {
                foreach (var m in metadata.EnumerateArray().Take(12))
                {
                    var type = Str(m, "type");
                    items.Add(new RecentItem
                    {
                        Title = type switch
                        {
                            "episode" => Str(m, "grandparentTitle"),
                            "season" => Str(m, "parentTitle"),
                            _ => $"{Str(m, "title")}{(Num(m, "year") is { } y ? $" ({y:0})" : "")}",
                        },
                        Detail = type switch
                        {
                            "episode" => $"S{Num(m, "parentIndex"):00}E{Num(m, "index"):00} · {Str(m, "title")}",
                            "season" => Str(m, "title"),
                            "movie" => "Movie",
                            _ => type,
                        },
                        AddedAt = Num(m, "addedAt") is { } added
                            ? DateTimeOffset.FromUnixTimeSeconds((long)added).ToLocalTime()
                            : DateTimeOffset.Now,
                    });
                }
            }
            return new RecentlyAddedSnapshot { Items = items };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Plex fetch failed");
            return new RecentlyAddedSnapshot { Error = Describe(ex) };
        }
    }

    // ── Overseerr ────────────────────────────────────────────────────────

    private async Task<RequestsSnapshot> FetchRequestsAsync(CancellationToken ct)
    {
        try
        {
            var baseUrl = _options.Overseerr.Url.TrimEnd('/');
            using var doc = await GetJsonAsync($"{baseUrl}/api/v1/request?take=15&filter=pending&sort=added", ct, apiKey: _options.Overseerr.ApiKey);
            var pending = new List<(long Id, string Type, long TmdbId, string By, DateTimeOffset At)>();
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                var type = Str(r, "type");
                var tmdbId = r.TryGetProperty("media", out var media) ? (long)(Num(media, "tmdbId") ?? 0) : 0;
                var by = r.TryGetProperty("requestedBy", out var user)
                    ? (Str(user, "displayName") is { Length: > 0 } d ? d : Str(user, "plexUsername"))
                    : "";
                pending.Add(((long)(Num(r, "id") ?? 0), type, tmdbId, by, Date(r, "createdAt") ?? DateTimeOffset.Now));
            }
            // Titles need one lookup each; do them concurrently (and cached after first sight).
            var requests = await Task.WhenAll(pending.Select(async p => new MediaRequest
            {
                Id = p.Id,
                Title = await LookupTitleAsync(baseUrl, p.Type, p.TmdbId, ct),
                Type = p.Type,
                RequestedBy = p.By,
                At = p.At,
            }));
            return new RequestsSnapshot { Requests = requests };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Overseerr fetch failed");
            return new RequestsSnapshot { Error = Describe(ex) };
        }
    }

    // Request objects only carry a TMDB id; titles come from the movie/tv detail
    // endpoints and never change, so cache them for the process lifetime.
    private async Task<string> LookupTitleAsync(string baseUrl, string type, long tmdbId, CancellationToken ct)
    {
        if (tmdbId == 0)
            return type;
        var key = $"{type}:{tmdbId}";
        if (_titleCache.TryGetValue(key, out var cached))
            return cached;
        try
        {
            var path = type == "movie" ? $"/api/v1/movie/{tmdbId}" : $"/api/v1/tv/{tmdbId}";
            using var doc = await GetJsonAsync($"{baseUrl}{path}", ct, apiKey: _options.Overseerr.ApiKey);
            var title = type == "movie" ? Str(doc.RootElement, "title") : Str(doc.RootElement, "name");
            if (string.IsNullOrEmpty(title))
                title = $"tmdb:{tmdbId}";
            _titleCache[key] = title;
            return title;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return $"tmdb:{tmdbId}";
        }
    }

    // ── ErsatzTV ─────────────────────────────────────────────────────────

    /// <summary>
    /// The channel lineup, joined to what each channel is airing and whether anyone
    /// has it open. The lineup comes from the REST API; the schedule does not live
    /// there at all, so now/next is read out of the XMLTV guide the same API serves.
    /// </summary>
    private async Task<ErsatzTvSnapshot> FetchErsatzTvAsync(CancellationToken ct)
    {
        var baseUrl = _options.ErsatzTv.Url.TrimEnd('/');

        List<(string Number, string Name, string Mode)> lineup;
        try
        {
            using var doc = await GetJsonAsync($"{baseUrl}/api/channels", ct);
            lineup = doc.RootElement.EnumerateArray()
                .Select(c => (Number: Str(c, "number"), Name: Str(c, "name"), Mode: Str(c, "streamingMode")))
                .Where(c => c.Number.Length > 0)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ErsatzTV channel fetch failed");
            return new ErsatzTvSnapshot { Error = Describe(ex) };
        }

        var numbers = lineup.Select(c => c.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Version and sessions are quick; the guide is a megabyte of XML and cached
        // for minutes. All three are extras — losing one must not blank the lineup.
        var versionTask = GetVersionAsync();
        var sessionsTask = GetSessionsAsync();
        var guideTask = GetCachedAsync(_guide, CalendarTtl, token => FetchGuideAsync(numbers, token), ct);

        var (version, versionError) = await TryFetchAsync(versionTask, "ErsatzTV version", ct);
        var (sessions, sessionsError) = await TryFetchAsync(sessionsTask, "ErsatzTV sessions", ct);
        var guide = await guideTask;

        var now = DateTimeOffset.Now;
        var channels = lineup
            .Select(c =>
            {
                var airings = guide.ByChannel.GetValueOrDefault(c.Number) ?? [];
                var state = sessions is not null && sessions.TryGetValue(c.Number, out var s) ? s : null;
                return new ErsatzTvChannel
                {
                    Number = c.Number,
                    Name = c.Name,
                    StreamingMode = c.Mode,
                    Now = airings.FirstOrDefault(p => p.Start <= now && p.Stop > now),
                    Next = airings.FirstOrDefault(p => p.Start > now),
                    IsStreaming = state is not null,
                    SessionState = state,
                };
            })
            // Dial order, and ErsatzTV allows decimal numbers ("5.1") that sort wrong as text.
            .OrderBy(c => double.TryParse(c.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : double.MaxValue)
            .ThenBy(c => c.Number, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ErsatzTvSnapshot
        {
            Channels = channels,
            Version = version,
            DetailError = guide.Error ?? sessionsError ?? versionError,
        };

        async Task<string> GetVersionAsync()
        {
            using var doc = await GetJsonAsync($"{baseUrl}/api/version", ct);
            return Str(doc.RootElement, "appVersion");
        }

        // Sessions exist per channel only while ErsatzTV is transcoding for a viewer,
        // and only in HLS modes — MPEG-TS channels never appear here.
        async Task<Dictionary<string, string>> GetSessionsAsync()
        {
            using var doc = await GetJsonAsync($"{baseUrl}/api/sessions", ct);
            var open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (Str(s, "channelNumber") is { Length: > 0 } number)
                    open[number] = Str(s, "state");
            }
            return open;
        }
    }

    /// <summary>The parsed guide, kept whole so a failed parse is cached as an error rather than retried every poll.</summary>
    private sealed record Guide(Dictionary<string, List<ErsatzTvProgramme>> ByChannel, string? Error = null);

    /// <summary>
    /// The programme schedule, per channel number, from ErsatzTV's XMLTV guide.
    /// The guide's channel ids ("C5.1.150.ersatztv.org") cannot be split back into a
    /// number once numbers contain dots, so each channel is matched through whichever
    /// of its display-names is a number the API just reported. That mapping is cached
    /// with the guide, so a channel added in the last few minutes shows no now/next
    /// until the next guide fetch.
    /// </summary>
    private async Task<Guide> FetchGuideAsync(IReadOnlySet<string> channelNumbers, CancellationToken ct)
    {
        try
        {
            return new Guide(await ParseGuideAsync(channelNumbers, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ErsatzTV guide fetch failed");
            return new Guide([], Describe(ex));
        }
    }

    private async Task<Dictionary<string, List<ErsatzTvProgramme>>> ParseGuideAsync(
        IReadOnlySet<string> channelNumbers, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync($"{_options.ErsatzTv.Url.TrimEnd('/')}/iptv/xmltv.xml", ct);
        response.EnsureSuccessStatusCode();
        var guide = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var numberById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var channel in guide.Root?.Elements("channel") ?? [])
        {
            if (channel.Attribute("id")?.Value is not { Length: > 0 } id)
                continue;
            var match = channel.Elements("display-name")
                .Select(n => n.Value.Trim())
                .FirstOrDefault(channelNumbers.Contains);
            if (match is not null)
                numberById[id] = match;
        }

        var byChannel = new Dictionary<string, List<ErsatzTvProgramme>>(StringComparer.OrdinalIgnoreCase);
        foreach (var programme in guide.Root?.Elements("programme") ?? [])
        {
            if (programme.Attribute("channel")?.Value is not { } id
                || !numberById.TryGetValue(id, out var number)
                || XmltvTime(programme.Attribute("start")?.Value) is not { } start
                || XmltvTime(programme.Attribute("stop")?.Value) is not { } stop)
                continue;
            byChannel.TryAdd(number, []);
            byChannel[number].Add(new ErsatzTvProgramme
            {
                Title = programme.Element("title")?.Value.Trim() ?? "",
                SubTitle = programme.Element("sub-title")?.Value.Trim() ?? "",
                Episode = programme.Elements("episode-num")
                    .FirstOrDefault(e => e.Attribute("system")?.Value == "onscreen")?.Value.Trim() ?? "",
                Start = start,
                Stop = stop,
            });
        }

        foreach (var airings in byChannel.Values)
            airings.Sort((a, b) => a.Start.CompareTo(b.Start));
        return byChannel;
    }

    // XMLTV stamps times as "20260806055636 -0400" — a plain local timestamp with the
    // offset trailing as ±hhmm, which no standard DateTimeOffset format accepts.
    private static DateTimeOffset? XmltvTime(string? value)
    {
        if (value is not { Length: >= 14 })
            return null;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!DateTime.TryParseExact(parts[0], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var stamp))
            return null;
        if (parts.Length > 1 && parts[1] is { Length: 5 } zone
            && int.TryParse(zone.AsSpan(1, 2), out var hours) && int.TryParse(zone.AsSpan(3, 2), out var minutes))
        {
            var offset = new TimeSpan(hours, minutes, 0);
            return new DateTimeOffset(stamp, zone[0] == '-' ? -offset : offset).ToLocalTime();
        }
        // No offset given: XMLTV says treat it as the server's local time, which is ours too.
        return new DateTimeOffset(stamp, TimeZoneInfo.Local.GetUtcOffset(stamp));
    }

    /// <summary>Stops the transcode session on one ErsatzTV channel — the fix for a wedged stream.</summary>
    public async Task StopErsatzTvSessionAsync(string channelNumber, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var response = await http.DeleteAsync(
            $"{_options.ErsatzTv.Url.TrimEnd('/')}/api/session/{Uri.EscapeDataString(channelNumber)}", ct);
        response.EnsureSuccessStatusCode();
        _ersatzTv.At = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Rebuilds a channel's playout from its schedule — what you reach for when a
    /// channel has run dry or is airing something the schedule no longer says.
    /// </summary>
    public async Task ResetErsatzTvPlayoutAsync(string channelNumber, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var response = await http.PostAsync(
            $"{_options.ErsatzTv.Url.TrimEnd('/')}/api/channels/{Uri.EscapeDataString(channelNumber)}/playout/reset",
            content: null, ct);
        response.EnsureSuccessStatusCode();
        _ersatzTv.At = DateTimeOffset.MinValue;
        _guide.At = DateTimeOffset.MinValue; // the guide reports the playout it just rebuilt
    }

    // ── plumbing ─────────────────────────────────────────────────────────

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, string? apiKey = null)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (apiKey is not null)
            request.Headers.Add("X-Api-Key", apiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private sealed class Cached<T> where T : class
    {
        public T? Value;
        public DateTimeOffset At;
        public readonly SemaphoreSlim Lock = new(1, 1);
    }

    private static async Task<T> GetCachedAsync<T>(Cached<T> cache, TimeSpan ttl, Func<CancellationToken, Task<T>> fetch, CancellationToken ct) where T : class
    {
        if (cache.Value is { } fresh && DateTimeOffset.UtcNow - cache.At < ttl)
            return fresh;
        await cache.Lock.WaitAsync(ct);
        try
        {
            if (cache.Value is { } stillFresh && DateTimeOffset.UtcNow - cache.At < ttl)
                return stillFresh;
            cache.Value = await fetch(ct);
            cache.At = DateTimeOffset.UtcNow;
            return cache.Value;
        }
        finally
        {
            cache.Lock.Release();
        }
    }

    // HttpClient timeouts surface as TaskCanceledException; name the likely cause.
    private static string Describe(Exception ex) =>
        ex is TaskCanceledException
            ? "timed out — is it reachable from the Labby container?"
            : ex.GetBaseException().Message;

    // Tautulli (and friends) return numbers as strings half the time; parse either.
    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement e, string prop) =>
        !e.TryGetProperty(prop, out var v) ? null
        : v.ValueKind == JsonValueKind.Number ? v.GetDouble()
        : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null;

    private static DateTimeOffset? Date(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var parsed)
            ? parsed.ToLocalTime()
            : null;
}
