using System.Text.Json;

namespace Labby.Services;

/// <summary>
/// Self-update: compares the running image's digest against Docker Hub's
/// :latest, and updates by launching a one-shot Watchtower container over the
/// mounted Docker socket — Watchtower pulls the new image and recreates Labby
/// gracefully (a container can't safely replace itself).
/// </summary>
public sealed class UpdateService(DockerEngineClient docker, IHttpClientFactory httpFactory, ILogger<UpdateService> logger)
{
    public const string HttpClientName = "docker-hub";
    private const string Image = "fennch/labby";
    private const string WatchtowerImage = "containrrr/watchtower";

    public string RunningVersion { get; } = Environment.GetEnvironmentVariable("LABBY_VERSION") ?? "dev";
    public bool CanSelfUpdate => docker.IsAvailable && RunningVersion != "dev";

    public sealed record UpdateCheck(bool UpdateAvailable, string? LatestVersion, string? Error);

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var localDigest = await docker.GetImageDigestAsync($"{Image}:latest", ct);
            var http = httpFactory.CreateClient(HttpClientName);
            using var doc = JsonDocument.Parse(
                await http.GetStringAsync($"https://hub.docker.com/v2/repositories/{Image}/tags/latest", ct));
            var hubDigest = doc.RootElement.TryGetProperty("digest", out var d) ? d.GetString() : null;
            // Newest SHA tag tells us which commit :latest points at.
            string? latestVersion = null;
            using (var tags = JsonDocument.Parse(
                await http.GetStringAsync($"https://hub.docker.com/v2/repositories/{Image}/tags?page_size=10", ct)))
            {
                foreach (var tag in tags.RootElement.GetProperty("results").EnumerateArray())
                {
                    var name = tag.GetProperty("name").GetString();
                    if (name is { Length: 40 } && tag.TryGetProperty("digest", out var td) && td.GetString() == hubDigest)
                    {
                        latestVersion = name;
                        break;
                    }
                }
            }

            if (hubDigest is null || localDigest is null)
                return new UpdateCheck(false, latestVersion, "Could not compare image digests.");
            return new UpdateCheck(!string.Equals(hubDigest, localDigest, StringComparison.OrdinalIgnoreCase), latestVersion, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed");
            return new UpdateCheck(false, null, ex.GetBaseException().Message);
        }
    }

    /// <summary>Starts the one-shot Watchtower update. Labby restarts moments later.</summary>
    public async Task TriggerUpdateAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Self-update requested — launching one-shot watchtower");
        await docker.RunOneShotAsync(
            WatchtowerImage,
            cmd: ["--run-once", "--cleanup", "labby"],
            binds: ["/var/run/docker.sock:/var/run/docker.sock"],
            ct);
    }
}
