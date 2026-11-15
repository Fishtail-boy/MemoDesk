using MemoDesk.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoDesk.App.Services;

public sealed class DatabaseBackupWorker(DatabaseBackupService backups, ILogger<DatabaseBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            backups.RefreshDailyBackup();
            if (backups.BackupFailed) logger.LogWarning("备忘自动备份失败，将在下一轮重试。");
        }
    }
}
