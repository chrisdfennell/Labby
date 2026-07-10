using System.Net.Sockets;

namespace Labby.Services;

/// <summary>
/// Minimal Docker Engine API client over the unix socket (mounted into the
/// container on the NAS). Used for container logs, which Container Station's
/// REST API doesn't expose. Absent socket = feature quietly unavailable.
/// </summary>
public sealed class DockerEngineClient
{
    private const string SocketPath = "/var/run/docker.sock";

    private readonly Lazy<HttpClient?> _http = new(() =>
    {
        if (!File.Exists(SocketPath))
            return null;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
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
    public async Task RunOneShotAsync(string image, string[] cmd, string[] binds, CancellationToken ct = default)
    {
        if (_http.Value is not { } http)
            throw new InvalidOperationException("Docker socket not available.");

        // Make sure the helper image exists locally (no-op if already pulled).
        using (var pull = await http.PostAsync($"images/create?fromImage={Uri.EscapeDataString(image)}&tag=latest", null, ct))
        {
            await pull.Content.ReadAsStringAsync(ct); // drain the progress stream
        }

        using var create = await http.PostAsync("containers/create",
            System.Net.Http.Json.JsonContent.Create(new
            {
                Image = $"{image}:latest",
                Cmd = cmd,
                HostConfig = new { Binds = binds, AutoRemove = true },
            }), ct);
        create.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.GetProperty("Id").GetString()!;

        using var start = await http.PostAsync($"containers/{id}/start", null, ct);
        start.EnsureSuccessStatusCode();
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
