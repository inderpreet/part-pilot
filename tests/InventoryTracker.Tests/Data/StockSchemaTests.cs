using InventoryTracker.Data;
using InventoryTracker.Data.Configurations;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-03-1: the StockTransaction ledger table, its constraints, indexes and immutability triggers.</summary>
public sealed class StockSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private int _productId;
    private string _userId = null!;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await using var context = _db.CreateContext();
        var user = new ApplicationUser { UserName = "stocker", DisplayName = "Stock Person" };
        var product = new Product { Sku = "P-1", Name = "Widget" };
        context.Users.Add(user);
        context.Products.Add(product);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _productId = product.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private StockTransaction Row(
        StockTransactionType type = StockTransactionType.Receive, int delta = 5, int after = 5, long? reverses = null, int? productId = null) => new()
    {
        ProductId = productId ?? _productId,
        Type = type,
        QuantityDelta = delta,
        QuantityAfter = after,
        ReversesTransactionId = reverses,
        UserId = _userId,
        OccurredAt = DateTime.UtcNow,
    };

    private static string Message(Exception ex) => (ex.InnerException ?? ex).Message;

    private async Task<long> AddAsync(StockTransaction row)
    {
        await using var context = _db.CreateContext();
        context.StockTransactions.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public async Task Fresh_database_has_the_ledger_table_indexes_and_triggers_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='StockTransactions'"));
        foreach (var index in new[]
                 {
                     "IX_StockTransactions_ProductId_Id", "IX_StockTransactions_OccurredAt",
                     "IX_StockTransactions_Type_OccurredAt_ProductId_QuantityDelta", "IX_StockTransactions_ProductId_OccurredAt",
                     "IX_StockTransactions_UserId", "IX_StockTransactions_PurchaseOrderLineId", "IX_StockTransactions_ReversesTransactionId",
                 })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}'"));
        }

        foreach (var trigger in new[] { StockTransactionConfiguration.NoUpdateTrigger, StockTransactionConfiguration.NoDeleteTrigger })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='{trigger}'"));
        }

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task A_row_stores_the_type_as_text_and_gets_a_utc_created_at_but_no_updated_at()
    {
        var id = await AddAsync(Row(StockTransactionType.Return));

        await using var context = _db.CreateContext();
        Assert.Equal("Return", await SqliteTestDb.ScalarAsync(context, $"SELECT Type FROM StockTransactions WHERE Id = {id}"));

        var row = await context.StockTransactions.SingleAsync();
        Assert.Equal(StockTransactionType.Return, row.Type);
        Assert.Equal(DateTimeKind.Utc, row.CreatedAt.Kind);
        Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(DateTimeKind.Utc, row.OccurredAt.Kind);
        Assert.Null(row.Reason);
        Assert.Null(row.PurchaseOrderLineId);
        Assert.Null(row.ImportBatchId);

        var columns = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StockTransaction))!.GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("UpdatedAt", columns);
    }

    [Fact]
    public async Task The_created_at_timestamp_comes_from_the_injected_clock()
    {
        var clock = new FakeClock(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        await using (var context = _db.CreateContext(clock))
        {
            context.StockTransactions.Add(Row());
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        Assert.Equal(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc), (await read.StockTransactions.SingleAsync()).CreatedAt);
    }

    [Fact]
    public async Task A_zero_delta_is_rejected()
    {
        await using var context = _db.CreateContext();
        context.StockTransactions.Add(Row(delta: 0, after: 0));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("CK_StockTransactions_QuantityDelta", Message(ex));
    }

    [Fact]
    public async Task Negative_deltas_and_negative_quantities_after_are_storable()
    {
        // The ledger records what happened; whether negative stock is allowed is a service rule (Stock.AllowNegative).
        await AddAsync(Row(StockTransactionType.Issue, delta: -3, after: -3));

        await using var context = _db.CreateContext();
        var row = await context.StockTransactions.SingleAsync();
        Assert.Equal(-3, row.QuantityDelta);
        Assert.Equal(-3, row.QuantityAfter);
    }

    [Fact]
    public async Task An_unknown_type_is_rejected_by_the_database()
    {
        await using var context = _db.CreateContext();
        var ex = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlAsync($"INSERT INTO StockTransactions (ProductId, Type, QuantityDelta, QuantityAfter, UserId, OccurredAt, CreatedAt) VALUES ({_productId}, 'Steal', 1, 1, {_userId}, '2026-01-01 00:00:00', '2026-01-01 00:00:00')"));
        Assert.Contains("CK_StockTransactions_Type", ex.Message);
    }

    [Fact]
    public async Task A_reversal_must_point_at_a_transaction_and_only_a_reversal_may()
    {
        var original = await AddAsync(Row());

        await using (var context = _db.CreateContext())
        {
            context.StockTransactions.Add(Row(StockTransactionType.Reversal, delta: -5, after: 0, reverses: null));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("CK_StockTransactions_Reversal", Message(ex));
        }

        await using (var context = _db.CreateContext())
        {
            context.StockTransactions.Add(Row(StockTransactionType.Issue, delta: -5, after: 0, reverses: original));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("CK_StockTransactions_Reversal", Message(ex));
        }

        await using (var context = _db.CreateContext())
        {
            context.StockTransactions.Add(Row(StockTransactionType.Reversal, delta: -5, after: 0, reverses: original));
            Assert.Equal(1, await context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_second_reversal_of_the_same_transaction_violates_the_unique_index()
    {
        var original = await AddAsync(Row());
        await AddAsync(Row(StockTransactionType.Reversal, delta: -5, after: 0, reverses: original));

        await using var context = _db.CreateContext();
        context.StockTransactions.Add(Row(StockTransactionType.Reversal, delta: -5, after: -5, reverses: original));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("UNIQUE", Message(ex), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StockTransactions.ReversesTransactionId", Message(ex));
    }

    [Fact]
    public async Task A_reversal_of_a_row_that_does_not_exist_is_rejected()
    {
        await using var context = _db.CreateContext();
        context.StockTransactions.Add(Row(StockTransactionType.Reversal, delta: -5, after: 0, reverses: 9999));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rows_need_an_existing_product_and_user()
    {
        await using (var context = _db.CreateContext())
        {
            context.StockTransactions.Add(Row(productId: 9999));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using (var context = _db.CreateContext())
        {
            var row = Row();
            row.UserId = "no-such-user";
            context.StockTransactions.Add(row);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_product_or_user_with_transactions_cannot_be_deleted()
    {
        await AddAsync(Row());

        await using (var context = _db.CreateContext())
        {
            context.Products.Remove(await context.Products.SingleAsync());
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
        }

        await using (var context = _db.CreateContext())
        {
            context.Users.Remove(await context.Users.SingleAsync());
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Rows_cannot_be_updated_not_through_ef_and_not_through_raw_sql()
    {
        var id = await AddAsync(Row());

        await using (var context = _db.CreateContext())
        {
            var row = await context.StockTransactions.SingleAsync();
            row.QuantityDelta = 99;
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains(StockTransactionConfiguration.ImmutableMessage, Message(ex));
        }

        await using (var context = _db.CreateContext())
        {
            var ex = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlAsync($"UPDATE StockTransactions SET QuantityDelta = 99 WHERE Id = {id}"));
            Assert.Contains(StockTransactionConfiguration.ImmutableMessage, ex.Message);
        }

        await using (var context = _db.CreateContext())
        {
            Assert.Equal(5, (await context.StockTransactions.SingleAsync()).QuantityDelta);
        }
    }

    [Fact]
    public async Task Rows_cannot_be_deleted()
    {
        var id = await AddAsync(Row());

        await using (var context = _db.CreateContext())
        {
            context.StockTransactions.Remove(await context.StockTransactions.SingleAsync());
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains(StockTransactionConfiguration.ImmutableMessage, Message(ex));
        }

        await using (var context = _db.CreateContext())
        {
            var ex = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlAsync($"DELETE FROM StockTransactions WHERE Id = {id}"));
            Assert.Contains(StockTransactionConfiguration.ImmutableMessage, ex.Message);
            Assert.Equal(1, await context.StockTransactions.CountAsync());
        }
    }

    [Fact]
    public async Task Text_lengths_are_enforced_by_the_model()
    {
        await using var context = _db.CreateContext();
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StockTransaction))!;

        Assert.Equal(20, entity.FindProperty(nameof(StockTransaction.Type))!.GetMaxLength());
        Assert.Equal(100, entity.FindProperty(nameof(StockTransaction.Reason))!.GetMaxLength());
        Assert.Equal(100, entity.FindProperty(nameof(StockTransaction.Reference))!.GetMaxLength());
        Assert.Equal(500, entity.FindProperty(nameof(StockTransaction.Note))!.GetMaxLength());
    }

    [Fact]
    public async Task Product_stock_transactions_navigation_returns_the_rows_in_posting_order()
    {
        await AddAsync(Row(delta: 5, after: 5));
        await AddAsync(Row(StockTransactionType.Issue, delta: -2, after: 3));

        await using var context = _db.CreateContext();
        var product = await context.Products.Include(p => p.StockTransactions).SingleAsync();
        Assert.Equal([5, -2], product.StockTransactions.OrderBy(t => t.Id).Select(t => t.QuantityDelta));
    }

    [Fact]
    public async Task Migration_applies_on_top_of_the_previous_schema_and_preserves_data()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        // Upgrade only as far as the catalog schema (the migration just before AddStockLedger), then put data in.
        await using (var context = new AppDbContext(Options()))
        {
            var previous = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().First(k => k.EndsWith("_AddCatalog", StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(previous);
            Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name='StockTransactions'"));

            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO Products (Sku, Name, Unit, ReorderPoint, ReorderQuantity, UnitCostCents, QuantityOnHand, IsActive, RowVersion, CreatedAt, UpdatedAt) " +
                "VALUES ('KEEP-1', 'Kept', 'each', 0, 0, 0, 7, 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        }

        await using (var context = new AppDbContext(Options()))
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name='StockTransactions'"));

            var kept = await context.Products.SingleAsync();
            Assert.Equal("KEEP-1", kept.Sku);
            Assert.Equal(7, kept.QuantityOnHand);
        }
    }

    [Fact]
    public async Task The_migration_can_be_rolled_back_and_reapplied()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var context = new AppDbContext(Options());
        await context.Database.MigrateAsync();

        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
        var ledger = migrations.FindIndex(m => m.EndsWith("_AddStockLedger", StringComparison.Ordinal));
        Assert.True(ledger > 0);
        var previous = migrations[ledger - 1];

        await context.GetService<IMigrator>().MigrateAsync(previous);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%StockTransactions%'"));

        await context.Database.MigrateAsync();
        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions%'"));
    }
}
