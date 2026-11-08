using InventoryTracker.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface IDashboardService
{
    /// <summary>Everything the home page shows.</summary>
    Task<DashboardSummary> GetAsync(CancellationToken cancellationToken = default);

    Task<DashboardTiles> GetTilesAsync(CancellationToken cancellationToken = default);

    /// <summary>The products furthest below their reorder point first (after counting what is on order).</summary>
    Task<IReadOnlyList<LowStockRow>> GetLowStockAsync(int take = DashboardService.ListSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardMovement>> GetRecentMovementsAsync(int take = DashboardService.ListSize, CancellationToken cancellationToken = default);

    /// <summary>Open orders whose expected date is today or within the next 7 days, soonest first.</summary>
    Task<IReadOnlyList<DashboardDueOrder>> GetDueThisWeekAsync(int take = DashboardService.ListSize, CancellationToken cancellationToken = default);
}

/// <summary>Dashboard figures (F06 FR-1, FR-2). The counts are always computed live; only the nav badge is cached.</summary>
public sealed class DashboardService(AppDbContext db, IClock clock) : IDashboardService
{
    public const int ListSize = 10;
    public const int DueWithinDays = 7;

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());

    public async Task<DashboardSummary> GetAsync(CancellationToken cancellationToken = default)
    {
        // One DbContext cannot run queries side by side, so these go one after another (each takes a few milliseconds).
        var tiles = await GetTilesAsync(cancellationToken);
        var lowStock = await GetLowStockAsync(ListSize, cancellationToken);
        var movements = await GetRecentMovementsAsync(ListSize, cancellationToken);
        var due = await GetDueThisWeekAsync(ListSize, cancellationToken);
        return new DashboardSummary(tiles, lowStock, movements, due);
    }

    public async Task<DashboardTiles> GetTilesAsync(CancellationToken cancellationToken = default)
    {
        var today = Today;
        var open = ReportingQueries.OpenStatuses;

        var active = db.Products.AsNoTracking().Where(p => p.IsActive);
        var activeSkus = await active.CountAsync(cancellationToken);
        var value = await active.SumAsync(p => (long)p.QuantityOnHand * p.UnitCostCents, cancellationToken);
        var outOfStock = await active.CountAsync(p => p.QuantityOnHand <= 0, cancellationToken);
        var lowStock = await db.LowStock().CountAsync(cancellationToken);

        var openOrders = db.PurchaseOrders.AsNoTracking().Where(p => open.Contains(p.Status));
        var openCount = await openOrders.CountAsync(cancellationToken);
        var openValue = await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => open.Contains(l.PurchaseOrder!.Status))
            .SumAsync(l => (long)l.QuantityOrdered * l.UnitCostCents, cancellationToken);
        var overdue = await openOrders.CountAsync(p => p.ExpectedDate < today, cancellationToken);

        return new DashboardTiles(activeSkus, value, lowStock, outOfStock, openCount, openValue, overdue);
    }

    public async Task<IReadOnlyList<LowStockRow>> GetLowStockAsync(int take = ListSize, CancellationToken cancellationToken = default)
        => await db.LowStock()
            .OrderBy(r => r.OnHand + r.OnOrder - r.ReorderPoint).ThenBy(r => r.Name).ThenBy(r => r.ProductId)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DashboardMovement>> GetRecentMovementsAsync(int take = ListSize, CancellationToken cancellationToken = default)
        => await db.StockTransactions.AsNoTracking()
            .OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id)
            .Take(Math.Clamp(take, 1, 100))
            .Select(t => new DashboardMovement(t.Id, t.ProductId, t.Product!.Sku, t.Product.Name, t.Type, t.QuantityDelta, t.User!.DisplayName, t.OccurredAt))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DashboardDueOrder>> GetDueThisWeekAsync(int take = ListSize, CancellationToken cancellationToken = default)
    {
        var today = Today;
        var end = today.AddDays(DueWithinDays);
        var open = ReportingQueries.OpenStatuses;

        return await db.PurchaseOrders.AsNoTracking()
            .Where(p => open.Contains(p.Status) && p.ExpectedDate >= today && p.ExpectedDate <= end)
            .OrderBy(p => p.ExpectedDate).ThenBy(p => p.Number)
            .Take(Math.Clamp(take, 1, 100))
            .Select(p => new DashboardDueOrder(
                p.Id, p.Number, p.SupplierId, p.Supplier!.Name, p.Status, p.ExpectedDate!.Value,
                p.Lines.Where(l => l.QuantityOrdered > l.QuantityReceived).Sum(l => (long)(l.QuantityOrdered - l.QuantityReceived) * l.UnitCostCents)))
            .ToListAsync(cancellationToken);
    }
}
