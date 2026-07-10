using Labby.Components;
using Labby.Options;
using Labby.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<QnapOptions>(builder.Configuration.GetSection(QnapOptions.SectionName));
builder.Services.Configure<AmbientWeatherOptions>(builder.Configuration.GetSection(AmbientWeatherOptions.SectionName));
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection(DashboardOptions.SectionName));
builder.Services.Configure<KontainrOptions>(builder.Configuration.GetSection(KontainrOptions.SectionName));
builder.Services.Configure<TerminalOptions>(builder.Configuration.GetSection(TerminalOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<HistoryOptions>(builder.Configuration.GetSection(HistoryOptions.SectionName));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection(AlertOptions.SectionName));
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.SectionName));
builder.Services.Configure<NetworkOptions>(builder.Configuration.GetSection(NetworkOptions.SectionName));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));

// Login is opt-in: setting Auth:Password turns it on, otherwise Labby stays open (trusted LAN).
var authEnabled = !string.IsNullOrWhiteSpace(builder.Configuration[$"{AuthOptions.SectionName}:Password"]);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "labby.auth";
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

static HttpMessageHandler CreateQnapHandler(IServiceProvider sp, bool useCookies)
{
    var qnap = sp.GetRequiredService<IOptions<QnapOptions>>().Value;
    var handler = new SocketsHttpHandler { UseCookies = useCookies };
    if (qnap.UseHttps && qnap.IgnoreCertificateErrors)
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    return handler;
}

builder.Services.AddHttpClient(QnapClient.HttpClientName, (sp, client) =>
    {
        var qnap = sp.GetRequiredService<IOptions<QnapOptions>>().Value;
        if (qnap.IsConfigured)
            client.BaseAddress = new Uri(qnap.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .ConfigurePrimaryHttpMessageHandler(sp => CreateQnapHandler(sp, useCookies: false));

// Uploads share the QNAP config but need a generous timeout for big files.
builder.Services.AddHttpClient(QnapFileStation.UploadHttpClientName, (sp, client) =>
    {
        var qnap = sp.GetRequiredService<IOptions<QnapOptions>>().Value;
        if (qnap.IsConfigured)
            client.BaseAddress = new Uri(qnap.BaseUrl);
        client.Timeout = TimeSpan.FromMinutes(30);
    })
    .ConfigurePrimaryHttpMessageHandler(sp => CreateQnapHandler(sp, useCookies: false));

builder.Services.AddHttpClient(ContainerStationClient.HttpClientName, (sp, client) =>
    {
        var qnap = sp.GetRequiredService<IOptions<QnapOptions>>().Value;
        if (qnap.IsConfigured)
            client.BaseAddress = new Uri(qnap.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .ConfigurePrimaryHttpMessageHandler(sp => CreateQnapHandler(sp, useCookies: true));

builder.Services.AddHttpClient(AmbientWeatherClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://rt.ambientweather.net/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient(AlertNotifier.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddHttpClient(MediaHub.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddHttpClient(MinerClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));

builder.Services.AddHttpClient(ServiceHealthMonitor.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Home lab services routinely use self-signed certs; a cert problem shouldn't read as "down".
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    });

builder.Services.AddSingleton<QnapClient>();
builder.Services.AddSingleton<QnapFileStation>();
builder.Services.AddSingleton<ContainerStationClient>();
builder.Services.AddSingleton<AmbientWeatherClient>();
builder.Services.AddSingleton<AlertNotifier>();
builder.Services.AddSingleton<MediaHub>();
builder.Services.AddSingleton<ServiceHistoryStore>();
builder.Services.AddSingleton<ServiceHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceHealthMonitor>());
builder.Services.AddSingleton<WeatherHistoryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WeatherHistoryService>());
builder.Services.AddSingleton<WakeOnLanService>();
builder.Services.AddSingleton<MinerClient>();
builder.Services.AddSingleton<DockerEngineClient>();
builder.Services.AddSingleton<ShareLinkService>();
builder.Services.AddSingleton<NotesStore>();
builder.Services.AddSingleton<MetricsStore>();
builder.Services.AddHostedService<MetricsHistoryService>();
builder.Services.AddSingleton<PingMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PingMonitor>());
builder.Services.AddSingleton<SpeedtestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SpeedtestService>());
builder.Services.AddHttpClient(PublicIpMonitor.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<PublicIpMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PublicIpMonitor>());
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddHttpClient(UpdateService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<DigestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DigestService>());
builder.Services.AddHttpClient(WeatherAlertMonitor.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Labby/1.0 (github.com/chrisdfennell/Labby)"); // NWS requires a UA
});
builder.Services.AddSingleton<WeatherAlertMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WeatherAlertMonitor>());
builder.Services.AddSingleton<PresenceMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PresenceMonitor>());
builder.Services.AddHostedService<NasHealthMonitor>();

// Behind a TLS-terminating reverse proxy (e.g. nginx-proxy-manager), honor its
// scheme/host headers so cookies and redirects work over https.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                               | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    // Home lab: the proxy lives on the LAN, not a fixed address.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// No UseHttpsRedirection: Labby serves plain HTTP on the LAN (and in Docker there is no https endpoint).

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseWebSockets();

// Liveness probe for Docker/monitoring; always anonymous.
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

// Down-service count for the favicon badge (status.js polls this).
var statusSummary = app.MapGet("/api/status/summary", (ServiceHealthMonitor health) =>
    Results.Json(new { down = health.Snapshot.Count(s => s.IsUp == false) }));
if (authEnabled)
    statusSummary.RequireAuthorization();

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    // Validate explicitly: the endpoint binds no form data, so the middleware wouldn't.
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest();
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

