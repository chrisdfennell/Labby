using Labby.Components;
using Labby.Options;
using Labby.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<QnapOptions>(builder.Configuration.GetSection(QnapOptions.SectionName));
builder.Services.Configure<AmbientWeatherOptions>(builder.Configuration.GetSection(AmbientWeatherOptions.SectionName));
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection(DashboardOptions.SectionName));
builder.Services.Configure<KontainrOptions>(builder.Configuration.GetSection(KontainrOptions.SectionName));

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
builder.Services.AddSingleton<ServiceHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceHealthMonitor>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// No UseHttpsRedirection: Labby serves plain HTTP on the LAN (and in Docker there is no https endpoint).

app.UseAntiforgery();

// Streams NAS file downloads through the app (the browser can't use our QTS session directly).
app.MapGet("/api/files/download", async (string path, string name, QnapFileStation fileStation, HttpContext context, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
        return Results.BadRequest();

    var upstream = await fileStation.OpenDownloadAsync(path, name, ct);
    context.Response.RegisterForDispose(upstream);
    var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    return Results.Stream(await upstream.Content.ReadAsStreamAsync(ct), contentType, fileDownloadName: name);
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
