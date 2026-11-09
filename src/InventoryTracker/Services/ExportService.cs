using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public sealed record BomExportQuery(int? ProjectId = null, bool IncludeArchived = false);

public sealed record BomExportOption(int Id, string Number, string Name, bool IsActive);

public interface IExportService
{
    Task WriteProductsAsync(Stream destination, CancellationToken cancellationToken = default);

    Task WriteSuppliersAsync(Stream destination, CancellationToken cancellationToken = default);

    Task WriteProductSuppliersAsync(Stream destination, CancellationToken cancellationToken = default);

    Task WriteTransactionsAsync(Stream destination, DateTime? fromUtc, DateTime? toUtcExclusive, CancellationToken cancellationToken = default);

    Task WriteBomsAsync(Stream destination, BomExportQuery query, CancellationToken cancellationToken = default);

    /// <summary>Every project, active and archived, for the export picker.</summary>
    Task<IReadOnlyList<BomExportOption>> ListBomProjectsAsync(CancellationToken cancellationToken = default);
}

public sealed class ExportService(AppDbContext db) : IExportService
{
    public Task WriteProductsAsync(Stream destination, CancellationToken cancellationToken = default)
        => CsvExport.WriteAsync(
            destination,
            db.Products.AsNoTracking().OrderBy(p => p.Sku).ThenBy(p => p.Id).Select(p => new ProductExportRow(
                p.Sku, p.Name, p.Description, p.Category == null ? null : p.Category.Name, p.Unit, p.Barcode,
                p.BinLocation, p.ReorderPoint, p.ReorderQuantity, p.UnitCostCents, p.IsActive, p.QuantityOnHand,
                p.CreatedAt, p.UpdatedAt)).AsAsyncEnumerable(),
            [
                CsvColumn.Text<ProductExportRow>("sku", r => r.Sku),
                CsvColumn.Text<ProductExportRow>("name", r => r.Name),
                CsvColumn.Text<ProductExportRow>("description", r => r.Description),
                CsvColumn.Text<ProductExportRow>("category", r => r.Category),
                CsvColumn.Text<ProductExportRow>("unit", r => r.Unit),
                CsvColumn.Text<ProductExportRow>("barcode", r => r.Barcode),
                CsvColumn.Text<ProductExportRow>("bin_location", r => r.Bin),
                CsvColumn.Int<ProductExportRow>("reorder_point", r => r.ReorderPoint),
                CsvColumn.Int<ProductExportRow>("reorder_quantity", r => r.ReorderQuantity),
                CsvColumn.Money<ProductExportRow>("unit_cost", r => r.UnitCostCents),
                CsvColumn.Text<ProductExportRow>("active", r => r.IsActive ? "true" : "false"),
                CsvColumn.Int<ProductExportRow>("quantity_on_hand", r => r.OnHand),
                CsvColumn.Utc<ProductExportRow>("created_at", r => r.CreatedAt),
                CsvColumn.Utc<ProductExportRow>("updated_at", r => r.UpdatedAt),
            ],
            cancellationToken);

    public Task WriteSuppliersAsync(Stream destination, CancellationToken cancellationToken = default)
        => CsvExport.WriteAsync(
            destination,
            db.Suppliers.AsNoTracking().OrderBy(s => s.Name).ThenBy(s => s.Id).Select(s => new SupplierExportRow(
                s.Name, s.ContactName, s.Email, s.Phone, s.Address, s.Website, s.Notes, s.IsActive)).AsAsyncEnumerable(),
            [
                CsvColumn.Text<SupplierExportRow>("name", r => r.Name),
                CsvColumn.Text<SupplierExportRow>("contact_name", r => r.ContactName),
                CsvColumn.Text<SupplierExportRow>("email", r => r.Email),
                CsvColumn.Text<SupplierExportRow>("phone", r => r.Phone),
                CsvColumn.Text<SupplierExportRow>("address", r => r.Address),
                CsvColumn.Text<SupplierExportRow>("website", r => r.Website),
                CsvColumn.Text<SupplierExportRow>("notes", r => r.Notes),
                CsvColumn.Text<SupplierExportRow>("active", r => r.IsActive ? "true" : "false"),
            ],
            cancellationToken);

    public Task WriteProductSuppliersAsync(Stream destination, CancellationToken cancellationToken = default)
        => CsvExport.WriteAsync(
            destination,
            db.ProductSuppliers.AsNoTracking()
                .OrderBy(l => l.Product!.Sku).ThenBy(l => l.Supplier!.Name)
                .Select(l => new LinkExportRow(
                    l.Product!.Sku, l.Supplier!.Name, l.SupplierSku, l.UnitCostCents, l.LeadTimeDays,
                    l.MinOrderQuantity, l.IsPreferred)).AsAsyncEnumerable(),
            [
                CsvColumn.Text<LinkExportRow>("sku", r => r.Sku),
                CsvColumn.Text<LinkExportRow>("supplier", r => r.Supplier),
                CsvColumn.Text<LinkExportRow>("supplier_sku", r => r.SupplierSku),
                CsvColumn.Money<LinkExportRow>("unit_cost", r => r.UnitCostCents),
                CsvColumn.Int<LinkExportRow>("lead_time_days", r => r.LeadTimeDays),
                CsvColumn.Int<LinkExportRow>("min_order_quantity", r => r.MinOrder),
                CsvColumn.Text<LinkExportRow>("preferred", r => r.Preferred ? "true" : "false"),
            ],
            cancellationToken);

