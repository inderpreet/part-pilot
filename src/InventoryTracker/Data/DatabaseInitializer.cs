using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Data;

/// <summary>Applies pending migrations at startup, backing up an existing DB file first (F00 FR-2).</summary>
public class DatabaseInitializer(AppDbContext db, ILogger<DatabaseInitializer> logger)
{
    /// <summary>Migrates the database. Returns the backup file path if one was taken, otherwise <c>null</c>.</summary>
    public async Task<string?> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var dbPath = GetDatabasePath();
        var fileExisted = dbPath is not null && File.Exists(dbPath);

        string? backupPath = null;
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        if (pending.Count > 0 && fileExisted)
        {
            backupPath = await BackupAsync(dbPath!, cancellationToken);
            logger.LogInformation("Backed up database to {BackupPath} before applying {Count} migration(s)", backupPath, pending.Count);
        }

        if (pending.Count > 0)
        {
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Applied migrations: {Migrations}", string.Join(", ", pending));
        }

        return backupPath;
    }

    private string? GetDatabasePath()
    {
        var dataSource = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(dataSource);
    }

    private async Task<string> BackupAsync(string dbPath, CancellationToken cancellationToken)
    {
        // Flush the WAL into the main file so a plain file copy is a complete snapshot.
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);

        var backupPath = $"{dbPath}.bak-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";
        File.Copy(dbPath, backupPath, overwrite: false);
        return backupPath;
    }
}
