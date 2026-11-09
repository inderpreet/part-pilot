using InventoryTracker.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public sealed record BackupSnapshot(string Path, string DownloadName) : IDisposable
{
    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
        }
    }
}

public interface IBackupService
{
    /// <summary>A consistent copy of the live SQLite file (<c>VACUUM INTO</c>). The caller streams it and disposes to delete it.</summary>
    Task<BackupSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken = default);

    Task NotifyDownloadedAsync(string downloadName, string? userId, CancellationToken cancellationToken = default);
}

public sealed class BackupService(AppDbContext db, IClock clock, BackupEventPublisher events) : IBackupService
{
    public async Task<BackupSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pp-backup-{Guid.NewGuid():N}.db");
        var escaped = path.Replace("'", "''", StringComparison.Ordinal);
#pragma warning disable EF1002 // Path is generated here (a Guid under GetTempPath), not taken from the user.
        await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escaped}'", cancellationToken);
#pragma warning restore EF1002
        return new BackupSnapshot(path, $"inventory-{clock.UtcNow:yyyyMMdd-HHmm}.db");
    }

    public Task NotifyDownloadedAsync(string downloadName, string? userId, CancellationToken cancellationToken = default)
        => events.PublishAsync(new BackupDownloadedEvent(downloadName, userId, clock.UtcNow), cancellationToken);
}
