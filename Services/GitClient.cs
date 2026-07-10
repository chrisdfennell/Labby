using System.Text.Json;
using Labby.Models;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Reads repositories, open issues, and open pull requests from a MyPersonalGit
/// instance (github.com/chrisdfennell/MyPersonalGit) over its Gitea-style REST API.
/// Auth is a personal access token sent as <c>Authorization: Bearer mypg_…</c>.
/// Results are cached briefly so the page and dashboard card share one fetch.
/// </summary>
public sealed class GitClient(IHttpClientFactory httpFactory, IOptions<GitOptions> options, ILogger<GitClient> logger)
{
    public const string HttpClientName = "git";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly GitOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private GitOverview? _cache;
    private DateTimeOffset _cachedAt;

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>Browser-facing base URL (for links into MyPersonalGit), without a trailing slash.</summary>
    public string BaseUrl => _options.Url.TrimEnd('/');

    public async Task<GitOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        if (_cache is { } fresh && DateTimeOffset.UtcNow - _cachedAt < Ttl)
            return fresh;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } stillFresh && DateTimeOffset.UtcNow - _cachedAt < Ttl)
                return stillFresh;
            _cache = await FetchAsync(ct);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<GitOverview> FetchAsync(CancellationToken ct)
    {
        if (!_options.IsConfigured)
            return new GitOverview { Error = "Not configured — set Git:Url and Git:Token." };

        try
        {
            var repos = await GetReposAsync(ct);

            // Open issues/PRs need one call each per repo; do them concurrently and
            // tolerate individual failures (a repo with issues disabled just shows 0).
            var issues = new List<GitIssue>();
            var pulls = new List<GitPull>();
            foreach (var batch in repos.Chunk(6))
            {
                var results = await Task.WhenAll(batch.Select(async repo =>
                {
                    var (repoIssues, repoPulls) = await GetIssuesAndPullsAsync(repo, ct);
                    repo.OpenIssues = repoIssues.Count;
                    repo.OpenPulls = repoPulls.Count;
                    return (repoIssues, repoPulls);
                }));
                foreach (var (repoIssues, repoPulls) in results)
                {
                    issues.AddRange(repoIssues);
                    pulls.AddRange(repoPulls);
                }
            }

            return new GitOverview
            {
                Repos = repos.OrderByDescending(r => r.UpdatedAt).ToList(),
                OpenIssues = issues.OrderByDescending(i => i.CreatedAt).ToList(),
                OpenPulls = pulls.OrderByDescending(p => p.CreatedAt).ToList(),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MyPersonalGit fetch failed");
            return new GitOverview { Error = Describe(ex) };
        }
    }

    private async Task<List<GitRepo>> GetReposAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/api/v1/repos", ct);
        var repos = new List<GitRepo>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            repos.Add(new GitRepo
            {
                RawName = Str(r, "name"),
                Description = Str(r, "description"),
                Owner = Str(r, "owner"),
                IsPrivate = Bool(r, "isPrivate"),
                Stars = (int)(Num(r, "stars") ?? 0),
                Forks = (int)(Num(r, "forks") ?? 0),
                Commits = (int)(Num(r, "commits") ?? 0),
                DefaultBranch = Str(r, "default_branch"),
                UpdatedAt = Date(r, "updated_at") ?? Date(r, "created_at") ?? DateTimeOffset.MinValue,
            });
        }
        return repos;
    }

    private async Task<(List<GitIssue> Issues, List<GitPull> Pulls)> GetIssuesAndPullsAsync(GitRepo repo, CancellationToken ct)
    {
        var name = Uri.EscapeDataString(repo.RawName);
        var issues = new List<GitIssue>();
        var pulls = new List<GitPull>();

        try
        {
            using var doc = await GetJsonAsync($"/api/v1/repos/{name}/issues?state=open", ct);
            foreach (var i in doc.RootElement.EnumerateArray())
            {
                issues.Add(new GitIssue
                {
                    Number = (int)(Num(i, "number") ?? 0),
                    Title = Str(i, "title"),
                    Author = Str(i, "author"),
                    CreatedAt = Date(i, "created_at") ?? DateTimeOffset.MinValue,
                    CommentCount = (int)(Num(i, "comment_count") ?? 0),
                    Repo = repo.Name,
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Issues fetch failed for {Repo}", repo.RawName);
        }

        try
        {
            using var doc = await GetJsonAsync($"/api/v1/repos/{name}/pulls?state=open", ct);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                pulls.Add(new GitPull
                {
                    Number = (int)(Num(p, "number") ?? 0),
                    Title = Str(p, "title"),
                    Author = Str(p, "author"),
                    IsDraft = Bool(p, "isDraft"),
                    SourceBranch = Str(p, "source_branch"),
                    TargetBranch = Str(p, "target_branch"),
                    CreatedAt = Date(p, "created_at") ?? DateTimeOffset.MinValue,
                    Repo = repo.Name,
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Pulls fetch failed for {Repo}", repo.RawName);
        }

        return (issues, pulls);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Version = System.Net.HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        // Ask the server to close after the body so the read reaches EOF promptly.
        request.Headers.ConnectionClose = true;
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.Token);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // This build of MyPersonalGit sends a chunked body but omits the terminating
        // zero-length chunk, so .NET throws "response ended prematurely" at EOF even
        // though the whole payload arrived. Buffer what we get and tolerate that.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        try
        {
            await stream.CopyToAsync(buffer, ct);
        }
        catch (HttpIOException) when (buffer.Length > 0)
        {
            // Missing chunk terminator — the JSON before it is complete.
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
    }

    private static string Describe(Exception ex) =>
        ex is TaskCanceledException
            ? "timed out — is MyPersonalGit reachable from the Labby container?"
            : ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                ? "unauthorized — check the Git:Token (it must be a valid mypg_ token)."
                : ex.GetBaseException().Message;

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var parsed)
            ? parsed.ToLocalTime()
            : null;
}
