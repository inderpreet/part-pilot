using InventoryTracker.Data;
using InventoryTracker.Data.Configurations;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-05-1: purchase order tables, constraints, the stock-transaction foreign key and the migration.</summary>
public sealed class PurchaseOrderSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private string _userId = null!;
    private int _productId;
    private int _supplierId;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await using var context = _db.CreateContext();
        var user = new ApplicationUser { UserName = "buyer", DisplayName = "Bea Buyer" };
        var product = new Product { Sku = "P-1", Name = "Widget" };
        var supplier = new Supplier { Name = "Acme" };
        context.AddRange(user, product, supplier);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _productId = product.Id;
        _supplierId = supplier.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string Message(Exception ex) => (ex.InnerException ?? ex).Message;

    private PurchaseOrder Order(string number = "PO-2026-0001", PurchaseOrderStatus status = PurchaseOrderStatus.Draft) => new()
    {
        Number = number,
        SupplierId = _supplierId,
        Status = status,
        OrderDate = new DateOnly(2026, 3, 9),
        CreatedById = _userId,
    };

    private async Task<(int OrderId, long LineId)> SeedLineAsync(int quantity = 5)
    {
        await using var context = _db.CreateContext();
        var order = Order();
        order.Lines.Add(new PurchaseOrderLine { ProductId = _productId, QuantityOrdered = quantity, UnitCostCents = 250 });
        context.PurchaseOrders.Add(order);
        await context.SaveChangesAsync();
        return (order.Id, order.Lines[0].Id);
    }

    private StockTransaction Receipt(long? lineId) => new()
    {
        ProductId = _productId,
        Type = StockTransactionType.Receive,
        QuantityDelta = 5,
        QuantityAfter = 5,
        PurchaseOrderLineId = lineId,
        UserId = _userId,
        OccurredAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Fresh_database_has_the_purchase_order_tables_indexes_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        foreach (var table in new[] { "PurchaseOrders", "PurchaseOrderLines", "DocumentSequences" })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        }

        foreach (var index in new[]
                 {
                     "IX_PurchaseOrders_Number", "IX_PurchaseOrders_Status", "IX_PurchaseOrders_SupplierId_OrderDate", "IX_PurchaseOrders_CreatedById",
                     "IX_PurchaseOrderLines_PurchaseOrderId_ProductId", "IX_PurchaseOrderLines_ProductId",
                 })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}'"));
        }

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task A_new_order_has_defaults_and_stores_dates_and_status_as_text()
    {
        await using (var context = _db.CreateContext())
        {
            var order = Order();
            order.ExpectedDate = new DateOnly(2026, 3, 20);
            context.PurchaseOrders.Add(order);
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        var saved = await read.PurchaseOrders.SingleAsync();
        Assert.Equal(PurchaseOrderStatus.Draft, saved.Status);
        Assert.Equal(new DateOnly(2026, 3, 9), saved.OrderDate);
        Assert.Equal(new DateOnly(2026, 3, 20), saved.ExpectedDate);
        Assert.Null(saved.SubmittedAt);
        Assert.Null(saved.ClosedAt);
        Assert.Equal(1L, saved.RowVersion);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);

        Assert.Equal("Draft", (string?)await SqliteTestDb.ScalarAsync(read, "SELECT Status FROM PurchaseOrders"));
        Assert.Equal("2026-03-09", (string?)await SqliteTestDb.ScalarAsync(read, "SELECT OrderDate FROM PurchaseOrders"));
    }

    [Fact]
    public async Task Order_numbers_are_unique_case_insensitively()
    {
        await using var context = _db.CreateContext();
        context.PurchaseOrders.Add(Order("PO-2026-0001"));
        await context.SaveChangesAsync();

        context.PurchaseOrders.Add(Order("po-2026-0001"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("PurchaseOrders.Number", Message(ex));
    }

    [Fact]
    public async Task An_unknown_status_is_rejected_by_a_check_constraint()
    {
        await using var context = _db.CreateContext();
        context.PurchaseOrders.Add(Order());
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(
            () => context.Database.ExecuteSqlRawAsync("UPDATE PurchaseOrders SET Status = 'Shipped'"));
        Assert.Contains("CK_PurchaseOrders_Status", ex.Message);
    }

    [Fact]
    public async Task Column_lengths_and_nullability_follow_the_spec()
    {
        await using var context = _db.CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var order = model.FindEntityType(typeof(PurchaseOrder))!;

        void Check(string property, int? max, bool nullable)
        {
            var p = order.FindProperty(property)!;
            Assert.Equal(max, p.GetMaxLength());
            Assert.Equal(nullable, p.IsNullable);
        }

        Check(nameof(PurchaseOrder.Number), 30, false);
        Check(nameof(PurchaseOrder.Notes), 2000, true);
        Check(nameof(PurchaseOrder.ExpectedDate), null, true);
        Check(nameof(PurchaseOrder.SubmittedAt), null, true);
        Check(nameof(PurchaseOrder.ClosedAt), null, true);
        Check(nameof(PurchaseOrder.OrderDate), null, false);
    }

    [Fact]
    public async Task An_order_needs_a_real_supplier_and_creator_and_they_cannot_be_deleted_while_referenced()
    {
        await using (var context = _db.CreateContext())
        {
            var order = Order();
            order.SupplierId = 9999;
            context.PurchaseOrders.Add(order);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
        }

        await using (var context = _db.CreateContext())
        {
            var order = Order();
            order.CreatedById = "nobody";
            context.PurchaseOrders.Add(order);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using (var seed = _db.CreateContext())
        {
            seed.PurchaseOrders.Add(Order());
            await seed.SaveChangesAsync();
        }

        await using (var context = _db.CreateContext())
        {
            context.Suppliers.Remove(await context.Suppliers.SingleAsync());
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using (var context = _db.CreateContext())
        {
            context.Users.Remove(await context.Users.SingleAsync());
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_line_defaults_to_nothing_received_and_one_line_per_product_per_order()
    {
        var (orderId, _) = await SeedLineAsync();

        await using (var read = _db.CreateContext())
        {
            var line = await read.PurchaseOrderLines.SingleAsync();
            Assert.Equal(0, line.QuantityReceived);
            Assert.Equal(5, line.QuantityOrdered);
            Assert.Equal(250L, line.UnitCostCents);
        }

        await using var context = _db.CreateContext();
        context.PurchaseOrderLines.Add(new PurchaseOrderLine { PurchaseOrderId = orderId, ProductId = _productId, QuantityOrdered = 1 });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("PurchaseOrderLines.PurchaseOrderId", Message(ex));
    }

    [Theory]
    [InlineData("QuantityOrdered", 0)]
    [InlineData("QuantityOrdered", -1)]
    [InlineData("QuantityReceived", -1)]
    [InlineData("UnitCostCents", -1)]
    public async Task Invalid_line_numbers_are_rejected_by_check_constraints(string column, int value)
    {
        await using var context = _db.CreateContext();
        var order = Order();
        var line = new PurchaseOrderLine { ProductId = _productId, QuantityOrdered = 1 };
        switch (column)
        {
            case "QuantityOrdered": line.QuantityOrdered = value; break;
            case "QuantityReceived": line.QuantityReceived = value; break;
            default: line.UnitCostCents = value; break;
        }

        order.Lines.Add(line);
        context.PurchaseOrders.Add(order);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("CHECK", Message(ex), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(column, Message(ex));
    }

    [Fact]
    public async Task Boundary_line_values_are_accepted()
    {
        await using var context = _db.CreateContext();
        var order = Order();
        order.Lines.Add(new PurchaseOrderLine { ProductId = _productId, QuantityOrdered = 1, QuantityReceived = 0, UnitCostCents = 0 });
        context.PurchaseOrders.Add(order);
        Assert.Equal(2, await context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_product_on_an_order_line_cannot_be_deleted()
    {
        await SeedLineAsync();

        await using var context = _db.CreateContext();
        context.Products.Remove(await context.Products.SingleAsync());
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_draft_order_deletes_its_lines()
    {
        var (orderId, _) = await SeedLineAsync();

        await using var context = _db.CreateContext();
        context.PurchaseOrders.Remove(await context.PurchaseOrders.SingleAsync(p => p.Id == orderId));
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.PurchaseOrderLines.CountAsync());
    }

    [Fact]
    public async Task A_stock_transaction_can_reference_a_line_and_then_the_line_cannot_be_deleted()
    {
        var (orderId, lineId) = await SeedLineAsync();
        await using (var context = _db.CreateContext())
        {
            context.StockTransactions.Add(Receipt(lineId));
            await context.SaveChangesAsync();
        }

        await using (var context = _db.CreateContext())
        {
            context.PurchaseOrderLines.Remove(await context.PurchaseOrderLines.SingleAsync(l => l.Id == lineId));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
        }

        // The cascade from the order hits the same restriction.
        await using (var context = _db.CreateContext())
        {
            context.PurchaseOrders.Remove(await context.PurchaseOrders.SingleAsync(p => p.Id == orderId));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using var read = _db.CreateContext();
        var receipt = await read.StockTransactions.Include(t => t.PurchaseOrderLine).SingleAsync();
        Assert.Equal(lineId, receipt.PurchaseOrderLine!.Id);
    }

    [Fact]
    public async Task A_stock_transaction_cannot_reference_a_line_that_does_not_exist()
    {
        await using var context = _db.CreateContext();
        context.StockTransactions.Add(Receipt(9999));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_ledger_triggers_survive_the_table_rebuild_this_migration_causes()
    {
        await using var context = _db.CreateContext();
        context.StockTransactions.Add(Receipt(null));
        await context.SaveChangesAsync();

        foreach (var trigger in new[] { StockTransactionConfiguration.NoUpdateTrigger, StockTransactionConfiguration.NoDeleteTrigger })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='{trigger}'"));
        }

        var update = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("UPDATE StockTransactions SET Note = 'x'"));
        Assert.Contains(StockTransactionConfiguration.ImmutableMessage, update.Message);
        var delete = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM StockTransactions"));
        Assert.Contains(StockTransactionConfiguration.ImmutableMessage, delete.Message);
    }

    [Fact]
    public async Task Document_sequences_are_keyed_by_name_and_year()
    {
        await using var context = _db.CreateContext();
        context.DocumentSequences.AddRange(
            new DocumentSequence { Name = "PurchaseOrder", Year = 2025 },
            new DocumentSequence { Name = "PurchaseOrder", Year = 2026, NextValue = 7 },
            new DocumentSequence { Name = "Other", Year = 2026 });
        await context.SaveChangesAsync();

        Assert.Equal(7, (await context.DocumentSequences.SingleAsync(s => s.Name == "PurchaseOrder" && s.Year == 2026)).NextValue);
        Assert.Equal(1, (await context.DocumentSequences.SingleAsync(s => s.Name == "PurchaseOrder" && s.Year == 2025)).NextValue);

        await using var again = _db.CreateContext();
        again.DocumentSequences.Add(new DocumentSequence { Name = "PurchaseOrder", Year = 2026 });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
        Assert.Contains("DocumentSequences", Message(ex));
    }

    [Fact]
    public async Task A_sequence_cannot_start_below_one()
    {
        await using var context = _db.CreateContext();
        context.DocumentSequences.Add(new DocumentSequence { Name = "PurchaseOrder", Year = 2026, NextValue = 0 });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("NextValue", Message(ex));
    }

    [Fact]
    public async Task Concurrent_edits_to_an_order_are_detected()
    {
        await using (var seed = _db.CreateContext())
        {
            seed.PurchaseOrders.Add(Order());
            await seed.SaveChangesAsync();
        }

        await using var first = _db.CreateContext();
        await using var second = _db.CreateContext();
        var a = await first.PurchaseOrders.SingleAsync();
        var b = await second.PurchaseOrders.SingleAsync();

        a.Notes = "first";
        await first.SaveChangesAsync();
        Assert.Equal(2L, a.RowVersion);

        b.Notes = "second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Migration_applies_on_top_of_the_previous_schema_preserving_ledger_rows_and_triggers()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var context = new AppDbContext(Options()))
        {
            var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddPurchaseOrders", StringComparison.Ordinal));
            Assert.True(index > 0);
            await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
            Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('PurchaseOrders', 'PurchaseOrderLines', 'DocumentSequences')"));

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

            var row = await context.StockTransactions.SingleAsync();
            Assert.Equal(7, row.QuantityDelta);
            Assert.Null(row.PurchaseOrderLineId);
            Assert.Equal(7, (await context.Products.SingleAsync()).QuantityOnHand);

            // The rebuild must not have cost us the append-only guarantee.
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("UPDATE StockTransactions SET Note = 'x'"));
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM StockTransactions"));
        }
    }

    [Fact]
    public async Task The_migration_can_be_rolled_back_and_reapplied_keeping_the_triggers()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();

        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_AddPurchaseOrders", StringComparison.Ordinal));

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND (name LIKE 'PurchaseOrder%' OR name = 'DocumentSequences')"));
        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));

        await context.Database.MigrateAsync();
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_PurchaseOrders_Number'"));
        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));
    }
}
