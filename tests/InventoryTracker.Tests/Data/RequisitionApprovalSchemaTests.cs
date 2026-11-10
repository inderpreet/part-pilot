using InventoryTracker.Data;
using InventoryTracker.Data.Configurations;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-12-1: the Manager role, decision and cancellation tables, permission defaults and the migration.</summary>
public sealed class RequisitionApprovalSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private string _userId = null!;
    private int _requisitionId;
    private long _lineId;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await using var context = _db.CreateContext();
        var user = new ApplicationUser { UserName = "boss", DisplayName = "Bo Ss" };
        var product = new Product { Sku = "P-1", Name = "Widget" };
        context.AddRange(user, product);
        await context.SaveChangesAsync();
        var requisition = new Requisition { Number = "REQ-2026-0001", RequestedById = user.Id };
        context.Requisitions.Add(requisition);
        await context.SaveChangesAsync();
        var line = new RequisitionLine { RequisitionId = requisition.Id, ProductId = product.Id, QuantityRequested = 5 };
        context.RequisitionLines.Add(line);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _requisitionId = requisition.Id;
        _lineId = line.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string Message(Exception ex) => (ex.InnerException ?? ex).Message;

    private RequisitionLineDecision Decision(RequisitionDecision decision, int quantity) => new()
    {
        RequisitionLineId = _lineId,
        Decision = decision,
        QuantityApproved = quantity,
        DecidedById = _userId,
        DecidedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private async Task SaveAsync(RequisitionLineDecision decision)
    {
        await using var context = _db.CreateContext();
        context.RequisitionLineDecisions.Add(decision);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Fresh_database_has_the_tables_the_role_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        foreach (var table in new[] { "RequisitionLineDecisions", "RequisitionCancellations" })
        {
            Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        }

        Assert.Equal(RoleConfiguration.ManagerId, (await context.Roles.SingleAsync(r => r.Name == Roles.Manager)).Id);
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task A_second_decision_on_a_line_is_refused()
    {
        await SaveAsync(Decision(RequisitionDecision.Approved, 5));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Decision(RequisitionDecision.Rejected, 0)));

        Assert.Contains("UNIQUE", Message(error), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RequisitionDecision.Approved, 0)]
    [InlineData(RequisitionDecision.Rejected, 1)]
    public async Task Quantity_must_fit_the_decision(RequisitionDecision decision, int quantity)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Decision(decision, quantity)));

        Assert.Contains("CK_RequisitionLineDecisions_Quantity", Message(error));
    }

    [Fact]
    public async Task An_unknown_decision_is_refused()
    {
        await SaveAsync(Decision(RequisitionDecision.Approved, 1));
        await using var context = _db.CreateContext();

        var error = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("UPDATE RequisitionLineDecisions SET Decision = 'Maybe'"));

        Assert.Contains("CK_RequisitionLineDecisions_Decision", error.Message);
    }

    [Fact]
    public async Task A_user_who_decided_cannot_be_deleted()
    {
        await SaveAsync(Decision(RequisitionDecision.Approved, 1));
        await using var context = _db.CreateContext();
        await context.Database.ExecuteSqlRawAsync("UPDATE Requisitions SET RequestedById = RequestedById");

        var error = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM AspNetUsers WHERE Id = {0}", _userId));

        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_cancellation_per_requisition()
    {
        await using var context = _db.CreateContext();
        RequisitionCancellation Cancellation() => new() { RequisitionId = _requisitionId, Reason = "Not needed", CancelledById = _userId, CancelledAt = DateTime.UtcNow };
        context.RequisitionCancellations.Add(Cancellation());
        await context.SaveChangesAsync();

        await using var second = _db.CreateContext();
        second.RequisitionCancellations.Add(Cancellation());
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        Assert.Contains("UNIQUE", Message(error), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DataPermission.ImportData, "false")]
    [InlineData(DataPermission.ImportBoms, "false")]
    [InlineData(DataPermission.ExportData, "true")]
    [InlineData(DataPermission.ExportBoms, "true")]
    public async Task Manager_permissions_default_like_Staff(DataPermission permission, string expected)
    {
        await using var context = _db.CreateContext();

        var manager = await context.AppSettings.SingleAsync(s => s.Key == SettingKeys.Permission(permission, Roles.Manager));
        var staff = await context.AppSettings.SingleAsync(s => s.Key == SettingKeys.Permission(permission, Roles.Staff));

        Assert.Equal(expected, manager.Value);
        Assert.Equal(staff.Value, manager.Value);
    }

    [Fact]
    public async Task Migration_applies_on_the_previous_schema_keeps_user_roles_and_can_be_rolled_back()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_AddRequisitionApprovals", StringComparison.Ordinal));
        Assert.True(index > 0);

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, DisplayName, IsActive, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) " +
            "VALUES ('u1', 'kept', 'KEPT', 'Kept', 1, 0, 0, 0, 0, 0)");
        await context.Database.ExecuteSqlRawAsync("INSERT INTO AspNetUserRoles (UserId, RoleId) VALUES ('u1', {0})", RoleConfiguration.StaffId);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM AspNetRoles WHERE Name = 'Manager'"));

        await context.Database.MigrateAsync();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM AspNetRoles WHERE Name = 'Manager'"));
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, $"SELECT COUNT(*) FROM AspNetUserRoles WHERE UserId = 'u1' AND RoleId = '{RoleConfiguration.StaffId}'"));
        Assert.Equal(4L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM AppSettings WHERE Key LIKE 'Permission.%.Manager'"));
        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('RequisitionLineDecisions', 'RequisitionCancellations')"));
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM AppSettings WHERE Key LIKE 'Permission.%.Manager'"));
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM AspNetUserRoles WHERE UserId = 'u1'"));

        await context.Database.MigrateAsync();
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM AspNetRoles WHERE Name = 'Manager'"));
    }
}
