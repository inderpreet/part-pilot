using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-04-1: Supplier and ProductSupplier tables, constraints and the migration.</summary>
public sealed class SupplierSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;

    public async Task InitializeAsync() => _db = await SqliteTestDb.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string Message(DbUpdateException ex) => ex.InnerException?.Message ?? ex.Message;

    private async Task<(int ProductId, int SupplierId)> SeedAsync(string sku = "P-1", string supplier = "Acme")
    {
        await using var context = _db.CreateContext();
        var product = new Product { Sku = sku, Name = "Product " + sku };
        var s = new Supplier { Name = supplier };
        context.AddRange(product, s);
        await context.SaveChangesAsync();
        return (product.Id, s.Id);
    }

    private async Task<int> NewSupplierAsync(string name)
    {
        await using var context = _db.CreateContext();
        var s = new Supplier { Name = name };
        context.Suppliers.Add(s);
        await context.SaveChangesAsync();
        return s.Id;
    }

    [Fact]
    public async Task Fresh_database_has_the_supplier_tables_indexes_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        foreach (var table in new[] { "Suppliers", "ProductSuppliers" })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        }

        foreach (var index in new[] { "IX_Suppliers_Name", "IX_Suppliers_IsActive_Name", "IX_ProductSuppliers_SupplierId", "UX_ProductSuppliers_Preferred" })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}'"));
        }

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task A_new_supplier_is_active_with_a_row_version_and_utc_timestamps()
    {
        var id = await NewSupplierAsync("Acme");

        await using var read = _db.CreateContext();
        var supplier = await read.Suppliers.SingleAsync(s => s.Id == id);
        Assert.True(supplier.IsActive);
        Assert.Equal(1L, supplier.RowVersion);
        Assert.Equal(DateTimeKind.Utc, supplier.CreatedAt.Kind);
        Assert.True(supplier.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Null(supplier.Email);
    }

    [Theory]
    [InlineData("Acme", "acme")]
    [InlineData("Globex", "GLOBEX")]
    public async Task Supplier_names_are_unique_case_insensitively_including_archived_ones(string first, string second)
    {
        await using var context = _db.CreateContext();
        context.Suppliers.Add(new Supplier { Name = first, IsActive = false });
        await context.SaveChangesAsync();

        context.Suppliers.Add(new Supplier { Name = second });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("Suppliers.Name", Message(ex));
    }

    [Fact]
    public async Task Supplier_names_sort_case_insensitively()
    {
        foreach (var name in new[] { "banana co", "Apple Ltd", "cherry inc" })
        {
            await NewSupplierAsync(name);
        }

        await using var read = _db.CreateContext();
        Assert.Equal(["Apple Ltd", "banana co", "cherry inc"], await read.Suppliers.OrderBy(s => s.Name).Select(s => s.Name).ToListAsync());
    }

    [Fact]
    public async Task Column_lengths_and_nullability_follow_the_spec()
    {
        await using var context = _db.CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var supplier = model.FindEntityType(typeof(Supplier))!;
        var link = model.FindEntityType(typeof(ProductSupplier))!;

        void Check(IEntityType type, string property, int max, bool nullable)
        {
            var p = type.FindProperty(property)!;
            Assert.Equal(max, p.GetMaxLength());
            Assert.Equal(nullable, p.IsNullable);
        }

        Check(supplier, nameof(Supplier.Name), 200, false);
        Check(supplier, nameof(Supplier.ContactName), 100, true);
        Check(supplier, nameof(Supplier.Email), 200, true);
        Check(supplier, nameof(Supplier.Phone), 50, true);
        Check(supplier, nameof(Supplier.Address), 500, true);
        Check(supplier, nameof(Supplier.Website), 200, true);
        Check(supplier, nameof(Supplier.Notes), 2000, true);
        Check(link, nameof(ProductSupplier.SupplierSku), 64, true);
        Assert.Equal("NOCASE", supplier.FindProperty(nameof(Supplier.Name))!.GetCollation());
    }

    [Fact]
    public async Task A_link_has_defaults_and_a_composite_key_so_a_pair_is_unique()
    {
        var (productId, supplierId) = await SeedAsync();
        await using (var context = _db.CreateContext())
        {
            context.ProductSuppliers.Add(new ProductSupplier { ProductId = productId, SupplierId = supplierId });
            await context.SaveChangesAsync();
        }

        await using (var read = _db.CreateContext())
        {
            var link = await read.ProductSuppliers.SingleAsync();
            Assert.Equal(1, link.MinOrderQuantity);
            Assert.False(link.IsPreferred);
            Assert.Null(link.LeadTimeDays);
            Assert.Null(link.SupplierSku);
            Assert.Equal(0L, link.UnitCostCents);
            Assert.Equal(DateTimeKind.Utc, link.UpdatedAt.Kind);
        }

        // Same pair again from a fresh context: SQLite rejects the duplicate primary key.
        await using var again = _db.CreateContext();
        again.ProductSuppliers.Add(new ProductSupplier { ProductId = productId, SupplierId = supplierId });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
        Assert.Contains("ProductSuppliers", Message(ex));
    }

    [Fact]
    public async Task A_second_preferred_supplier_for_the_same_product_violates_the_filtered_unique_index()
    {
        var (productId, first) = await SeedAsync();
        var second = await NewSupplierAsync("Globex");

        await using var context = _db.CreateContext();
        context.ProductSuppliers.Add(new ProductSupplier { ProductId = productId, SupplierId = first, IsPreferred = true });
        await context.SaveChangesAsync();

        context.ProductSuppliers.Add(new ProductSupplier { ProductId = productId, SupplierId = second, IsPreferred = true });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("ProductSuppliers.ProductId", Message(ex));
    }

    [Fact]
    public async Task Non_preferred_links_are_not_limited_and_preferred_may_differ_per_product()
    {
        var (product1, supplier1) = await SeedAsync("P-1", "Acme");
        var supplier2 = await NewSupplierAsync("Globex");
        int product2;
        await using (var seed = _db.CreateContext())
        {
            var p = new Product { Sku = "P-2", Name = "Two" };
            seed.Products.Add(p);
            await seed.SaveChangesAsync();
            product2 = p.Id;
        }

        await using var context = _db.CreateContext();
        context.ProductSuppliers.AddRange(
            new ProductSupplier { ProductId = product1, SupplierId = supplier1 },
            new ProductSupplier { ProductId = product1, SupplierId = supplier2 },
            new ProductSupplier { ProductId = product2, SupplierId = supplier1, IsPreferred = true },
            new ProductSupplier { ProductId = product2, SupplierId = supplier2 });
        Assert.Equal(4, await context.SaveChangesAsync());

        // Preferred for product 1 is still free to be set once.
        var link = await context.ProductSuppliers.SingleAsync(l => l.ProductId == product1 && l.SupplierId == supplier2);
        link.IsPreferred = true;
        Assert.Equal(1, await context.SaveChangesAsync());
    }

    [Theory]
    [InlineData("UnitCostCents")]
    [InlineData("LeadTimeDays")]
    [InlineData("MinOrderQuantity")]
    public async Task Invalid_numbers_are_rejected_by_check_constraints(string column)
    {
        var (productId, supplierId) = await SeedAsync();
        await using var context = _db.CreateContext();
        var link = new ProductSupplier { ProductId = productId, SupplierId = supplierId };
        switch (column)
        {
            case "UnitCostCents": link.UnitCostCents = -1; break;
            case "LeadTimeDays": link.LeadTimeDays = -1; break;
            default: link.MinOrderQuantity = 0; break;
        }

        context.ProductSuppliers.Add(link);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("CHECK", Message(ex), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(column, Message(ex));
    }

    [Fact]
    public async Task Boundary_values_are_accepted()
    {
        var (productId, supplierId) = await SeedAsync();
        await using var context = _db.CreateContext();
        context.ProductSuppliers.Add(new ProductSupplier { ProductId = productId, SupplierId = supplierId, LeadTimeDays = 0, MinOrderQuantity = 1, UnitCostCents = 0 });
        Assert.Equal(1, await context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_link_must_reference_a_real_product_and_supplier()
    {
        var (productId, supplierId) = await SeedAsync();

        await using (var context = _db.CreateContext())
        {
            context.ProductSuppliers.Add(new ProductSupplier { ProductId = 9999, SupplierId = supplierId });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
        }

        await using (var context = _db.CreateContext())
        {
            context.ProductSuppliers.Add(new ProductSupplier { ProductId = productId, SupplierId = 9999 });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_supplier_or_product_with_links_cannot_be_deleted()
    {
        var (productId, supplierId) = await SeedAsync();
        await using (var seed = _db.CreateContext())
        {
            seed.ProductSuppliers.Add(new ProductSupplier { ProductId = productId, SupplierId = supplierId });
            await seed.SaveChangesAsync();
        }

        await using (var context = _db.CreateContext())
        {
            context.Suppliers.Remove(await context.Suppliers.SingleAsync());
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
        }

        await using (var context = _db.CreateContext())
        {
            context.Products.Remove(await context.Products.SingleAsync());
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Concurrent_edits_to_a_supplier_are_detected()
    {
        var id = await NewSupplierAsync("Acme");

        await using var first = _db.CreateContext();
        await using var second = _db.CreateContext();
        var a = await first.Suppliers.SingleAsync(s => s.Id == id);
        var b = await second.Suppliers.SingleAsync(s => s.Id == id);

        a.Phone = "111";
        await first.SaveChangesAsync();
        Assert.Equal(2L, a.RowVersion);

        b.Phone = "222";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Migration_applies_on_top_of_the_previous_schema_and_preserves_data()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var context = new AppDbContext(Options()))
        {
            var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
            var suppliers = migrations.FindIndex(m => m.EndsWith("_AddSuppliers", StringComparison.Ordinal));
            Assert.True(suppliers > 0);
            await context.GetService<IMigrator>().MigrateAsync(migrations[suppliers - 1]);
            Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Suppliers', 'ProductSuppliers')"));

            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO Products (Sku, Name, Unit, ReorderPoint, ReorderQuantity, UnitCostCents, QuantityOnHand, IsActive, RowVersion, CreatedAt, UpdatedAt) " +
                "VALUES ('KEEP-1', 'Kept', 'each', 0, 0, 0, 7, 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        }

        await using (var context = new AppDbContext(Options()))
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Suppliers', 'ProductSuppliers')"));

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

        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();

        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
        var suppliers = migrations.FindIndex(m => m.EndsWith("_AddSuppliers", StringComparison.Ordinal));

        await context.GetService<IMigrator>().MigrateAsync(migrations[suppliers - 1]);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%Suppliers%'"));

        await context.Database.MigrateAsync();
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'UX_ProductSuppliers_Preferred'"));
    }
}