    public async Task<IReadOnlyList<BomExportOption>> ListBomProjectsAsync(CancellationToken cancellationToken = default)
        => await db.Projects.AsNoTracking()
            .OrderByDescending(p => p.IsActive).ThenBy(p => p.Number).ThenBy(p => p.Id)
            .Select(p => new BomExportOption(p.Id, p.Number, p.Name, p.IsActive))
            .ToListAsync(cancellationToken);

    public Task WriteTransactionsAsync(Stream destination, DateTime? fromUtc, DateTime? toUtcExclusive, CancellationToken cancellationToken = default)
    {
        var rows = db.StockTransactions.AsNoTracking().AsQueryable();
        if (fromUtc is { } from)
        {
            rows = rows.Where(t => t.OccurredAt >= from);
        }

        if (toUtcExclusive is { } to)
        {
            rows = rows.Where(t => t.OccurredAt < to);
        }

        return CsvExport.WriteAsync(
            destination,
            rows.OrderBy(t => t.OccurredAt).ThenBy(t => t.Id).Select(t => new TxExportRow(
                t.OccurredAt, t.Product!.Sku, t.Product.Name, t.Type.ToString(), t.QuantityDelta, t.QuantityAfter,
                t.UnitCostCents, t.Reason, t.Reference,
                t.PurchaseOrderLine == null ? null : t.PurchaseOrderLine.PurchaseOrder!.Number,
                t.User!.DisplayName)).AsAsyncEnumerable(),
            [
                CsvColumn.Utc<TxExportRow>("occurred_at", r => r.OccurredAt),
                CsvColumn.Text<TxExportRow>("sku", r => r.Sku),
                CsvColumn.Text<TxExportRow>("product_name", r => r.Name),
                CsvColumn.Text<TxExportRow>("type", r => r.Type),
                CsvColumn.Int<TxExportRow>("quantity_delta", r => r.Delta),
                CsvColumn.Int<TxExportRow>("quantity_after", r => r.After),
                CsvColumn.Money<TxExportRow>("unit_cost", r => r.UnitCostCents),
                CsvColumn.Text<TxExportRow>("reason", r => r.Reason),
                CsvColumn.Text<TxExportRow>("reference", r => r.Reference),
                CsvColumn.Text<TxExportRow>("po_number", r => r.PoNumber),
                CsvColumn.Text<TxExportRow>("user", r => r.User),
            ],
            cancellationToken);
    }

    public async Task WriteBomsAsync(Stream destination, BomExportQuery query, CancellationToken cancellationToken = default)
    {
        var projects = db.Projects.AsNoTracking().Include(p => p.Lines).ThenInclude(l => l.Product).AsQueryable();
        if (query.ProjectId is { } id)
        {
            projects = projects.Where(p => p.Id == id);
        }
        else if (!query.IncludeArchived)
        {
            projects = projects.Where(p => p.IsActive);
        }

        var list = await projects.OrderBy(p => p.Number).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        var rows = new List<BomExportRow>();
        foreach (var project in list)
        {
            var lines = project.Lines.OrderBy(l => l.Product!.Sku, StringComparer.OrdinalIgnoreCase).ToList();
            if (lines.Count == 0)
            {
                rows.Add(new BomExportRow(project.Number, project.Name, project.TargetUnits, project.Description, project.Notes,
                    null, null, null, null, null, null));
                continue;
            }

            foreach (var line in lines)
            {
                var part = line.Product!;
                rows.Add(new BomExportRow(project.Number, project.Name, project.TargetUnits, project.Description, project.Notes,
                    part.Sku, line.QuantityPerUnit, line.Note, part.Name, part.Unit, part.UnitCostCents));
            }
        }

        await CsvExport.WriteAsync(
            destination,
            rows,
            [
                CsvColumn.Text<BomExportRow>("project_number", r => r.Number),
                CsvColumn.Text<BomExportRow>("project_name", r => r.Name),
                CsvColumn.Int<BomExportRow>("target_units", r => r.Target),
                CsvColumn.Text<BomExportRow>("description", r => r.Description),
                CsvColumn.Text<BomExportRow>("notes", r => r.Notes),
                CsvColumn.Text<BomExportRow>("sku", r => r.Sku),
                CsvColumn.Int<BomExportRow>("quantity_per_unit", r => r.Qty),
                CsvColumn.Text<BomExportRow>("note", r => r.Note),
                CsvColumn.Text<BomExportRow>("part_name", r => r.PartName),
                CsvColumn.Text<BomExportRow>("part_unit", r => r.PartUnit),
                CsvColumn.Money<BomExportRow>("part_unit_cost", r => r.PartCost),
            ],
            cancellationToken);
    }

    private sealed record ProductExportRow(string Sku, string Name, string? Description, string? Category, string Unit, string? Barcode, string? Bin, int ReorderPoint, int ReorderQuantity, long UnitCostCents, bool IsActive, int OnHand, DateTime CreatedAt, DateTime UpdatedAt);

    private sealed record SupplierExportRow(string Name, string? ContactName, string? Email, string? Phone, string? Address, string? Website, string? Notes, bool IsActive);

    private sealed record LinkExportRow(string Sku, string Supplier, string? SupplierSku, long UnitCostCents, int? LeadTimeDays, int MinOrder, bool Preferred);

    private sealed record TxExportRow(DateTime OccurredAt, string Sku, string Name, string Type, int Delta, int After, long? UnitCostCents, string? Reason, string? Reference, string? PoNumber, string User);

    private sealed record BomExportRow(string Number, string Name, int Target, string? Description, string? Notes, string? Sku, int? Qty, string? Note, string? PartName, string? PartUnit, long? PartCost);
}
