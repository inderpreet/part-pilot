using InventoryTracker.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-06-1: the reporting indexes on the stock ledger, and the migration that adds them.</summary>
public sealed class ReportingIndexSchemaTests
{
    private const string TypeIndex = "IX_StockTransactions_Type";
    private const string UsageIndex = "IX_StockTransactions_Type_OccurredAt_ProductId_QuantityDelta";
    private const string DeadStockIndex = "IX_StockTransactions_ProductId_OccurredAt";

    private static async Task<long> IndexCountAsync(AppDbContext context, string name)
        => (long)(await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{name}'"))!;

    private static async Task<string> PlanAsync(AppDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var plan = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.AppendLine(reader.GetString(3));
        }

        return plan.ToString();
    }

    [Fact]
    public async Task Fresh_database_has_the_reporting_indexes_and_no_pending_changes()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await using var context = db.CreateContext();

        Assert.Equal(1L, await IndexCountAsync(context, UsageIndex));
        Assert.Equal(1L, await IndexCountAsync(context, DeadStockIndex));
        Assert.Equal(0L, await IndexCountAsync(context, TypeIndex));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task Usage_by_product_is_answered_from_the_index_alone()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await using var context = db.CreateContext();

        var plan = await PlanAsync(context,
            "SELECT \"ProductId\", SUM(\"QuantityDelta\") FROM \"StockTransactions\" WHERE \"Type\" = 'Issue' AND \"OccurredAt\" >= '2026-01-01 00:00:00' GROUP BY \"ProductId\"");

        Assert.Contains($"COVERING INDEX {UsageIndex}", plan);
    }

    [Fact]
    public async Task Movements_of_one_kind_in_a_date_range_use_the_type_and_date_index()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await using var context = db.CreateContext();

        var plan = await PlanAsync(context,
            "SELECT * FROM \"StockTransactions\" WHERE \"Type\" = 'Receive' AND \"OccurredAt\" >= '2026-01-01 00:00:00' ORDER BY \"OccurredAt\" DESC LIMIT 50");

        Assert.Contains(UsageIndex, plan);
        Assert.DoesNotContain("TEMP B-TREE", plan);
    }

    [Fact]
    public async Task Whether_a_product_moved_since_a_date_is_an_index_seek()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await using var context = db.CreateContext();

        var plan = await PlanAsync(context,
            "SELECT 1 FROM \"StockTransactions\" WHERE \"ProductId\" = 5 AND \"OccurredAt\" >= '2026-01-01 00:00:00' LIMIT 1");

        Assert.Contains($"COVERING INDEX {DeadStockIndex} (ProductId=? AND OccurredAt>?)", plan);
    }

    [Fact]
    public async Task Migration_applies_on_top_of_the_previous_schema_keeping_the_ledger_and_its_triggers()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var context = new AppDbContext(Options()))
        {
            var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddReportingIndexes", StringComparison.Ordinal));
            Assert.True(index > 0);
            await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
            Assert.Equal(1L, await IndexCountAsync(context, TypeIndex));
            Assert.Equal(0L, await IndexCountAsync(context, UsageIndex));

            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO AspNetUsers (Id, DisplayName, IsActive, UserName, NormalizedUserName, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) " +
                "VALUES ('u1', 'U One', 1, 'u1', 'U1', 0, 0, 0, 0, 0)");
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO Products (Sku, Name, Unit, ReorderPoint, ReorderQuantity, UnitCostCents, QuantityOnHand, IsActive, RowVersion, CreatedAt, UpdatedAt) " +
                "VALUES ('KEEP-1', 'Kept', 'each', 0, 0, 0, 7, 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO StockTransactions (ProductId, Type, QuantityDelta, QuantityAfter, UserId, OccurredAt, CreatedAt) " +
                "VALUES (1, 'Receive', 7, 7, 'u1', '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        }

        await using (var context = new AppDbContext(Options()))
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());

            Assert.Equal(0L, await IndexCountAsync(context, TypeIndex));
            Assert.Equal(1L, await IndexCountAsync(context, UsageIndex));
            Assert.Equal(1L, await IndexCountAsync(context, DeadStockIndex));
            Assert.Equal(7, (await context.StockTransactions.SingleAsync()).QuantityDelta);
            Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("UPDATE StockTransactions SET Note = 'x'"));
        }
    }

    [Fact]
    public async Task The_migration_can_be_rolled_back_and_reapplied()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();

        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_AddReportingIndexes", StringComparison.Ordinal));

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        Assert.Equal(1L, await IndexCountAsync(context, TypeIndex));
        Assert.Equal(0L, await IndexCountAsync(context, UsageIndex));
        Assert.Equal(0L, await IndexCountAsync(context, DeadStockIndex));

        await context.Database.MigrateAsync();
        Assert.Equal(1L, await IndexCountAsync(context, UsageIndex));
        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));
    }
}
