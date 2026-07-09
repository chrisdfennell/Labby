using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Pushes service down/recovery messages to the configured webhook. Discord and
/// Slack get their JSON payload shape; any other URL (ntfy topics, generic
/// webhooks) gets the message as a plain-text POST body.
/// </summary>
public sealed class AlertNotifier(IHttpClientFactory httpFactory, IOptions<AlertOptions> options, ILogger<AlertNotifier> logger)
{
    public const string HttpClientName = "alerts";

    public bool IsEnabled => options.Value.IsEnabled;

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Alert webhook failed");
        }
    }
}