// Relays a NAS file to the browser, forwarding Range requests both ways so video
// seeking and resumable downloads work (QTS answers 206 + Content-Range itself).
static async Task RelayNasFileAsync(HttpContext context, QnapFileStation fileStation,
    string folderPath, string fileName, string? contentType, bool attachment, CancellationToken ct)
{
    var range = context.Request.Headers.Range.ToString();
    var upstream = await fileStation.OpenDownloadAsync(folderPath, fileName, range.Length > 0 ? range : null, ct);
    context.Response.RegisterForDispose(upstream);

    var response = context.Response;
    response.StatusCode = (int)upstream.StatusCode; // 200, or 206 for a satisfied range
    response.Headers.AcceptRanges = "bytes";
    if (upstream.Content.Headers.ContentRange is { } contentRange)
        response.Headers.ContentRange = contentRange.ToString();
    if (upstream.Content.Headers.ContentLength is { } length)
        response.ContentLength = length;
    response.ContentType = contentType
        ?? upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    if (attachment)
    {
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName);
        response.Headers.ContentDisposition = disposition.ToString();
    }
    await upstream.Content.CopyToAsync(response.Body, ct);
}

// Signed, expiring share links — the HMAC token is the authorization, so no login.
app.MapGet("/share/{token}", async (string token, ShareLinkService shareLinks, QnapFileStation fileStation, HttpContext context, CancellationToken ct) =>
{
    if (shareLinks.Validate(token) is not { } link)
        return Results.NotFound("This share link is invalid or has expired.");

    try
    {
        await RelayNasFileAsync(context, fileStation, link.FolderPath, link.FileName, null, attachment: true, ct);
        return Results.Empty;
    }
    catch (HttpRequestException)
    {
        return Results.NotFound("The shared file is no longer available.");
    }
}).AllowAnonymous();

// Downloads a consistent snapshot of the SQLite database (history, notes, metrics).
var backup = app.MapGet("/api/backup", async (MetricsStore metrics, CancellationToken ct) =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"labby-backup-{Guid.NewGuid():N}.db");
    await metrics.BackupToAsync(temp, ct);
    var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
        FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    return Results.Stream(stream, "application/octet-stream",
        fileDownloadName: $"labby-backup-{DateTimeOffset.Now:yyyy-MM-dd}.db");
});
if (authEnabled)
    backup.RequireAuthorization();

// Streams NAS file downloads through the app (the browser can't use our QTS session directly).
// inline=true serves the file for in-browser display (previews) instead of as an attachment.
var previewTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
var download = app.MapGet("/api/files/download", async (string path, string name, QnapFileStation fileStation, HttpContext context, CancellationToken ct, bool inline = false) =>
{
    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
        return Results.BadRequest();

    string? contentType = null;
    if (inline)
    {
        // QTS answers octet-stream for everything; the extension knows better.
        contentType = previewTypes.TryGetContentType(name, out var guessed)
            ? guessed
            : "text/plain; charset=utf-8"; // .log, .conf, .yml… render as text
    }
    await RelayNasFileAsync(context, fileStation, path, name, contentType, attachment: !inline, ct);
    return Results.Empty;
});
if (authEnabled)
    download.RequireAuthorization();

// Bridges a browser terminal (xterm.js) to `docker exec` inside a container.
// Binary frames = keystrokes; text frames = JSON control messages (resize).
var shell = app.MapGet("/ws/shell/{id}", async (HttpContext context, string id, DockerEngineClient docker) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
        return Results.BadRequest("WebSocket connections only.");

    var ct = context.RequestAborted;
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        var execId = await docker.CreateShellExecAsync(id, ct);
        await using var stream = await docker.StartExecStreamAsync(execId, ct);

        // shell → browser
        var output = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;
                await ws.SendAsync(buffer.AsMemory(0, read), System.Net.WebSockets.WebSocketMessageType.Binary, true, ct);
            }
        }, ct);

        // browser → shell
        var input = Task.Run(async () =>
        {
            var receive = new byte[8192];
            while (true)
            {
                var frame = await ws.ReceiveAsync(receive.AsMemory(), ct);
                if (frame.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    break;
                if (frame.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(receive.AsMemory(0, frame.Count));
                    if (doc.RootElement.TryGetProperty("cols", out var cols) && doc.RootElement.TryGetProperty("rows", out var rows))
                        await docker.ResizeExecAsync(execId, cols.GetInt32(), rows.GetInt32(), ct);
                }
                else
                {
                    await stream.WriteAsync(receive.AsMemory(0, frame.Count), ct);
                    await stream.FlushAsync(ct);
                }
            }
        }, ct);

        // Either side ending (shell exited, or browser left) tears the session down;
        // disposing the hijacked stream unblocks whichever pump is still awaiting.
        await Task.WhenAny(output, input);
    }
    catch (OperationCanceledException)
    {
        // Browser navigated away — normal teardown.
    }
    catch (Exception ex) when (ws.State == System.Net.WebSockets.WebSocketState.Open)
    {
        var message = System.Text.Encoding.UTF8.GetBytes($"\r\n\x1b[31m{ex.GetBaseException().Message}\x1b[0m\r\n");
        await ws.SendAsync(message, System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
    }
    finally
    {
        if (ws.State == System.Net.WebSockets.WebSocketState.Open)
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "shell ended", CancellationToken.None);
    }
    return Results.Empty;
});
if (authEnabled)
    shell.RequireAuthorization();

app.MapStaticAssets();
var components = app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
if (authEnabled)
    components.RequireAuthorization();

app.Run();
