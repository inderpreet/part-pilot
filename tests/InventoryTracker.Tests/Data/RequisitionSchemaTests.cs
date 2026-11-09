using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-11-1: requisition and line tables, their constraints, the number prefix setting and the migration.</summary>
public sealed class RequisitionSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private string _userId = null!;
    private int _productId;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await using var context = _db.CreateContext();
        var user = new ApplicationUser { UserName = "asker", DisplayName = "Ann Asker" };
        var product = new Product { Sku = "P-1", Name = "Widget" };
        context.AddRange(user, product);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _productId = product.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string Message(Exception ex) => (ex.InnerException ?? ex).Message;

    private async Task<int> SeedRequisitionAsync(string number = "REQ-2026-0001")
    {
        await using var context = _db.CreateContext();
        var requisition = new Requisition { Number = number, RequestedById = _userId };
        context.Requisitions.Add(requisition);
        await context.SaveChangesAsync();
        return requisition.Id;
    }

    private async Task AddLineAsync(int requisitionId, int quantity = 1)
    {
        await using var context = _db.CreateContext();
        context.RequisitionLines.Add(new RequisitionLine { RequisitionId = requisitionId, ProductId = _productId, QuantityRequested = quantity });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Fresh_database_has_the_tables_indexes_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        foreach (var table in new[] { "Requisitions", "RequisitionLines" })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        }

        foreach (var index in new[]
                 {
                     "IX_Requisitions_Number", "IX_Requisitions_Status_SubmittedAt", "IX_Requisitions_RequestedById",
                     "IX_RequisitionLines_RequisitionId_ProductId", "IX_RequisitionLines_ProductId",
                 })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}'"));
        }

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task A_new_requisition_is_a_draft_with_timestamps_and_a_row_version()
    {
        var id = await SeedRequisitionAsync();

        await using var read = _db.CreateContext();
        var saved = await read.Requisitions.SingleAsync(r => r.Id == id);
        Assert.Equal(RequisitionStatus.Draft, saved.Status);
        Assert.Equal(1L, saved.RowVersion);
        Assert.Null(saved.Reason);
        Assert.Null(saved.NeededBy);
        Assert.Null(saved.SubmittedAt);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);
    }

    [Fact]
    public async Task Numbers_are_unique_ignoring_case()
    {
        await SeedRequisitionAsync("REQ-2026-0001");
        await using var context = _db.CreateContext();
        context.Requisitions.Add(new Requisition { Number = "req-2026-0001", RequestedById = _userId });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("UNIQUE", Message(error), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Submitted")]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    public async Task Every_status_is_allowed_from_the_first_migration(string status)
    {
        var id = await SeedRequisitionAsync();
        await using var context = _db.CreateContext();

        Assert.Equal(1, await context.Database.ExecuteSqlRawAsync("UPDATE Requisitions SET Status = {0} WHERE Id = {1}", status, id));
    }

    [Fact]
    public async Task An_unknown_status_is_refused()
    {
        var id = await SeedRequisitionAsync();
        await using var context = _db.CreateContext();

        var error = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("UPDATE Requisitions SET Status = 'Ordered' WHERE Id = {0}", id));

        Assert.Contains("CK_Requisitions_Status", error.Message);
    }

    [Fact]
    public async Task A_second_line_for_the_same_part_is_refused()
    {
        var id = await SeedRequisitionAsync();
        await AddLineAsync(id);

        await using var context = _db.CreateContext();
        context.RequisitionLines.Add(new RequisitionLine { RequisitionId = id, ProductId = _productId, QuantityRequested = 5 });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("UNIQUE", Message(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_part_can_be_on_two_requisitions()
    {
        await AddLineAsync(await SeedRequisitionAsync("REQ-2026-0001"));
        await AddLineAsync(await SeedRequisitionAsync("REQ-2026-0002"));

        await using var read = _db.CreateContext();
        Assert.Equal(2, await read.RequisitionLines.CountAsync());
    }

    [Fact]
    public async Task Quantity_below_one_is_rejected()
    {
        var id = await SeedRequisitionAsync();
        await using var context = _db.CreateContext();
        context.RequisitionLines.Add(new RequisitionLine { RequisitionId = id, ProductId = _productId, QuantityRequested = 0 });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("CK_RequisitionLines_QuantityRequested", Message(error));
    }

    [Fact]
    public async Task A_product_on_a_requisition_cannot_be_deleted()
    {
        await AddLineAsync(await SeedRequisitionAsync());

        await using var context = _db.CreateContext();
        var error = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM Products WHERE Id = {0}", _productId));

        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_user_who_raised_a_requisition_cannot_be_deleted()
    {
        await SeedRequisitionAsync();

        await using var context = _db.CreateContext();
        var error = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM AspNetUsers WHERE Id = {0}", _userId));

        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_requisition_removes_its_lines_and_keeps_the_part()
    {
        await AddLineAsync(await SeedRequisitionAsync());

        await using (var context = _db.CreateContext())
        {
            context.Requisitions.Remove(await context.Requisitions.SingleAsync());
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        Assert.Empty(await read.RequisitionLines.ToListAsync());
        Assert.Equal(1, await read.Products.CountAsync());
    }

    [Fact]
    public async Task The_number_prefix_setting_has_its_default()
    {
        await using var context = _db.CreateContext();

        var setting = await context.AppSettings.SingleAsync(s => s.Key == SettingKeys.RequisitionNumberPrefix);

        Assert.Equal("REQ-", setting.Value);
        Assert.Equal("REQ-", SettingKeys.Defaults[SettingKeys.RequisitionNumberPrefix]);
    }

    [Fact]
    public async Task Migration_applies_on_top_of_the_previous_schema_keeping_data_and_can_be_rolled_back()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_AddRequisitions", StringComparison.Ordinal));
        Assert.True(index > 0);

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Requisitions', 'RequisitionLines')"));
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Products (Sku, Name, Unit, ReorderPoint, ReorderQuantity, UnitCostCents, QuantityOnHand, IsActive, RowVersion, CreatedAt, UpdatedAt) " +
            "VALUES ('KEEP-1', 'Kept', 'each', 0, 0, 0, 7, 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");

        await context.Database.MigrateAsync();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(7, (await context.Products.SingleAsync()).QuantityOnHand);
        Assert.Empty(await context.Requisitions.ToListAsync());
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM AppSettings WHERE Key = 'Requisition.NumberPrefix'"));
        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Requisitions', 'RequisitionLines')"));
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM AppSettings WHERE Key = 'Requisition.NumberPrefix'"));
        Assert.Equal(7, (await context.Products.SingleAsync()).QuantityOnHand);

        await context.Database.MigrateAsync();
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_Requisitions_Number'"));
    }
}
