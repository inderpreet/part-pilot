using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.FileProviders;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-07-1: ImportBatches table, FK from stock transactions, permission setting defaults, CSV templates.</summary>
public sealed class ImportBatchSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private string _userId = null!;
    private int _productId;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await using var context = _db.CreateContext();
        var user = new ApplicationUser { UserName = "importer", DisplayName = "Importer" };
        var product = new Product { Sku = "P-1", Name = "Widget" };
        context.AddRange(user, product);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _productId = product.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string Message(Exception ex) => (ex.InnerException ?? ex).Message;

    [Fact]
    public async Task Fresh_database_has_the_table_indexes_fk_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ImportBatches'"));
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_ImportBatches_CreatedAt'"));
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_ImportBatches_CreatedById'"));
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_StockTransactions_ImportBatchId'"));

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task Permission_settings_are_seeded_with_the_documented_defaults()
    {
        await using var context = _db.CreateContext();
        var stored = await context.AppSettings.ToDictionaryAsync(s => s.Key, s => s.Value);

        foreach (var permission in Enum.GetValues<DataPermission>())
        {
            foreach (var role in DataPermissions.ConfigurableRoles)
            {
                var key = SettingKeys.Permission(permission, role);
                Assert.True(stored.ContainsKey(key), key);
                Assert.Equal(DataPermissions.DefaultAllowed(permission, role) ? "true" : "false", stored[key]);
            }
        }

        Assert.Equal("true", stored[SettingKeys.Permission(DataPermission.ExportData, Roles.Staff)]);
        Assert.Equal("true", stored[SettingKeys.Permission(DataPermission.ExportBoms, Roles.Staff)]);
        Assert.Equal("false", stored[SettingKeys.Permission(DataPermission.ImportData, Roles.Staff)]);
        Assert.Equal("false", stored[SettingKeys.Permission(DataPermission.ImportBoms, Roles.Viewer)]);
    }

    [Fact]
    public async Task A_batch_records_kind_counts_and_who_created_it()
    {
        await using var context = _db.CreateContext();
        context.ImportBatches.Add(new ImportBatch
        {
            Kind = ImportKind.Boms,
            FileName = "bom.csv",
            RowCount = 3,
            CreatedCount = 2,
            UpdatedCount = 1,
            SkippedCount = 0,
            Options = "mode=Merge;createMissingParts=false",
            CreatedById = _userId,
        });
        await context.SaveChangesAsync();

        var saved = await context.ImportBatches.AsNoTracking().SingleAsync();
        Assert.Equal(ImportKind.Boms, saved.Kind);
        Assert.Equal("bom.csv", saved.FileName);
        Assert.Equal(3, saved.RowCount);
        Assert.True(saved.CreatedAt > DateTime.MinValue);
        Assert.Equal(_userId, saved.CreatedById);
    }

    [Fact]
    public async Task An_unknown_kind_or_negative_count_is_rejected()
    {
        await using var context = _db.CreateContext();
        const string insert = """
            INSERT INTO "ImportBatches" ("Kind", "FileName", "RowCount", "CreatedCount", "UpdatedCount", "SkippedCount", "CreatedById", "CreatedAt")
            VALUES ({0}, 'x.csv', {1}, 0, 0, 0, {2}, {3})
            """;

        var kind = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlRawAsync(insert, "Nope", 0, _userId, DateTime.UtcNow));
        Assert.Contains("CHECK", Message(kind), StringComparison.OrdinalIgnoreCase);

        var count = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlRawAsync(insert, "Products", -1, _userId, DateTime.UtcNow));
        Assert.Contains("CHECK", Message(count), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restrict_stops_deleting_a_batch_that_has_stock_movements()
    {
        await using var context = _db.CreateContext();
        var batch = new ImportBatch { Kind = ImportKind.OpeningStock, FileName = "open.csv", CreatedById = _userId };
        context.ImportBatches.Add(batch);
        await context.SaveChangesAsync();

        context.StockTransactions.Add(new StockTransaction
        {
            ProductId = _productId,
            Type = StockTransactionType.Count,
            QuantityDelta = 5,
            QuantityAfter = 5,
            UserId = _userId,
            OccurredAt = DateTime.UtcNow,
            ImportBatchId = batch.Id,
        });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlRawAsync("DELETE FROM \"ImportBatches\" WHERE \"Id\" = {0}", batch.Id));
        Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restrict_stops_deleting_the_user_who_ran_an_import()
    {
        await using var context = _db.CreateContext();
        context.ImportBatches.Add(new ImportBatch { Kind = ImportKind.Products, FileName = "p.csv", CreatedById = _userId });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlRawAsync("DELETE FROM \"AspNetUsers\" WHERE \"Id\" = {0}", _userId));
        Assert.Contains("FOREIGN KEY", Message(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Templates_are_embedded_with_the_other_static_files()
    {
        var files = new ManifestEmbeddedFileProvider(typeof(AppDbContext).Assembly, "wwwroot");
        foreach (var name in new[] { "products.csv", "suppliers.csv", "product_suppliers.csv", "opening_stock.csv", "bom.csv" })
        {
            var file = files.GetFileInfo("templates/" + name);
            Assert.True(file.Exists, name);
            using var stream = file.CreateReadStream();
            using var reader = new StreamReader(stream);
            var header = reader.ReadLine();
            Assert.False(string.IsNullOrEmpty(header), name);
            Assert.Contains(',', header);
        }
    }
}
