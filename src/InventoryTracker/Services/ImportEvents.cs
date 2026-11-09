using InventoryTracker.Data.Entities;

namespace InventoryTracker.Services;

public sealed record ImportCommittedEvent(
    long BatchId,
    ImportKind Kind,
    string FileName,
    int CreatedCount,
    int UpdatedCount,
    int SkippedCount,
    string? UserId,
    DateTime OccurredAtUtc);

public interface IImportEvents
{
    Task HandleAsync(ImportCommittedEvent importEvent, CancellationToken cancellationToken = default);
}

public sealed class ImportEventPublisher(IEnumerable<IImportEvents> handlers, ILogger<ImportEventPublisher> logger)
{
    public async Task PublishAsync(ImportCommittedEvent importEvent, CancellationToken cancellationToken = default)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(importEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Import event handler {Handler} failed", handler.GetType().Name);
            }
        }
    }
}

public sealed record BackupDownloadedEvent(string FileName, string? UserId, DateTime OccurredAtUtc);

public interface IBackupEvents
{
    Task HandleAsync(BackupDownloadedEvent backupEvent, CancellationToken cancellationToken = default);
}

public sealed class BackupEventPublisher(IEnumerable<IBackupEvents> handlers, ILogger<BackupEventPublisher> logger)
{
    public async Task PublishAsync(BackupDownloadedEvent backupEvent, CancellationToken cancellationToken = default)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(backupEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Backup event handler {Handler} failed", handler.GetType().Name);
            }
        }
    }
}
