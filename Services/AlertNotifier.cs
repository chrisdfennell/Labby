using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Fans alert messages out to every configured channel: a webhook (Discord and
/// Slack get their JSON payload shape, anything else — ntfy topics, generic
/// hooks — gets a plain-text POST) and/or Pushover push notifications.
/// Channels are independent; one failing never blocks the other.
/// </summary>
public sealed class AlertNotifier(IHttpClientFactory httpFactory, IOptions<AlertOptions> options, ILogger<AlertNotifier> logger)
{
    public const string HttpClientName = "alerts";
    private const string PushoverEndpoint = "https://api.pushover.net/1/messages.json";

    public bool IsEnabled => options.Value.IsEnabled;

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        if (!string.IsNullOrWhiteSpace(options.Value.WebhookUrl))
            await SendWebhookAsync(message, ct);
        if (options.Value.PushoverEnabled)
            await SendPushoverAsync(message, ct);
    }

    private async Task SendWebhookAsync(string message, CancellationToken ct)
    {
        var url = options.Value.WebhookUrl;
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            var host = new Uri(url).Host;
            using HttpContent content =
                host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase)
                    ? JsonContent.Create(new { content = message })
                : host.EndsWith("slack.com", StringComparison.OrdinalIgnoreCase)
                    ? JsonContent.Create(new { text = message })
                : new StringContent(message);
            using var response = await http.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Alert webhook returned HTTP {Status}", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) // includes HttpClient timeouts (TaskCanceledException)
        {
            logger.LogWarning(ex, "Alert webhook failed");
        }
    }

    private async Task SendPushoverAsync(string message, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = options.Value.PushoverToken,
                ["user"] = options.Value.PushoverUser,
                ["title"] = "Labby",
                ["message"] = message,
            });
            using var response = await http.PostAsync(PushoverEndpoint, content, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Pushover returned HTTP {Status}: {Body}",
                    (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) // includes HttpClient timeouts (TaskCanceledException)
        {
            logger.LogWarning(ex, "Pushover alert failed");
        }
    }
}
