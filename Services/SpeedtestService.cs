using System.Diagnostics;
using System.Text.Json;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Runs librespeed-cli (bundled in the Docker image) on the configured interval
/// and stores download/upload/ping in the metrics store. Off by default —
/// speed tests use real bandwidth.
/// </summary>
public sealed class SpeedtestService(MetricsStore store, AlertNotifier alerts, IOptions<NetworkOptions> options, ILogger<SpeedtestService> logger)
    : BackgroundService
{
    private const string Binary = "librespeed-cli";

    public bool IsEnabled => options.Value.SpeedtestHours > 0;
    public bool BinaryAvailable { get; private set; }
    public bool IsRunning { get; private set; }

    public MetricsStore.SpeedtestResult? Latest { get; private set; }

    /// <summary>Runs a test on demand; returns an error message or null on success.</summary>
    public async Task<string?> TriggerAsync(CancellationToken ct = default)
    {
        if (IsRunning)
            return "A test is already running.";
        if (!File.Exists($"/usr/local/bin/{Binary}") && !File.Exists($"/usr/bin/{Binary}"))
            return "librespeed-cli isn't available here (it ships in the Docker image).";
        IsRunning = true;
        try
        {
            await RunOnceAsync(ct);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "On-demand speedtest failed");
            return ex.GetBaseException().Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            logger.LogInformation("Speedtests disabled (Network:SpeedtestHours is 0)");
            return;
        }

        BinaryAvailable = File.Exists($"/usr/local/bin/{Binary}") || File.Exists($"/usr/bin/{Binary}");
        if (!BinaryAvailable)
        {
            logger.LogWarning("librespeed-cli not found in the image; speedtests unavailable");
            return;
        }

        // Seed "latest" from history so a restart doesn't blank the tile.
        Latest = (await store.GetSpeedtestsAsync(DateTimeOffset.Now.AddDays(-7), stoppingToken)).LastOrDefault();

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.SpeedtestHours));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Speedtest failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Binary,
            Arguments = "--json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"librespeed-cli exited {process.ExitCode}");

        // librespeed-cli --json emits an array of results (one per server).
        using var doc = JsonDocument.Parse(output);
        var first = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
            ? doc.RootElement[0]
            : doc.RootElement;
        var down = first.GetProperty("download").GetDouble();
        var up = first.GetProperty("upload").GetDouble();
        var ping = first.GetProperty("ping").GetDouble();

        var now = DateTimeOffset.Now;
        Latest = new MetricsStore.SpeedtestResult(now, down, up, ping);
        await store.WriteSpeedtestAsync(now, down, up, ping, ct);
        logger.LogInformation("Speedtest: ↓{Down:0.#} Mbps ↑{Up:0.#} Mbps ping {Ping:0} ms", down, up, ping);

        if (options.Value.MinDownloadMbps > 0 && down < options.Value.MinDownloadMbps)
            await alerts.SendAsync($"🐌 Internet speed is down: {down:0.#} Mbps (expected ≥ {options.Value.MinDownloadMbps:0.#} Mbps)", ct);
    }
}
