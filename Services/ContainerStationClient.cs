using System.Text.Json;
using Labby.Models;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// QNAP Container Station v1 REST API (QTS with Container Station 2.x; CS 3
/// still ships the v1 surface for compatibility on most firmware). Uses its own
/// cookie-based login, separate from the QTS sid session.
/// </summary>
public sealed class ContainerStationClient(IHttpClientFactory httpFactory, IOptions<QnapOptions> options, QnapClient qnap, ILogger<ContainerStationClient> logger)
{
    public const string HttpClientName = "qnap-container-station";

    private readonly QnapOptions _options = options.Value;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _loggedIn;

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// Lists containers via the v3 API (the QTS sid doubles as its bearer token),
    /// which also reports live CPU/memory usage per container.
    /// </summary>
    public async Task<IReadOnlyList<ContainerInfo>> GetContainersAsync(CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient(HttpClientName);

        async Task<HttpResponseMessage> ListAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "container-station/api/v3/containers");
            request.Headers.Add("Authorization", $"Bearer {await qnap.GetSidAsync(ct)}");
            return await http.SendAsync(request, ct);
        }

        var response = await ListAsync();
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            qnap.InvalidateSession();
            response = await ListAsync();
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var containers = new List<ContainerInfo>();
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    containers.Add(new ContainerInfo
                    {
                        Id = GetString(item, "id"),
                        Name = GetString(item, "name"),
                        Image = GetString(item, "image"),
                        State = GetString(item, "status"),
                        Type = GetString(item, "type") is { Length: > 0 } t ? t : "docker",
                        // v3 reports both as fractions of the whole NAS.
                        CpuPercent = GetNumber(item, "cpu") is { } cpu ? Math.Round(cpu * 100, 1) : null,
                        MemoryPercent = GetNumber(item, "memory") is { } mem ? Math.Round(mem * 100, 1) : null,
                    });
                }
            }
            return containers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public Task StartAsync(ContainerInfo container, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"container-station/api/v1/container/{container.Type}/{Uri.EscapeDataString(container.Id)}/start", ct);

    public Task StopAsync(ContainerInfo container, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"container-station/api/v1/container/{container.Type}/{Uri.EscapeDataString(container.Id)}/stop", ct);

    public Task RestartAsync(ContainerInfo container, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"container-station/api/v1/container/{container.Type}/{Uri.EscapeDataString(container.Id)}/restart", ct);

    private async Task<string> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("QNAP is not configured. Set Qnap:Host, Qnap:Username and Qnap:Password.");

        await EnsureLoggedInAsync(force: false, ct);
        var response = await SendOnceAsync(method, path, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            // The pooled handler (and its cookie jar) rotates periodically; just log in again.
            await EnsureLoggedInAsync(force: true, ct);
            response = await SendOnceAsync(method, path, ct);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Container Station returned {(int)response.StatusCode} for {path}. " +
                "If your NAS runs Container Station 3 with the v1 API removed, this integration needs updating.");
        return body;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, path);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task EnsureLoggedInAsync(bool force, CancellationToken ct)
    {
        if (_loggedIn && !force)
            return;

        await _loginLock.WaitAsync(ct);
        try
        {
            if (_loggedIn && !force)
                return;

            var http = httpFactory.CreateClient(HttpClientName);
            // Container Station 3 parses the login body as JSON (form-encoding gets a 400).
            using var content = JsonContent.Create(new { username = _options.Username, password = _options.Password });
            var response = await http.PostAsync("container-station/api/v1/login", content, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Container Station login failed with {(int)response.StatusCode}.");

            logger.LogInformation("Logged in to Container Station at {Host}", _options.Host);
            _loggedIn = true;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double? GetNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
