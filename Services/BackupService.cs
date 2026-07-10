using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Periodically snapshots the SQLite database (history, notes, metrics) to a NAS
/// share via File Station. Opt-in: set Backup:SharePath (e.g. "/Public/labby-backups").
/// </summary>
public sealed class BackupService(MetricsStore metrics, QnapFileStation fileStation, IOptions<BackupOptions> options, ILogger<BackupService> logger)
    : BackgroundService
{
    public DateTimeOffset? LastBackupAt { get; private set; }
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.SharePath) || !fileStation.IsConfigured)
        {
            logger.LogInformation("Scheduled backups idle (set Backup:SharePath and QNAP credentials)");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromDays(Math.Max(1, options.Value.Days)));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                LastError = null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.GetBaseException().Message;
                logger.LogWarning(ex, "Scheduled backup failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"labby-backup-{Guid.NewGuid():N}.db");
        try
        {
            await metrics.BackupToAsync(temp, ct);
            await using var stream = File.OpenRead(temp);
            var name = $"labby-backup-{DateTimeOffset.Now:yyyy-MM-dd}.db";
            await fileStation.UploadAsync(options.Value.SharePath, name, stream, ct);
            LastBackupAt = DateTimeOffset.Now;
            logger.LogInformation("Backed up database to {Share}/{Name}", options.Value.SharePath, name);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
