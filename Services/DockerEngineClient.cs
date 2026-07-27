using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

namespace Labby.Services;

/// <summary>
/// Minimal Docker Engine API client over the unix socket (mounted into the
/// container on the NAS). Used for container logs and exec shells, which
/// Container Station's REST API doesn't expose. Absent socket = feature quietly
/// unavailable. On Windows dev boxes, Docker Desktop's named pipe works too.
/// </summary>
public sealed class DockerEngineClient
{
    private const string SocketPath = "/var/run/docker.sock";
    private const string WindowsPipeName = "docker_engine";

    private static bool UseNamedPipe => !File.Exists(SocketPath) && OperatingSystem.IsWindows()
                                        && File.Exists(@"\\.\pipe\" + WindowsPipeName);

    private static async ValueTask<Stream> ConnectRawAsync(CancellationToken ct)
    {
        if (UseNamedPipe)
        {
            var pipe = new NamedPipeClientStream(".", WindowsPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(ct);
            return pipe;
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
        return new NetworkStream(socket, ownsSocket: true);
    }

    private readonly Lazy<HttpClient?> _http = new(() =>
    {
        if (!File.Exists(SocketPath) && !UseNamedPipe)
            return null;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) => await ConnectRawAsync(ct),
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docker/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    });

    public bool IsAvailable => _http.Value is not null;

    /// <summary>The sha256 repo digest of a local image (what "docker pull" would compare against).</summary>
    public async Task<string?> GetImageDigestAsync(string image, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            return null;
        using var doc = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"images/{Uri.EscapeDataString(image)}/json", ct));
        if (doc.RootElement.TryGetProperty("RepoDigests", out var digests) && digests.GetArrayLength() > 0)
        {
            var full = digests[0].GetString() ?? "";
            var at = full.IndexOf('@');
            return at >= 0 ? full[(at + 1)..] : full;
        }
        return null;
    }

    /// <summary>Creates and starts a short-lived helper container (auto-removed on exit).</summary>
    public Task RunOneShotAsync(string image, string[] cmd, string[] binds, CancellationToken ct = default) =>
        RunHelperAsync(image, cmd, binds, ct: ct);

    /// <summary>
    /// Creates and starts a helper container. Give it a name and <paramref name="autoRemove"/>
    /// false to keep it around after it exits — that's the only way to read what it printed.
    /// </summary>
    public async Task RunHelperAsync(string image, string[] cmd, string[] binds, string? name = null,
        string? workingDir = null, bool autoRemove = true, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            throw new InvalidOperationException("Docker socket not available.");

        var (repo, tag) = SplitImage(image);
        // Make sure the helper image exists locally (no-op if already pulled). Read the
        // headers only: the pull streams progress for as long as it takes, which is more
        // than the client's request timeout allows for.
        using (var request = new HttpRequestMessage(HttpMethod.Post,
                   $"images/create?fromImage={Uri.EscapeDataString(repo)}&tag={Uri.EscapeDataString(tag)}"))
        using (var pull = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            await pull.Content.CopyToAsync(Stream.Null, ct); // drain the progress stream
        }

        var createUrl = name is { Length: > 0 } ? $"containers/create?name={Uri.EscapeDataString(name)}" : "containers/create";
        using var create = await http.PostAsync(createUrl,
            System.Net.Http.Json.JsonContent.Create(new
            {
                Image = $"{repo}:{tag}",
                Cmd = cmd,
                WorkingDir = workingDir ?? "",
                HostConfig = new { Binds = binds, AutoRemove = autoRemove },
            }), ct);
        create.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.GetProperty("Id").GetString()!;

        using var start = await http.PostAsync($"containers/{id}/start", null, ct);
        start.EnsureSuccessStatusCode();
    }

    /// <summary>"docker:cli" → ("docker", "cli"); a bare name gets :latest. Registry ports keep their colon.</summary>
    private static (string Repo, string Tag) SplitImage(string image)
    {
        var colon = image.LastIndexOf(':');
        return colon > 0 && !image[(colon + 1)..].Contains('/') ? (image[..colon], image[(colon + 1)..]) : (image, "latest");
    }

    public sealed record ContainerDetail(string Id, string Name, bool Running, int ExitCode,
        DateTimeOffset? FinishedAt, IReadOnlyDictionary<string, string> Labels);

