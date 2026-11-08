using InventoryTracker.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface IReorderService
{
    /// <summary>Low-stock products (on hand + on order at or below the reorder point) with a suggested quantity, one page.</summary>
    Task<PagedResult<ReorderSuggestion>> GetSuggestionsAsync(ReorderFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the chosen quantities on Draft orders, one new draft per preferred supplier, via
    /// <see cref="IPurchaseOrderService.CreateDraftAsync"/> (lines take the supplier link's price). Items that cannot be ordered are
    /// reported, not thrown: no preferred supplier, an archived supplier or part, a quantity below 1 or an unknown part. A supplier
    /// whose draft cannot be created (for example a quantity above the maximum) has all its items reported as skipped; the other
    /// suppliers' drafts are still created.
    /// </summary>
    Task<ReorderDraftResult> CreateDraftPosAsync(
        IReadOnlyList<ReorderRequest> requests, PurchaseOrderActor actor, CancellationToken cancellationToken = default);
}

public sealed class ReorderService(AppDbContext db, IPurchaseOrderService purchaseOrders) : IReorderService
{
    /// <summary>Most items one call will handle (a page of suggestions is far smaller).</summary>
    public const int MaxRequests = 500;

    public async Task<PagedResult<ReorderSuggestion>> GetSuggestionsAsync(ReorderFilter filter, CancellationToken cancellationToken = default)
    {
        var rows = db.LowStock();

        if (filter.CategoryId is { } categoryId)
        {
            rows = rows.Where(r => r.CategoryId == categoryId);
        }

        if (filter.SupplierId is { } supplierId)
        {
            rows = rows.Where(r => r.PreferredSupplierId == supplierId);
        }

        if (filter.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            rows = rows.Where(r => EF.Functions.Like(r.Sku, pattern, LikePattern.Escape) || EF.Functions.Like(r.Name, pattern, LikePattern.Escape));
        }

        var ordered = (filter.Sort?.ToLowerInvariant(), filter.Desc) switch
        {
            ("sku", false) => rows.OrderBy(r => r.Sku),
            ("sku", true) => rows.OrderByDescending(r => r.Sku),
            ("name", false) => rows.OrderBy(r => r.Name),
            ("name", true) => rows.OrderByDescending(r => r.Name),
            ("category", false) => rows.OrderBy(r => r.CategoryName).ThenBy(r => r.Name),
            ("category", true) => rows.OrderByDescending(r => r.CategoryName).ThenBy(r => r.Name),
            ("onhand", false) => rows.OrderBy(r => r.OnHand).ThenBy(r => r.Name),
            ("onhand", true) => rows.OrderByDescending(r => r.OnHand).ThenBy(r => r.Name),
            ("onorder", false) => rows.OrderBy(r => r.OnOrder).ThenBy(r => r.Name),
            ("onorder", true) => rows.OrderByDescending(r => r.OnOrder).ThenBy(r => r.Name),
            ("reorderpoint", false) => rows.OrderBy(r => r.ReorderPoint).ThenBy(r => r.Name),
            ("reorderpoint", true) => rows.OrderByDescending(r => r.ReorderPoint).ThenBy(r => r.Name),
            ("supplier", false) => rows.OrderBy(r => r.PreferredSupplierName == null).ThenBy(r => r.PreferredSupplierName).ThenBy(r => r.Name),
            ("supplier", true) => rows.OrderByDescending(r => r.PreferredSupplierName).ThenBy(r => r.Name),

            // Default and "urgency": furthest below the reorder point first.
            (_, true) => rows.OrderByDescending(r => r.OnHand + r.OnOrder - r.ReorderPoint).ThenBy(r => r.Name),
            _ => rows.OrderBy(r => r.OnHand + r.OnOrder - r.ReorderPoint).ThenBy(r => r.Name),
        };

        var page = await PagedResult<LowStockRow>.CreateAsync(ordered.ThenBy(r => r.ProductId), filter, cancellationToken);
        var drafts = page.Items.Count == 0
            ? new Dictionary<int, InDraftQuantity>()
            : (await purchaseOrders.GetInDraftByProductAsync(page.Items.Select(r => r.ProductId).ToList(), cancellationToken)).ToDictionary(d => d.Key, d => d.Value);

        var items = page.Items.Select(r =>
        {
            drafts.TryGetValue(r.ProductId, out var inDraft);
            return new ReorderSuggestion(
                r.ProductId, r.Sku, r.Name, r.Unit, r.CategoryName, r.OnHand, r.OnOrder, r.ReorderPoint, r.ReorderQuantity,
                ReorderMath.SuggestedQuantity(r.OnHand, r.OnOrder, r.ReorderPoint, r.ReorderQuantity, r.PreferredMinOrderQuantity),
                r.PreferredSupplierId, r.PreferredSupplierName, r.PreferredSupplierIsActive, r.PreferredMinOrderQuantity, r.PreferredUnitCostCents,
                inDraft?.Total ?? 0, inDraft?.Orders ?? []);
        }).ToList();

        return new PagedResult<ReorderSuggestion>(items, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<ReorderDraftResult> CreateDraftPosAsync(
        IReadOnlyList<ReorderRequest> requests, PurchaseOrderActor actor, CancellationToken cancellationToken = default)
    {
        if (requests.Count > MaxRequests)
        {
            throw new ArgumentException($"At most {MaxRequests} items can be ordered at once.", nameof(requests));
        }

        // The first request for a product wins, so a doubled form field cannot order twice.
        var wanted = requests.GroupBy(r => r.ProductId).Select(g => g.First()).ToList();
        var ids = wanted.Select(r => r.ProductId).ToList();

        var known = (await (
                from p in db.Products.AsNoTracking()
                where ids.Contains(p.Id)
                let link = db.ProductSuppliers.FirstOrDefault(s => s.ProductId == p.Id && s.IsPreferred)
                select new
                {
                    p.Id,
                    p.Sku,
                    p.Name,
                    p.IsActive,
                    SupplierId = link == null ? (int?)null : link.SupplierId,
                    SupplierName = link == null ? null : link.Supplier!.Name,
                    SupplierActive = link != null && link.Supplier!.IsActive,
                })
            .ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id);

        var skipped = new List<ReorderSkipped>();
        var bySupplier = new Dictionary<int, (string Name, List<(ReorderRequest Request, string Sku, string ProductName)> Items)>();

        foreach (var request in wanted)
        {
            if (!known.TryGetValue(request.ProductId, out var product))
            {
                skipped.Add(new ReorderSkipped(request.ProductId, "?", $"Part #{request.ProductId}", "That part no longer exists."));
            }
            else if (!product.IsActive)
            {
                skipped.Add(new ReorderSkipped(product.Id, product.Sku, product.Name, "The part is archived."));
            }
            else if (request.Quantity < 1)
            {
                skipped.Add(new ReorderSkipped(product.Id, product.Sku, product.Name, "Enter a quantity of at least 1."));
            }
            else if (product.SupplierId is not { } supplierId)
            {
                skipped.Add(new ReorderSkipped(product.Id, product.Sku, product.Name, "No preferred supplier. Choose one on the part's page."));
            }
            else if (!product.SupplierActive)
            {
                skipped.Add(new ReorderSkipped(product.Id, product.Sku, product.Name, $"Its preferred supplier ({product.SupplierName}) is archived."));
            }
            else
            {
                if (!bySupplier.TryGetValue(supplierId, out var group))
                {
                    bySupplier[supplierId] = group = (product.SupplierName!, []);
                }

                group.Items.Add((request, product.Sku, product.Name));
            }
        }

        var created = new List<ReorderCreatedOrder>();
        foreach (var (supplierId, group) in bySupplier.OrderBy(g => g.Value.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key))
        {
            var lines = group.Items.Select(i => new PurchaseOrderLineInput(i.Request.ProductId, i.Request.Quantity)).ToList();
            var result = await purchaseOrders.CreateDraftAsync(supplierId, actor, lines, cancellationToken: cancellationToken);
            if (result.Succeeded)
            {
                var order = result.Value;
                created.Add(new ReorderCreatedOrder(order.Id, order.Number, supplierId, group.Name, order.Lines.Count, order.TotalCents));
            }
            else
            {
                var reason = string.Join(" ", result.Errors.Select(e => e.Message));
                skipped.AddRange(group.Items.Select(i => new ReorderSkipped(i.Request.ProductId, i.Sku, i.ProductName, $"The order for {group.Name} could not be created: {reason}")));
            }
        }

        return new ReorderDraftResult(created, skipped);
    }
}
