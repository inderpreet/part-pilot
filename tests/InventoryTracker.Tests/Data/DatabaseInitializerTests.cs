using InventoryTracker.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Data;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inventory-tests-").FullName;

    private string DbPath => Path.Combine(_dir, "inventory.db");

    private AppDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseInventorySqlite($"Data Source={DbPath}");
        return new AppDbContext(builder.Options);
    }

    private static DatabaseInitializer CreateInitializer(AppDbContext context)
        => new(context, NullLogger<DatabaseInitializer>.Instance);

    [Fact]
    public async Task Fresh_run_creates_database_with_settings_wal_and_no_backup()
    {
        Assert.False(File.Exists(DbPath));

        await using (var context = CreateContext())
        {
            var backup = await CreateInitializer(context).MigrateAsync();
            Assert.Null(backup);
        }

        Assert.True(File.Exists(DbPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.bak-*"));

        await using (var context = CreateContext())
        {
            Assert.Equal(SettingKeys.Defaults.Count, await context.AppSettings.CountAsync());
            Assert.Equal("wal", await SqliteTestDb.ScalarAsync(context, "PRAGMA journal_mode"));
        }
    }

    [Fact]
    public async Task Up_to_date_database_is_not_backed_up()
    {
        await using (var context = CreateContext())
        {
            await CreateInitializer(context).MigrateAsync();
        }

        await using (var context = CreateContext())
        {
            Assert.Null(await CreateInitializer(context).MigrateAsync());
        }

        Assert.Empty(Directory.GetFiles(_dir, "*.bak-*"));
    }

    [Fact]
    public async Task Pending_migration_on_existing_file_takes_backup_first()
    {
        await using (var context = CreateContext())
        {
            await CreateInitializer(context).MigrateAsync();

            // Roll back to an empty schema so the Initial migration is pending again on an existing file.
            await context.GetService<IMigrator>().MigrateAsync("0");
            Assert.NotEmpty(await context.Database.GetPendingMigrationsAsync());
        }

        string? backup;
        await using (var context = CreateContext())
        {
            backup = await CreateInitializer(context).MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        Assert.NotNull(backup);
        Assert.StartsWith($"{DbPath}.bak-", backup);
        Assert.True(File.Exists(backup));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
