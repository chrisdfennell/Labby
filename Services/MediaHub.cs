using System.Collections.Concurrent;
using System.Text.Json;
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

    private readonly MediaOptions _options = options.Value;
    private readonly Cached<NowPlayingSnapshot> _nowPlaying = new();
    private readonly Cached<DownloadsSnapshot> _downloads = new();
    private readonly Cached<UpcomingSnapshot> _tv = new();
    private readonly Cached<UpcomingSnapshot> _movies = new();
    private readonly Cached<RequestsSnapshot> _requests = new();
    private readonly Cached<RecentlyAddedSnapshot> _recent = new();
    private readonly Cached<QueueSnapshot> _queue = new();
    private readonly Cached<WatchStatsSnapshot> _watchStats = new();
    private readonly ConcurrentDictionary<string, string> _titleCache = new();
    private string? _qbitSid;

    public bool AnyConfigured => _options.AnyConfigured;
    public bool TautulliConfigured => _options.Tautulli.IsConfigured;
    public bool SonarrConfigured => _options.Sonarr.IsConfigured;
    public bool RadarrConfigured => _options.Radarr.IsConfigured;
    public bool OverseerrConfigured => _options.Overseerr.IsConfigured;
    public bool PlexConfigured => _options.Plex.IsConfigured;
    public bool ProwlarrConfigured => _options.Prowlarr.IsConfigured;
    public bool DownloadsConfigured => _options.Qbittorrent.IsConfigured || _options.Nzbget.IsConfigured;

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

    /// <summary>Searches Overseerr/Seerr for movies and shows.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchMediaAsync(string query, CancellationToken ct = default)
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
                TmdbId = (long)(Num(r, "id") ?? 0),
                Type = type,
                Title = type == "movie" ? Str(r, "title") : Str(r, "name"),
                Year = date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : null,
                Status = status switch { 2 => "pending", 3 => "requested", 4 => "partial", 5 => "available", _ => null },
            });
        }
        return results.Take(12).ToList();
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
        return new QueueItem
        {
            Title = knownTitle is { Length: > 0 } ? knownTitle : Str(record, "title"),
            Source = source,
            Status = Str(record, "status"),
            TimeLeft = Str(record, "timeleft") is { Length: > 0 } t ? t : null,
            ProgressPercent = size > 0 ? Math.Round((size - left) / size * 100, 1) : 0,
            SizeBytes = (long)size,
        };
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
