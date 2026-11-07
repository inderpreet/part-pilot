using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Data;

public class Widget : IHasTimestamps, IHasRowVersion
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? ShippedAt { get; set; }

    public long RowVersion { get; set; }
}

/// <summary>Test-only context adding a <see cref="Widget"/> entity on top of the real model.</summary>
public class WidgetDbContext(DbContextOptions<WidgetDbContext> options) : AppDbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>();
        base.OnModelCreating(modelBuilder);
    }
}

public class InterceptorAndModelTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private readonly FakeClock _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync(_time);
        await using var context = CreateWidgetContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "Widgets" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Widgets" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "ShippedAt" TEXT NULL,
                "RowVersion" INTEGER NOT NULL
            );
            """);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private WidgetDbContext CreateWidgetContext() => new(_db.CreateOptions<WidgetDbContext>(_time));

    [Fact]
    public async Task Migration_seeds_default_settings()
    {
        await using var context = _db.CreateContext();

        var settings = await context.AppSettings.ToDictionaryAsync(s => s.Key, s => s.Value);

        Assert.Equal(SettingKeys.Defaults.Count, settings.Count);
        Assert.Equal("My Company", settings[SettingKeys.CompanyName]);
        Assert.Equal("USD", settings[SettingKeys.CurrencyCode]);
        Assert.Equal("$", settings[SettingKeys.CurrencySymbol]);
        Assert.Equal("false", settings[SettingKeys.StockAllowNegative]);
        Assert.Equal("PO-", settings[SettingKeys.PurchaseOrderNumberPrefix]);
    }

    [Fact]
    public async Task Setting_keys_are_case_insensitive_unique()
    {
        await using var context = _db.CreateContext();
        context.AppSettings.Add(new AppSetting { Key = "company.name", Value = "dup" });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Foreign_keys_are_enforced()
    {
        await using var context = _db.CreateContext();

        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "PRAGMA foreign_keys"));

        // Prove it behaviorally: a dangling FK insert must fail.
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE Parent (Id INTEGER PRIMARY KEY)");
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Child (Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL REFERENCES Parent(Id))");
        await Assert.ThrowsAnyAsync<Exception>(
            () => context.Database.ExecuteSqlRawAsync("INSERT INTO Child (ParentId) VALUES (999)"));
    }

    [Fact]
    public async Task Timestamps_are_set_on_insert_and_updated_on_modify()
    {
        var created = _time.GetUtcNow().UtcDateTime;
        await using (var context = CreateWidgetContext())
        {
            context.Widgets.Add(new Widget { Name = "a" });
            await context.SaveChangesAsync();
        }

        _time.Advance(TimeSpan.FromHours(1));

        await using (var context = CreateWidgetContext())
        {
            var widget = await context.Widgets.SingleAsync();
            Assert.Equal(created, widget.CreatedAt);
            Assert.Equal(created, widget.UpdatedAt);

            widget.Name = "b";
            await context.SaveChangesAsync();
        }

        await using (var context = CreateWidgetContext())
        {
            var widget = await context.Widgets.SingleAsync();
            Assert.Equal(created, widget.CreatedAt);
            Assert.Equal(created.AddHours(1), widget.UpdatedAt);
        }
    }

    [Fact]
    public async Task DateTimes_round_trip_as_utc()
    {
        await using (var context = CreateWidgetContext())
        {
            context.Widgets.Add(new Widget
            {
                Name = "a",
                ShippedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified),
            });
            await context.SaveChangesAsync();
        }

        await using (var context = CreateWidgetContext())
        {
            var widget = await context.Widgets.SingleAsync();
            Assert.Equal(DateTimeKind.Utc, widget.CreatedAt.Kind);
            Assert.Equal(DateTimeKind.Utc, widget.ShippedAt!.Value.Kind);
            Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7), widget.ShippedAt.Value);
        }
    }

    [Fact]
    public async Task Row_version_increments_and_detects_concurrent_updates()
    {
        await using (var context = CreateWidgetContext())
        {
            context.Widgets.Add(new Widget { Name = "a" });
            await context.SaveChangesAsync();
            Assert.Equal(1, (await context.Widgets.SingleAsync()).RowVersion);
        }

        await using var first = CreateWidgetContext();
        await using var second = CreateWidgetContext();
        var w1 = await first.Widgets.SingleAsync();
        var w2 = await second.Widgets.SingleAsync();

        w1.Name = "first";
        await first.SaveChangesAsync();
        Assert.Equal(2, w1.RowVersion);

        w2.Name = "second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }
}