    /// <summary>Inspects a container by id or name. Null when the socket is absent or nothing matches.</summary>
    public async Task<ContainerDetail?> InspectAsync(string idOrName, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            return null;

        using var response = await http.GetAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var state = root.GetProperty("State");

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("Config", out var config)
            && config.TryGetProperty("Labels", out var raw)
            && raw.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var label in raw.EnumerateObject())
                labels[label.Name] = label.Value.GetString() ?? "";
        }

        // Docker writes year 1 for "never finished"; that's not a timestamp worth showing.
        DateTimeOffset? finishedAt = state.TryGetProperty("FinishedAt", out var finished)
                                     && DateTimeOffset.TryParse(finished.GetString(), out var at) && at.Year > 1
            ? at
            : null;

        return new ContainerDetail(
            root.GetProperty("Id").GetString()!,
            (root.GetProperty("Name").GetString() ?? "").TrimStart('/'),
            state.GetProperty("Running").GetBoolean(),
            state.TryGetProperty("ExitCode", out var exit) ? exit.GetInt32() : 0,
            finishedAt,
            labels);
    }

    /// <summary>
    /// Restarts a container. Docker answers only once it is up again, so restarting
    /// the container this process runs in never sees a response — see SelfMaintenanceService.
    /// </summary>
    public async Task RestartAsync(string idOrName, int stopTimeoutSeconds = 10, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            throw new InvalidOperationException("Docker socket not available (mount /var/run/docker.sock into the labby container).");

        using var response = await http.PostAsync(
            $"containers/{Uri.EscapeDataString(idOrName)}/restart?t={stopTimeoutSeconds}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Force-removes a container. A container that isn't there is already the wanted state.</summary>
    public async Task RemoveAsync(string idOrName, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            return;

        using var response = await http.DeleteAsync($"containers/{Uri.EscapeDataString(idOrName)}?force=1", ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates an interactive TTY exec (bash when the image has it, else sh).</summary>
    public async Task<string> CreateShellExecAsync(string containerId, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            throw new InvalidOperationException("Docker socket not available (mount /var/run/docker.sock into the labby container).");

        using var response = await http.PostAsync($"containers/{Uri.EscapeDataString(containerId)}/exec",
            System.Net.Http.Json.JsonContent.Create(new
            {
                AttachStdin = true,
                AttachStdout = true,
                AttachStderr = true,
                Tty = true,
                Cmd = new[] { "/bin/sh", "-c", "command -v bash >/dev/null 2>&1 && exec bash || exec sh" },
                Env = new[] { "TERM=xterm-256color" },
            }), ct);
        response.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("Id").GetString()!;
    }

    public async Task ResizeExecAsync(string execId, int cols, int rows, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            return;
        using var response = await http.PostAsync($"exec/{Uri.EscapeDataString(execId)}/resize?h={rows}&w={cols}", null, ct);
        // Resize can race the shell starting up; a failed resize is cosmetic.
    }

    /// <summary>
    /// Starts the exec and returns the hijacked duplex stream (stdin/stdout of the
    /// shell). HttpClient can't do connection hijacking, so this speaks just enough
    /// HTTP/1.1 by hand: send the upgrade request, skip the response headers, and
    /// everything after is the raw TTY byte stream.
    /// </summary>
    public async Task<Stream> StartExecStreamAsync(string execId, CancellationToken ct = default)
    {
        var stream = await ConnectRawAsync(ct);
        try
        {
            var body = """{"Detach":false,"Tty":true}""";
            var request = $"POST /exec/{Uri.EscapeDataString(execId)}/start HTTP/1.1\r\n" +
                          "Host: docker\r\n" +
                          "Content-Type: application/json\r\n" +
                          "Connection: Upgrade\r\n" +
                          "Upgrade: tcp\r\n" +
                          $"Content-Length: {body.Length}\r\n\r\n{body}";
            var requestBytes = System.Text.Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, ct);
            await stream.FlushAsync(ct);

            // Read the response headers byte-by-byte so no shell output is swallowed.
            var header = new System.Text.StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, ct) == 0)
                    throw new IOException("Docker closed the exec connection during upgrade.");
                header.Append((char)one[0]);
                if (header.Length > 16 * 1024)
                    throw new IOException("Docker exec upgrade response too large.");
            }
            var status = header.ToString().Split("\r\n")[0];
            // 101 Switching Protocols on current engines; older ones answered 200.
            if (!status.Contains(" 101 ") && !status.Contains(" 200 "))
                throw new IOException($"Docker exec start failed: {status}");
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task<string> GetLogsAsync(string containerId, int tail = 200, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            throw new InvalidOperationException("Docker socket not available (mount /var/run/docker.sock into the labby container).");

        var bytes = await http.GetByteArrayAsync(
            $"containers/{Uri.EscapeDataString(containerId)}/logs?stdout=1&stderr=1&tail={tail}", ct);
        return DemultiplexLogs(bytes);
    }

    /// <summary>
    /// Non-TTY containers return logs as multiplexed frames:
    /// [stream:1][pad:3][length:4 big-endian][payload]. TTY containers return raw text.
    /// </summary>
    private static string DemultiplexLogs(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "";
        // Frame header starts with stream id 0/1/2 followed by three zero bytes.
        if (bytes[0] > 2 || bytes.Length < 8 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0)
            return System.Text.Encoding.UTF8.GetString(bytes);

        var sb = new System.Text.StringBuilder();
        var offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            var length = (bytes[offset + 4] << 24) | (bytes[offset + 5] << 16) | (bytes[offset + 6] << 8) | bytes[offset + 7];
            offset += 8;
            if (length <= 0 || offset + length > bytes.Length)
                break;
            sb.Append(System.Text.Encoding.UTF8.GetString(bytes, offset, length));
            offset += length;
        }
        return sb.ToString();
    }
}
