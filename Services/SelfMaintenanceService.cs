using System.Text.RegularExpressions;

namespace Labby.Services;

/// <summary>
/// Restart and rebuild of Labby's own container, driven from Settings.
///
/// Restart goes straight at the Docker socket. Rebuild can't: a container cannot
/// build the image it is running, so it runs in a detached docker:cli helper that
/// drives "docker compose" against the project Labby was started from. Which
/// project that is comes from the compose labels Docker puts on our own container,
/// so nothing here has to be told where the source lives.
///
/// Both need the /var/run/docker.sock mount; without it the buttons stay disabled.
/// </summary>
public sealed class SelfMaintenanceService(DockerEngineClient docker, ILogger<SelfMaintenanceService> logger)
{
    /// <summary>Ships the compose plugin, so one small helper covers both build and recreate.</summary>
    private const string HelperImage = "docker:cli";

    /// <summary>Named, and not auto-removed, so the build output survives for the log view.</summary>
    public const string RebuildContainerName = "labby-rebuild";

    /// <summary>Every Labby compose file sets container_name: labby (same name Watchtower is given).</summary>
    private const string FallbackContainerName = "labby";

    private const string NoContainer = "Couldn't find Labby's own container over the Docker socket.";

    private SelfInfo? _self;

    public bool IsAvailable => docker.IsAvailable;

    public sealed record ComposeProject(string Project, string Service, string WorkingDir, string[] ConfigFiles);

    public sealed record SelfInfo(string Id, string Name, ComposeProject? Compose)
    {
        /// <summary>Rebuilding means re-running compose, which needs the project it came from.</summary>
        public bool CanRebuild => Compose is not null;
    }

    public sealed record RebuildStatus(bool Running, int ExitCode, DateTimeOffset? FinishedAt);

