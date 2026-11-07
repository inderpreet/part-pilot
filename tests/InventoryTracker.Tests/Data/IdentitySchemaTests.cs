using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

public sealed class IdentitySchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;

    public async Task InitializeAsync() => _db = await SqliteTestDb.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static ApplicationUser NewUser(string userName, string displayName = "Test User") => new()
    {
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        DisplayName = displayName,
        SecurityStamp = Guid.NewGuid().ToString(),
    };

    [Fact]
    public async Task Fresh_database_has_identity_tables_and_three_seeded_roles()
    {
        await using var context = _db.CreateContext();

        var tables = new[] { "AspNetUsers", "AspNetRoles", "AspNetUserRoles", "AspNetUserClaims", "AspNetRoleClaims", "AspNetUserLogins", "AspNetUserTokens" };
        foreach (var table in tables)
        {
            var count = await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'");
            Assert.Equal(1L, count);
        }

        var roles = await context.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        Assert.Equal(["Admin", "Staff", "Viewer"], roles.Select(r => r.Name));
        Assert.Equal(["ADMIN", "STAFF", "VIEWER"], roles.Select(r => r.NormalizedName));
        Assert.All(roles, r => Assert.False(string.IsNullOrEmpty(r.ConcurrencyStamp)));
        Assert.Equal(Roles.All.OrderBy(r => r), roles.Select(r => r.Name!));
    }

    [Fact]
    public async Task Role_seed_ids_are_the_fixed_ones()
    {
        await using var context = _db.CreateContext();
        var ids = await context.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);

        Assert.Equal(InventoryTracker.Data.Configurations.RoleConfiguration.AdminId, ids[Roles.Admin]);
        Assert.Equal(InventoryTracker.Data.Configurations.RoleConfiguration.StaffId, ids[Roles.Staff]);
        Assert.Equal(InventoryTracker.Data.Configurations.RoleConfiguration.ViewerId, ids[Roles.Viewer]);
    }

    [Fact]
    public async Task Model_has_no_pending_changes()
    {
        // Guards against editing entities/configuration without adding a migration.
        await using var context = _db.CreateContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task User_defaults_are_active_with_no_sign_in_and_round_trip_utc()
    {
        var signedIn = new DateTime(2026, 11, 7, 12, 30, 0, DateTimeKind.Utc);
        await using (var context = _db.CreateContext())
        {
            var fresh = NewUser("alice");
            Assert.True(fresh.IsActive);
            Assert.Null(fresh.LastSignInAt);

            var other = NewUser("bob");
            other.LastSignInAt = signedIn;
            context.Users.AddRange(fresh, other);
            await context.SaveChangesAsync();
        }

        await using (var context = _db.CreateContext())
        {
            var alice = await context.Users.SingleAsync(u => u.UserName == "alice");
            var bob = await context.Users.SingleAsync(u => u.UserName == "bob");
            Assert.True(alice.IsActive);
            Assert.Null(alice.LastSignInAt);
            Assert.Equal(signedIn, bob.LastSignInAt);
            Assert.Equal(DateTimeKind.Utc, bob.LastSignInAt!.Value.Kind);
        }
    }

    [Fact]
    public async Task Inactive_user_stays_inactive()
    {
        await using (var context = _db.CreateContext())
        {
            var user = NewUser("carol");
            user.IsActive = false;
            context.Users.Add(user);
            await context.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        Assert.False((await read.Users.SingleAsync(u => u.UserName == "carol")).IsActive);
    }

    [Fact]
    public async Task Display_name_is_required_and_limited_to_100_chars()
    {
        await using (var context = _db.CreateContext())
        {
            var user = NewUser("dave");
            user.DisplayName = null!;
            context.Users.Add(user);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        var property = _db.CreateContext().Model.FindEntityType(typeof(ApplicationUser))!.FindProperty(nameof(ApplicationUser.DisplayName))!;
        Assert.Equal(ApplicationUser.DisplayNameMaxLength, property.GetMaxLength());
        Assert.False(property.IsNullable);
    }

    [Fact]
    public async Task Duplicate_normalized_user_name_is_rejected()
    {
        await using var context = _db.CreateContext();
        context.Users.Add(NewUser("Eve"));
        await context.SaveChangesAsync();

        // Same name differing only in case normalises to the same value, so the unique index rejects it.
        context.Users.Add(NewUser("EVE"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task User_role_rows_cannot_reference_unknown_roles()
    {
        await using var context = _db.CreateContext();
        var user = NewUser("frank");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = "no-such-role" });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()); // foreign_keys pragma is on
    }

    [Fact]
    public async Task Migration_applies_on_top_of_the_previous_schema_and_preserves_data()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        // Upgrade only as far as the F00 schema, then put data in.
        await using (var context = new AppDbContext(Options()))
        {
            var migrator = context.GetService<IMigrator>();
            var previous = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().First(k => k.EndsWith("_Initial", StringComparison.Ordinal));
            await migrator.MigrateAsync(previous);
            Assert.False(await TableExistsAsync(context, "AspNetUsers"));

            await context.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET Value = 'Kept Co' WHERE Key = 'Company.Name'");
        }

        await using (var context = new AppDbContext(Options()))
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.True(await TableExistsAsync(context, "AspNetUsers"));
            Assert.Equal(3, await context.Roles.CountAsync());
            Assert.Equal("Kept Co", (await context.AppSettings.SingleAsync(s => s.Key == SettingKeys.CompanyName)).Value);
        }
    }

    private static async Task<bool> TableExistsAsync(DbContext context, string table)
        => Convert.ToInt64(await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'")) == 1;
}
