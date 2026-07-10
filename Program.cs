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
builder.Services.AddHostedService<NasHealthMonitor>();

var app = builder.Build();

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

// Liveness probe for Docker/monitoring; always anonymous.
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    // Validate explicitly: the endpoint binds no form data, so the middleware wouldn't.
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest();
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

// Signed, expiring share links — the HMAC token is the authorization, so no login.
app.MapGet("/share/{token}", async (string token, ShareLinkService shareLinks, QnapFileStation fileStation, HttpContext context, CancellationToken ct) =>
{
    if (shareLinks.Validate(token) is not { } link)
        return Results.NotFound("This share link is invalid or has expired.");

    try
    {
        var upstream = await fileStation.OpenDownloadAsync(link.FolderPath, link.FileName, ct);
        context.Response.RegisterForDispose(upstream);
        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        return Results.Stream(await upstream.Content.ReadAsStreamAsync(ct), contentType, fileDownloadName: link.FileName);
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
var download = app.MapGet("/api/files/download", async (string path, string name, QnapFileStation fileStation, HttpContext context, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
        return Results.BadRequest();

    var upstream = await fileStation.OpenDownloadAsync(path, name, ct);
    context.Response.RegisterForDispose(upstream);
    var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    return Results.Stream(await upstream.Content.ReadAsStreamAsync(ct), contentType, fileDownloadName: name);
});
if (authEnabled)
    download.RequireAuthorization();

app.MapStaticAssets();
var components = app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
if (authEnabled)
    components.RequireAuthorization();

app.Run();