    /// <summary>The container this process runs in, plus its compose project. Null when neither can be found.</summary>
    public async Task<SelfInfo?> DescribeSelfAsync(CancellationToken ct = default)
    {
        if (_self is not null)
            return _self;

        foreach (var candidate in Candidates())
        {
            DockerEngineClient.ContainerDetail? detail;
            try
            {
                detail = await docker.InspectAsync(candidate, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Inspect of {Candidate} failed while identifying own container", candidate);
                continue;
            }
            if (detail is null)
                continue;

            return _self = new SelfInfo(detail.Id, detail.Name, ReadCompose(detail.Labels));
        }
        return null;
    }

    /// <summary>Restarts Labby's container. The page reconnects on its own once it's back.</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        var self = await DescribeSelfAsync(ct) ?? throw new InvalidOperationException(NoContainer);
        logger.LogInformation("Restart requested for container {Name}", self.Name);
        try
        {
            await docker.RestartAsync(self.Id, stopTimeoutSeconds: 10, ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            // Docker replies only once the container is up again — and this process is
            // what it just stopped, so losing the connection here IS the success case.
            logger.LogInformation("Restart connection dropped as expected: {Message}", ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Builds the image from the compose project's source and recreates Labby with it.
    /// Returns as soon as the helper is running; the build itself takes minutes and
    /// outlives this container.
    /// </summary>
    public Task RebuildAsync(CancellationToken ct = default) => RunComposeAsync(build: true, ct);

    /// <summary>
    /// Recreates Labby from the compose project without building — the way to pick
    /// up an edited .env. Compose re-interpolates the file it is given, which the
    /// image-only update path can't do: Watchtower copies the running container's
    /// environment onto the new one, so config changes never reach it.
    /// </summary>
    public Task RecreateFromComposeAsync(CancellationToken ct = default) => RunComposeAsync(build: false, ct);

    private async Task RunComposeAsync(bool build, CancellationToken ct)
    {
        var self = await DescribeSelfAsync(ct) ?? throw new InvalidOperationException(NoContainer);
        if (self.Compose is not { } compose)
            throw new InvalidOperationException(
                "This container wasn't started by docker compose, so there's no project to rebuild from.");

        var files = string.Join(' ', compose.ConfigFiles.Select(file => $"-f {Quote(file)}"));
        var args = $"--project-directory {Quote(compose.WorkingDir)} -p {Quote(compose.Project)} {files}";
        var service = Quote(compose.Service);
        // Build first and recreate only if it succeeds, so a broken build leaves the
        // running Labby alone. set -x puts the commands themselves in the log view.
        // A plain recreate skips the build and just re-reads the compose file and .env.
        var script = build
            ? $"set -x; docker compose {args} build --progress plain {service} && " +
              $"docker compose {args} up -d {service}"
            : $"set -x; docker compose {args} up -d {service}";

        var binds = new List<string>
        {
            "/var/run/docker.sock:/var/run/docker.sock",
            // The build context and the .env compose interpolates from, at the same path
            // inside the helper — these are host paths either way.
            $"{compose.WorkingDir}:{compose.WorkingDir}",
        };
        foreach (var dir in compose.ConfigFiles.Select(ParentDir).Distinct(StringComparer.Ordinal))
        {
            if (dir.Length > 0 && !dir.StartsWith(compose.WorkingDir, StringComparison.Ordinal))
                binds.Add($"{dir}:{dir}"); // a compose file kept outside the project directory
        }

        logger.LogInformation("{Action} requested for compose service {Service} of project {Project} in {Dir}",
            build ? "Rebuild" : "Recreate", compose.Service, compose.Project, compose.WorkingDir);

        await docker.RemoveAsync(RebuildContainerName, ct); // keep only the newest build's log
        await docker.RunHelperAsync(
            HelperImage,
            cmd: ["sh", "-c", script],
            binds: [.. binds],
            name: RebuildContainerName,
            workingDir: compose.WorkingDir,
            autoRemove: false,
            ct: ct);
    }

    /// <summary>State of the last rebuild, or null when none has run since the helper was last cleaned up.</summary>
    public async Task<RebuildStatus?> GetRebuildStatusAsync(CancellationToken ct = default)
    {
        var detail = await docker.InspectAsync(RebuildContainerName, ct);
        return detail is null ? null : new RebuildStatus(detail.Running, detail.ExitCode, detail.FinishedAt);
    }

    /// <summary>What the rebuild printed — the place to look when a build fails.</summary>
    public Task<string> GetRebuildLogAsync(CancellationToken ct = default) =>
        docker.GetLogsAsync(RebuildContainerName, tail: 400, ct);

    private static ComposeProject? ReadCompose(IReadOnlyDictionary<string, string> labels)
    {
        labels.TryGetValue("com.docker.compose.project", out var project);
        labels.TryGetValue("com.docker.compose.service", out var service);
        labels.TryGetValue("com.docker.compose.project.working_dir", out var workingDir);
        labels.TryGetValue("com.docker.compose.project.config_files", out var configFiles);

        var files = (configFiles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (project is not { Length: > 0 } || service is not { Length: > 0 } || workingDir is not { Length: > 0 } || files.Length == 0)
            return null;
        return new ComposeProject(project, service, workingDir, files);
    }

    /// <summary>
    /// Who am I? /proc is definitive when it's readable — Docker's own bind mounts carry
    /// the container id. The compose container_name comes next, and the hostname last:
    /// with network_mode host (how Labby runs on the NAS) the hostname is the *host's*,
    /// not the container id, so it identifies nothing on its own.
    /// </summary>
    private static IEnumerable<string> Candidates()
    {
        foreach (var id in ProcContainerIds())
            yield return id;
        yield return FallbackContainerName;

        var hostname = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(hostname) && hostname != FallbackContainerName)
            yield return hostname;
    }

    private static IEnumerable<string> ProcContainerIds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // mountinfo: /var/lib/docker/containers/<id>/hostname and friends.
        // cgroup: /docker/<id> on v1, docker-<id>.scope under systemd on v2.
        foreach (var (path, pattern) in new[]
                 {
                     ("/proc/self/mountinfo", "/containers/([0-9a-f]{64})"),
                     ("/proc/self/cgroup", "([0-9a-f]{64})"),
                 })
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception)
            {
                continue; // not Linux, or not in a container
            }
            foreach (Match match in Regex.Matches(text, pattern))
            {
                if (seen.Add(match.Groups[1].Value))
                    yield return match.Groups[1].Value;
            }
        }
    }

    /// <summary>Parent of a Linux path — Path.GetDirectoryName would answer in Windows separators on a dev box.</summary>
    private static string ParentDir(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut > 0 ? path[..cut] : "";
    }

    /// <summary>Single-quote for sh: these paths come out of Docker labels, not from us.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
