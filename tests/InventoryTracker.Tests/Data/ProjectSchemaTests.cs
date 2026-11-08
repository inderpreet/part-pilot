using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-10-1: project, BOM line and build tables, their constraints and the migration.</summary>
public sealed class ProjectSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private string _userId = null!;
    private int _productId;
    private int _otherProductId;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await using var context = _db.CreateContext();
        var user = new ApplicationUser { UserName = "builder", DisplayName = "Bo Builder" };
        var product = new Product { Sku = "P-1", Name = "Widget" };
        var other = new Product { Sku = "P-2", Name = "Gadget" };
        context.AddRange(user, product, other);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _productId = product.Id;
        _otherProductId = other.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string Message(Exception ex) => (ex.InnerException ?? ex).Message;

    private static Project NewProject(string name = "Robot", string number = "PRJ-2026-0001") => new() { Name = name, Number = number };

    private async Task<int> SeedProjectAsync(string name = "Robot", string number = "PRJ-2026-0001")
    {
        await using var context = _db.CreateContext();
        var project = NewProject(name, number);
        context.Projects.Add(project);
        await context.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task Fresh_database_has_the_project_tables_indexes_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        foreach (var table in new[] { "Projects", "BomLines", "ProjectBuilds" })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        }

        foreach (var index in new[]
                 {
                     "IX_Projects_Name", "IX_Projects_Number", "IX_Projects_IsActive_Name",
                     "IX_BomLines_ProjectId_ProductId", "IX_BomLines_ProductId",
                     "IX_ProjectBuilds_ProjectId_Id", "IX_ProjectBuilds_UserId",
                 })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}'"));
        }

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task A_new_project_has_defaults_timestamps_and_a_row_version()
    {
        var id = await SeedProjectAsync();

        await using var read = _db.CreateContext();
        var saved = await read.Projects.SingleAsync(p => p.Id == id);
        Assert.Equal(1, saved.TargetUnits);
        Assert.True(saved.IsActive);
        Assert.Equal(1L, saved.RowVersion);
        Assert.Null(saved.Description);
        Assert.Null(saved.Notes);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);
        Assert.Equal(saved.CreatedAt, saved.UpdatedAt);
    }

    [Fact]
    public async Task Project_names_are_unique_ignoring_case()
    {
        await SeedProjectAsync("Robot", "PRJ-2026-0001");
        await using var context = _db.CreateContext();
        context.Projects.Add(NewProject("ROBOT", "PRJ-2026-0002"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("UNIQUE", Message(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Project_numbers_are_unique_ignoring_case()
    {
        await SeedProjectAsync("Robot", "PRJ-2026-0001");
        await using var context = _db.CreateContext();
        context.Projects.Add(NewProject("Other", "prj-2026-0001"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("UNIQUE", Message(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_project_must_have_a_number()
    {
        await using var context = _db.CreateContext();

        var error = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Projects (Name, Number, TargetUnits, IsActive, RowVersion, CreatedAt, UpdatedAt) " +
            "VALUES ('No number', NULL, 1, 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')"));

        Assert.Contains("NOT NULL", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Target_units_below_one_are_rejected()
    {
        await using var context = _db.CreateContext();
        var project = NewProject();
        project.TargetUnits = 0;
        context.Projects.Add(project);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("CK_Projects_TargetUnits", Message(error));
    }

    [Fact]
    public async Task A_product_appears_once_per_project_but_can_be_in_many_projects()
    {
        var first = await SeedProjectAsync("Robot", "PRJ-2026-0001");
        var second = await SeedProjectAsync("Drone", "PRJ-2026-0002");

        await using (var context = _db.CreateContext())
        {
            context.BomLines.AddRange(
                new BomLine { ProjectId = first, ProductId = _productId, QuantityPerUnit = 2 },
                new BomLine { ProjectId = first, ProductId = _otherProductId, QuantityPerUnit = 1 },
                new BomLine { ProjectId = second, ProductId = _productId, QuantityPerUnit = 4 });
            await context.SaveChangesAsync();
        }

        await using var duplicate = _db.CreateContext();
        duplicate.BomLines.Add(new BomLine { ProjectId = first, ProductId = _productId, QuantityPerUnit = 9 });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        Assert.Contains("UNIQUE", Message(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quantity_per_unit_below_one_is_rejected()
    {
        var project = await SeedProjectAsync();
        await using var context = _db.CreateContext();
        context.BomLines.Add(new BomLine { ProjectId = project, ProductId = _productId, QuantityPerUnit = 0 });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("CK_BomLines_QuantityPerUnit", Message(error));
    }

    [Fact]
    public async Task A_line_needs_an_existing_project_and_product()
    {
        var project = await SeedProjectAsync();

        await using (var context = _db.CreateContext())
        {
            context.BomLines.Add(new BomLine { ProjectId = 9999, ProductId = _productId, QuantityPerUnit = 1 });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using (var context = _db.CreateContext())
        {
            context.BomLines.Add(new BomLine { ProjectId = project, ProductId = 9999, QuantityPerUnit = 1 });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_product_that_is_in_a_bom_cannot_be_deleted()
    {
        var project = await SeedProjectAsync();
        await using (var context = _db.CreateContext())
        {
            context.BomLines.Add(new BomLine { ProjectId = project, ProductId = _productId, QuantityPerUnit = 1 });
            await context.SaveChangesAsync();
        }

        await using var delete = _db.CreateContext();
        var error = await Assert.ThrowsAsync<SqliteException>(() => delete.Database.ExecuteSqlRawAsync("DELETE FROM Products WHERE Id = {0}", _productId));

        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_project_without_builds_removes_its_lines()
    {
        var project = await SeedProjectAsync();
        await using (var context = _db.CreateContext())
        {
            context.BomLines.AddRange(
                new BomLine { ProjectId = project, ProductId = _productId, QuantityPerUnit = 1 },
                new BomLine { ProjectId = project, ProductId = _otherProductId, QuantityPerUnit = 3 });
            await context.SaveChangesAsync();
        }

        await using (var context = _db.CreateContext())
        {
            context.Projects.Remove(await context.Projects.SingleAsync());
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        Assert.Empty(await read.BomLines.ToListAsync());
        Assert.Equal(2, await read.Products.CountAsync());
    }

    [Fact]
    public async Task A_project_with_a_build_cannot_be_deleted()
    {
        var project = await SeedProjectAsync();
        await using (var context = _db.CreateContext())
        {
            context.ProjectBuilds.Add(new ProjectBuild { ProjectId = project, Units = 2, UserId = _userId });
            await context.SaveChangesAsync();
        }

        await using var delete = _db.CreateContext();
        var error = await Assert.ThrowsAsync<SqliteException>(() => delete.Database.ExecuteSqlRawAsync("DELETE FROM Projects WHERE Id = {0}", project));

        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_build_stores_units_user_note_and_a_utc_creation_time()
    {
        var project = await SeedProjectAsync();
        await using (var context = _db.CreateContext())
        {
            context.ProjectBuilds.Add(new ProjectBuild { ProjectId = project, Units = 3, UserId = _userId, Note = "First batch" });
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        var build = await read.ProjectBuilds.Include(b => b.User).Include(b => b.Project).SingleAsync();
        Assert.Equal(3, build.Units);
        Assert.Equal("First batch", build.Note);
        Assert.Equal("Bo Builder", build.User!.DisplayName);
        Assert.Equal("Robot", build.Project!.Name);
        Assert.Equal(DateTimeKind.Utc, build.CreatedAt.Kind);
    }

    [Fact]
    public async Task Build_units_below_one_are_rejected()
    {
        var project = await SeedProjectAsync();
        await using var context = _db.CreateContext();
        context.ProjectBuilds.Add(new ProjectBuild { ProjectId = project, Units = 0, UserId = _userId });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("CK_ProjectBuilds_Units", Message(error));
    }

    [Fact]
    public async Task A_user_who_recorded_a_build_cannot_be_deleted()
    {
        var project = await SeedProjectAsync();
        await using (var context = _db.CreateContext())
        {
            context.ProjectBuilds.Add(new ProjectBuild { ProjectId = project, Units = 1, UserId = _userId });
            await context.SaveChangesAsync();
        }

        await using var delete = _db.CreateContext();
        await Assert.ThrowsAsync<SqliteException>(() => delete.Database.ExecuteSqlRawAsync("DELETE FROM AspNetUsers WHERE Id = {0}", _userId));
    }

    [Fact]
    public async Task The_stock_ledger_is_untouched_by_this_schema()
    {
        await using var context = _db.CreateContext();

        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM pragma_table_info('StockTransactions') WHERE name LIKE '%Project%'"));
    }

    [Fact]
    public async Task Migration_applies_on_top_of_the_previous_schema_keeping_existing_data_and_the_ledger_triggers()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var context = new AppDbContext(Options()))
        {
            var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddProjects", StringComparison.Ordinal));
            Assert.True(index > 0);
            await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
            Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Projects', 'BomLines', 'ProjectBuilds')"));

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

            Assert.Equal(7, (await context.Products.SingleAsync()).QuantityOnHand);
            Assert.Equal(7, (await context.StockTransactions.SingleAsync()).QuantityDelta);
            Assert.Empty(await context.Projects.ToListAsync());

            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("UPDATE StockTransactions SET Note = 'x'"));
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM StockTransactions"));
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
        var index = migrations.FindIndex(m => m.EndsWith("_AddProjects", StringComparison.Ordinal));

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Projects', 'BomLines', 'ProjectBuilds')"));

        await context.Database.MigrateAsync();
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_Projects_Number'"));
        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));
    }
}
