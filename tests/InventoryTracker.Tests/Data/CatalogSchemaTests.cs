using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

public sealed class CatalogSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;

    public async Task InitializeAsync() => _db = await SqliteTestDb.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static Product NewProduct(string sku, string? barcode = null, string name = "Widget") => new()
    {
        Sku = sku,
        Name = name,
        Barcode = barcode,
    };

    private static string Message(DbUpdateException ex) => ex.InnerException?.Message ?? ex.Message;

    [Fact]
    public async Task Fresh_database_has_catalog_tables_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        foreach (var table in new[] { "Categories", "Products" })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        }

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task Indexes_exist_for_the_documented_access_paths()
    {
        await using var context = _db.CreateContext();

        foreach (var index in new[] { "IX_Products_Sku", "IX_Products_Barcode", "IX_Products_Name", "IX_Products_CategoryId", "IX_Products_IsActive_Name", "IX_Categories_Name" })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}'"));
        }
    }

    [Fact]
    public async Task New_product_gets_defaults_row_version_and_utc_timestamps()
    {
        await using (var context = _db.CreateContext())
        {
            context.Products.Add(NewProduct("SKU-1"));
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        var product = await read.Products.SingleAsync();
        Assert.Equal("each", product.Unit);
        Assert.Equal(0, product.QuantityOnHand);
        Assert.Equal(0, product.ReorderPoint);
        Assert.Equal(0, product.ReorderQuantity);
        Assert.Equal(0L, product.UnitCostCents);
        Assert.True(product.IsActive);
        Assert.Null(product.CategoryId);
        Assert.Equal(1L, product.RowVersion);
        Assert.Equal(DateTimeKind.Utc, product.CreatedAt.Kind);
        Assert.True(product.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task An_archived_product_stays_archived()
    {
        await using (var context = _db.CreateContext())
        {
            var product = NewProduct("OLD-1");
            product.IsActive = false;
            context.Products.Add(product);
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        Assert.False((await read.Products.SingleAsync()).IsActive);
    }

    [Theory]
    [InlineData("ABC-1", "abc-1")]
    [InlineData("Widget", "WIDGET")]
    public async Task Duplicate_sku_differing_only_by_case_is_rejected(string first, string second)
    {
        await using var context = _db.CreateContext();
        context.Products.Add(NewProduct(first));
        await context.SaveChangesAsync();

        context.Products.Add(NewProduct(second));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("UNIQUE", Message(ex), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Products.Sku", Message(ex));
    }

    [Fact]
    public async Task Sku_stays_unique_across_archived_products()
    {
        await using var context = _db.CreateContext();
        var archived = NewProduct("DUP-1");
        archived.IsActive = false;
        context.Products.Add(archived);
        await context.SaveChangesAsync();

        context.Products.Add(NewProduct("dup-1"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Many_products_may_have_no_barcode()
    {
        await using (var context = _db.CreateContext())
        {
            context.Products.AddRange(NewProduct("A"), NewProduct("B"), NewProduct("C"));
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        Assert.Equal(3, await read.Products.CountAsync(p => p.Barcode == null));
    }

    [Fact]
    public async Task Duplicate_barcode_is_rejected_case_insensitively_even_for_archived_products()
    {
        await using var context = _db.CreateContext();
        var archived = NewProduct("A", barcode: "ab12cd");
        archived.IsActive = false;
        context.Products.Add(archived);
        await context.SaveChangesAsync();

        context.Products.Add(NewProduct("B", barcode: "AB12CD"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("Products.Barcode", Message(ex));
    }

    [Theory]
    [InlineData("ReorderPoint")]
    [InlineData("ReorderQuantity")]
    [InlineData("UnitCostCents")]
    public async Task Negative_values_are_rejected_by_check_constraints(string column)
    {
        await using var context = _db.CreateContext();
        var product = NewProduct("NEG-1");
        switch (column)
        {
            case "ReorderPoint": product.ReorderPoint = -1; break;
            case "ReorderQuantity": product.ReorderQuantity = -1; break;
            default: product.UnitCostCents = -1; break;
        }

        context.Products.Add(product);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("CHECK", Message(ex), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(column, Message(ex));
    }

    [Fact]
    public async Task Zero_values_are_accepted_by_the_check_constraints()
    {
        await using var context = _db.CreateContext();
        context.Products.Add(NewProduct("ZERO-1"));
        Assert.Equal(1, await context.SaveChangesAsync());
    }

    [Fact]
    public async Task Required_columns_and_lengths_are_enforced_in_the_model()
    {
        await using var context = _db.CreateContext();
        // Collation lives in the design-time (relational) model, not the runtime read-optimised one.
        var model = context.GetService<IDesignTimeModel>().Model;
        var product = model.FindEntityType(typeof(Product))!;
        var category = model.FindEntityType(typeof(Category))!;

        void Check(IEntityType type, string property, int max, bool nullable)
        {
            var p = type.FindProperty(property)!;
            Assert.Equal(max, p.GetMaxLength());
            Assert.Equal(nullable, p.IsNullable);
        }

        Check(product, nameof(Product.Sku), 40, false);
        Check(product, nameof(Product.Name), 200, false);
        Check(product, nameof(Product.Description), 2000, true);
        Check(product, nameof(Product.Unit), 20, false);
        Check(product, nameof(Product.Barcode), 64, true);
        Check(product, nameof(Product.BinLocation), 50, true);
        Check(category, nameof(Category.Name), 100, false);
        Check(category, nameof(Category.Description), 500, true);

        Assert.Equal("NOCASE", product.FindProperty(nameof(Product.Sku))!.GetCollation());
        Assert.Equal("NOCASE", product.FindProperty(nameof(Product.Barcode))!.GetCollation());
        Assert.Equal("NOCASE", category.FindProperty(nameof(Category.Name))!.GetCollation());

        // And the database refuses a missing SKU outright.
        context.Products.Add(new Product { Sku = null!, Name = "x" });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Product_name_sorts_case_insensitively()
    {
        await using (var context = _db.CreateContext())
        {
            context.Products.AddRange(NewProduct("1", name: "banana"), NewProduct("2", name: "Apple"), NewProduct("3", name: "cherry"));
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        Assert.Equal(["Apple", "banana", "cherry"], await read.Products.OrderBy(p => p.Name).Select(p => p.Name).ToListAsync());
    }

    [Fact]
    public async Task Category_name_is_unique_case_insensitively()
    {
        await using var context = _db.CreateContext();
        context.Categories.Add(new Category { Name = "Hardware" });
        await context.SaveChangesAsync();

        context.Categories.Add(new Category { Name = "HARDWARE" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("Categories.Name", Message(ex));
    }

    [Fact]
    public async Task New_category_is_active_with_timestamps()
    {
        await using (var context = _db.CreateContext())
        {
            context.Categories.Add(new Category { Name = "Tools", Description = "Hand tools" });
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        var category = await read.Categories.SingleAsync();
        Assert.True(category.IsActive);
        Assert.Equal("Hand tools", category.Description);
        Assert.Equal(DateTimeKind.Utc, category.UpdatedAt.Kind);
    }

    [Fact]
    public async Task A_category_that_has_products_cannot_be_deleted()
    {
        int categoryId;
        await using (var seed = _db.CreateContext())
        {
            var category = new Category { Name = "Tools" };
            var product = NewProduct("T-1");
            product.Category = category;
            seed.Products.Add(product);
            await seed.SaveChangesAsync();
            categoryId = category.Id;
        }

        // A fresh context, as a page request would have: the product is not tracked, so only the DB can object.
        await using var context = _db.CreateContext();
        context.Categories.Remove(await context.Categories.SingleAsync(c => c.Id == categoryId));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_product_cannot_reference_an_unknown_category()
    {
        await using var context = _db.CreateContext();
        var product = NewProduct("X-1");
        product.CategoryId = 9999;
        context.Products.Add(product);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_edits_to_a_product_are_detected()
    {
        await using (var seed = _db.CreateContext())
        {
            seed.Products.Add(NewProduct("C-1"));
            await seed.SaveChangesAsync();
        }

        await using var first = _db.CreateContext();
        await using var second = _db.CreateContext();
        var a = await first.Products.SingleAsync();
        var b = await second.Products.SingleAsync();

        a.Name = "Edited by A";
        await first.SaveChangesAsync();
        Assert.Equal(2L, a.RowVersion);

        b.Name = "Edited by B";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Migration_applies_on_top_of_the_previous_schema_and_preserves_data()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        // Upgrade only as far as the F01 schema (the migration just before AddCatalog), then put data in.
        await using (var context = new AppDbContext(Options()))
        {
            var migrator = context.GetService<IMigrator>();
            var previous = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().First(k => k.EndsWith("_AddIdentity", StringComparison.Ordinal));
            await migrator.MigrateAsync(previous);
            Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name='Products'"));

            await context.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET Value = 'Kept Co' WHERE Key = 'Company.Name'");
        }

        await using (var context = new AppDbContext(Options()))
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name='Products'"));
            Assert.Equal(Roles.All.Count, await context.Roles.CountAsync());
            Assert.Equal("Kept Co", (await context.AppSettings.SingleAsync(s => s.Key == SettingKeys.CompanyName)).Value);
        }
    }
}
