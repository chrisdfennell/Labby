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
    private readonly ConcurrentDictionary<string, string> _titleCache = new();
    private string? _qbitSid;

    public bool AnyConfigured => _options.AnyConfigured;
    public bool TautulliConfigured => _options.Tautulli.IsConfigured;
    public bool SonarrConfigured => _options.Sonarr.IsConfigured;
    public bool RadarrConfigured => _options.Radarr.IsConfigured;
    public bool OverseerrConfigured => _options.Overseerr.IsConfigured;
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tautulli fetch failed");
            return new NowPlayingSnapshot { Error = ex.GetBaseException().Message };
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sonarr fetch failed");
            return new UpcomingSnapshot { SonarrError = ex.GetBaseException().Message };
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Radarr fetch failed");
            return new UpcomingSnapshot { RadarrError = ex.GetBaseException().Message };
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "qBittorrent fetch failed");
                qbitError = ex.GetBaseException().Message;
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "NZBGet fetch failed");
                nzbError = ex.GetBaseException().Message;
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
                items.Add(new DownloadItem
                {
                    Name = Str(t, "name"),
                    Source = "qBittorrent",
                    ProgressPercent = Math.Round((Num(t, "progress") ?? 0) * 100, 1),
                    SizeBytes = (long)(Num(t, "size") ?? 0),
                    SpeedBps = (long)(Num(t, "dlspeed") ?? 0),
                    Eta = eta is > 0 and < 8_640_000 ? TimeSpan.FromSeconds(eta) : null,
                    State = Str(t, "state"),
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
            items.Add(new DownloadItem
            {
                Name = Str(g, "NZBName"),
                Source = "NZBGet",
                ProgressPercent = totalMb > 0 ? Math.Round((totalMb - remainingMb) / totalMb * 100, 1) : 0,
                SizeBytes = (long)(totalMb * 1024 * 1024),
                SpeedBps = rate, // NZBGet reports one global rate, not per-item
                Eta = rate > 0 ? TimeSpan.FromSeconds(remainingMb * 1024 * 1024 / rate) : null,
                State = Str(g, "Status").ToLowerInvariant(),
            });
        }
        return (items, rate);
    }

    // ── Overseerr ────────────────────────────────────────────────────────

    private async Task<RequestsSnapshot> FetchRequestsAsync(CancellationToken ct)
    {
        try
        {
            var baseUrl = _options.Overseerr.Url.TrimEnd('/');
            using var doc = await GetJsonAsync($"{baseUrl}/api/v1/request?take=15&filter=pending&sort=added", ct, apiKey: _options.Overseerr.ApiKey);
            var pending = new List<(string Type, long TmdbId, string By, DateTimeOffset At)>();
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                var type = Str(r, "type");
                var tmdbId = r.TryGetProperty("media", out var media) ? (long)(Num(media, "tmdbId") ?? 0) : 0;
                var by = r.TryGetProperty("requestedBy", out var user)
                    ? (Str(user, "displayName") is { Length: > 0 } d ? d : Str(user, "plexUsername"))
                    : "";
                pending.Add((type, tmdbId, by, Date(r, "createdAt") ?? DateTimeOffset.Now));
            }
            // Titles need one lookup each; do them concurrently (and cached after first sight).
            var requests = await Task.WhenAll(pending.Select(async p => new MediaRequest
            {
                Title = await LookupTitleAsync(baseUrl, p.Type, p.TmdbId, ct),
                Type = p.Type,
                RequestedBy = p.By,
                At = p.At,
            }));
            return new RequestsSnapshot { Requests = requests };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Overseerr fetch failed");
            return new RequestsSnapshot { Error = ex.GetBaseException().Message };
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
