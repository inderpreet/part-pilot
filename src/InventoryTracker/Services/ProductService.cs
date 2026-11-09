using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface IProductService
{
    Task<PagedResult<ProductListItem>> SearchAsync(ProductListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Exact SKU match, else exact barcode match (case-insensitive, archived products included); null if none.</summary>
    Task<ProductDetail?> FindByCodeAsync(string? code, CancellationToken cancellationToken = default);

    Task<ProductDetail?> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<Result<ProductDetail>> CreateAsync(ProductInput input, CancellationToken cancellationToken = default);

    /// <summary>Saves the edit if <paramref name="rowVersion"/> (the version the form was loaded with) is still current.</summary>
    Task<Result<ProductDetail>> UpdateAsync(int id, long rowVersion, ProductInput input, CancellationToken cancellationToken = default);

    /// <summary>Archives (<c>false</c>) or restores (<c>true</c>) a product. Idempotent. Pass <paramref name="rowVersion"/> to detect a stale page.</summary>
    Task<Result> SetActiveAsync(int id, bool isActive, long? rowVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Picker search over SKU, name and barcode; exact SKU/barcode matches first.</summary>
    Task<IReadOnlyList<ProductLookupItem>> LookupAsync(string? query, bool activeOnly = true, int take = 20, CancellationToken cancellationToken = default);
}

public sealed class ProductService(AppDbContext db) : IProductService
{
    public const int MaxLookupTake = 50;

    public async Task<PagedResult<ProductListItem>> SearchAsync(ProductListQuery query, CancellationToken cancellationToken = default)
    {
        var products = db.Products.AsNoTracking().AsQueryable();

        products = query.Status switch
        {
            ProductStatus.Active => products.Where(p => p.IsActive),
            ProductStatus.Archived => products.Where(p => !p.IsActive),
            _ => products,
        };

        if (query.CategoryId is { } categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        if (query.BinTerm is { } bin)
        {
            var binPattern = LikePattern.Contains(bin);
            products = products.Where(p => p.BinLocation != null && EF.Functions.Like(p.BinLocation, binPattern, LikePattern.Escape));
        }

        if (query.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            products = products.Where(p =>
                EF.Functions.Like(p.Sku, pattern, LikePattern.Escape)
                || EF.Functions.Like(p.Name, pattern, LikePattern.Escape)
                || (p.Barcode != null && EF.Functions.Like(p.Barcode, pattern, LikePattern.Escape)));
        }

        // Every ordering ends in Id so paging is stable when the sort column has ties.
        var ordered = (query.Sort?.ToLowerInvariant(), query.Desc) switch
        {
            ("sku", false) => products.OrderBy(p => p.Sku),
            ("sku", true) => products.OrderByDescending(p => p.Sku),
            ("onhand", false) => products.OrderBy(p => p.QuantityOnHand).ThenBy(p => p.Name),
            ("onhand", true) => products.OrderByDescending(p => p.QuantityOnHand).ThenBy(p => p.Name),
            ("category", false) => products.OrderBy(p => p.Category!.Name).ThenBy(p => p.Name),
            ("category", true) => products.OrderByDescending(p => p.Category!.Name).ThenBy(p => p.Name),
            ("bin", false) => products.OrderBy(p => p.BinLocation == null).ThenBy(p => p.BinLocation).ThenBy(p => p.Name),
            ("bin", true) => products.OrderByDescending(p => p.BinLocation).ThenBy(p => p.Name),
            (_, true) => products.OrderByDescending(p => p.Name),
            _ => products.OrderBy(p => p.Name),
        };

        var page = ordered.ThenBy(p => p.Id).Select(p => new ProductListItem(
            p.Id,
            p.Sku,
            p.Name,
            p.Category == null ? null : p.Category.Name,
            p.Unit,
            p.BinLocation,
            p.QuantityOnHand,
            p.ReorderPoint,
            p.UnitCostCents,
            p.IsActive,
            // Low once what is already on order is counted too (F06 FR-4); the same rule as ReportingQueries.LowStock.
            p.IsActive && p.ReorderPoint > 0
                && p.QuantityOnHand + (db.PurchaseOrderLines
                    .Where(l => l.ProductId == p.Id
                                && (l.PurchaseOrder!.Status == PurchaseOrderStatus.Submitted || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived)
                                && l.QuantityOrdered > l.QuantityReceived)
                    .Sum(l => (int?)(l.QuantityOrdered - l.QuantityReceived)) ?? 0) <= p.ReorderPoint));

        return await PagedResult<ProductListItem>.CreateAsync(page, query, cancellationToken);
    }

    public async Task<ProductDetail?> FindByCodeAsync(string? code, CancellationToken cancellationToken = default)
    {
        var text = code?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Columns are NOCASE, so == is case-insensitive. A SKU match wins over a barcode match.
        var id = await db.Products.AsNoTracking().Where(p => p.Sku == text).Select(p => (int?)p.Id).FirstOrDefaultAsync(cancellationToken)
                 ?? await db.Products.AsNoTracking().Where(p => p.Barcode == text).Select(p => (int?)p.Id).FirstOrDefaultAsync(cancellationToken);

        return id is { } found ? await GetAsync(found, cancellationToken) : null;
    }

    public async Task<ProductDetail?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var p = await db.Products.AsNoTracking().Include(x => x.Category).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (p is null)
        {
            return null;
        }

        var onOrder = await db.OnOrder().Where(o => o.ProductId == id).Select(o => o.Quantity).FirstOrDefaultAsync(cancellationToken);
        return ToDetail(p, await IsSkuLockedAsync(p.Id, cancellationToken), onOrder);
    }

    public async Task<Result<ProductDetail>> CreateAsync(ProductInput input, CancellationToken cancellationToken = default)
    {
        var errors = await ValidateAsync(input, existing: null, cancellationToken);
        if (errors.Count > 0)
        {
            return Result.Failure<ProductDetail>([.. errors]);
        }

        var product = new Product { Sku = Clean(input.Sku)!, Name = Clean(input.Name)! };
        Apply(product, input);
        db.Products.Add(product);

        if (await SaveAsync(cancellationToken) is { } error)
        {
            return Result.Failure<ProductDetail>(error);
        }

        return Result.Success((await GetAsync(product.Id, cancellationToken))!);
    }

    public async Task<Result<ProductDetail>> UpdateAsync(int id, long rowVersion, ProductInput input, CancellationToken cancellationToken = default)
    {
        var product = await db.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return Result.Failure<ProductDetail>("product.not_found", "That part no longer exists.");
        }

        if (product.RowVersion != rowVersion)
        {
            return Result.Failure<ProductDetail>(ConflictError());
        }

        var errors = await ValidateAsync(input, product, cancellationToken);
        if (errors.Count > 0)
        {
            return Result.Failure<ProductDetail>([.. errors]);
        }

        product.Sku = Clean(input.Sku)!;
        product.Name = Clean(input.Name)!;
        Apply(product, input);

        if (await SaveAsync(cancellationToken) is { } error)
        {
            return Result.Failure<ProductDetail>(error);
        }

        return Result.Success((await GetAsync(id, cancellationToken))!);
    }

    public async Task<Result> SetActiveAsync(int id, bool isActive, long? rowVersion = null, CancellationToken cancellationToken = default)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return Result.Failure("product.not_found", "That part no longer exists.");
        }

        if (rowVersion is { } expected && product.RowVersion != expected)
        {
            return Result.Failure(ConflictError());
        }

        if (product.IsActive == isActive)
        {
            return Result.Success();
        }

        product.IsActive = isActive;
        return await SaveAsync(cancellationToken) is { } error ? Result.Failure(error) : Result.Success();
    }

    public async Task<IReadOnlyList<ProductLookupItem>> LookupAsync(string? query, bool activeOnly = true, int take = 20, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, MaxLookupTake);
        var products = db.Products.AsNoTracking().AsQueryable();
        if (activeOnly)
        {
            products = products.Where(p => p.IsActive);
        }

        var term = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        IOrderedQueryable<Product> ordered;
        if (term is null)
        {
            ordered = products.OrderBy(p => p.Name);
        }
        else
        {
            var pattern = LikePattern.Contains(term);
            products = products.Where(p =>
                EF.Functions.Like(p.Sku, pattern, LikePattern.Escape)
                || EF.Functions.Like(p.Name, pattern, LikePattern.Escape)
                || (p.Barcode != null && EF.Functions.Like(p.Barcode, pattern, LikePattern.Escape)));

            // A scanned or fully typed code shows its product first, then the rest by name.
            ordered = products.OrderByDescending(p => p.Sku == term || p.Barcode == term).ThenBy(p => p.Name);
        }

        return await ordered.ThenBy(p => p.Id)
            .Take(take)
            .Select(p => new ProductLookupItem(p.Id, p.Sku, p.Name, p.Unit, p.Barcode, p.QuantityOnHand, p.IsActive))
            .ToListAsync(cancellationToken);
    }

    // ---- internals ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A product's SKU becomes read-only once it has stock transactions (keeps history and exports consistent).
    /// </summary>
    private Task<bool> IsSkuLockedAsync(int productId, CancellationToken cancellationToken)
        => db.StockTransactions.AnyAsync(t => t.ProductId == productId, cancellationToken);

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void Apply(Product product, ProductInput input)
    {
        product.Description = Clean(input.Description);
        product.CategoryId = input.CategoryId;
        product.Unit = Clean(input.Unit) ?? Product.DefaultUnit;
        product.Barcode = Clean(input.Barcode);
        product.BinLocation = Clean(input.BinLocation);
        product.ReorderPoint = input.ReorderPoint;
        product.ReorderQuantity = input.ReorderQuantity;
        product.UnitCostCents = input.UnitCostCents;
    }

    /// <summary>The rules that need no database, so an import can report them per row in its preview.</summary>
    internal static void ValidateFields(ProductInput input, List<Error> errors)
    {
        var sku = Clean(input.Sku);
        if (sku is null)
        {
            errors.Add(new Error("product.sku_required", "SKU is required.", ProductFields.Sku));
        }
        else if (sku.Length > Product.SkuMaxLength)
        {
            errors.Add(new Error("product.sku_too_long", $"SKU can be at most {Product.SkuMaxLength} characters.", ProductFields.Sku));
        }

        var name = Clean(input.Name);
        if (name is null)
        {
            errors.Add(new Error("product.name_required", "Name is required.", ProductFields.Name));
        }
        else if (name.Length > Product.NameMaxLength)
        {
            errors.Add(new Error("product.name_too_long", $"Name can be at most {Product.NameMaxLength} characters.", ProductFields.Name));
        }

        TooLong(errors, input.Description, Product.DescriptionMaxLength, ProductFields.Description, "Description");
        TooLong(errors, input.Unit, Product.UnitMaxLength, ProductFields.Unit, "Unit");
        TooLong(errors, input.Barcode, Product.BarcodeMaxLength, ProductFields.Barcode, "Barcode");
        TooLong(errors, input.BinLocation, Product.BinLocationMaxLength, ProductFields.BinLocation, "Bin location");

        if (input.ReorderPoint < 0)
        {
            errors.Add(new Error("product.reorder_point_negative", "Reorder point cannot be negative.", ProductFields.ReorderPoint));
        }

        if (input.ReorderQuantity < 0)
        {
            errors.Add(new Error("product.reorder_quantity_negative", "Reorder quantity cannot be negative.", ProductFields.ReorderQuantity));
        }

        if (input.UnitCostCents < 0)
        {
            errors.Add(new Error("product.cost_negative", "Unit cost cannot be negative.", ProductFields.UnitCost));
        }
    }

    private async Task<List<Error>> ValidateAsync(ProductInput input, Product? existing, CancellationToken cancellationToken)
    {
        var errors = new List<Error>();
        ValidateFields(input, errors);
        var sku = Clean(input.Sku);

        if (existing is not null && sku is not null && !string.Equals(existing.Sku, sku, StringComparison.Ordinal)
            && await IsSkuLockedAsync(existing.Id, cancellationToken))
        {
            errors.Add(new Error("product.sku_locked", "The SKU cannot be changed because this part has stock history.", ProductFields.Sku));
        }

        if (input.CategoryId is { } categoryId)
        {
            var category = await db.Categories.AsNoTracking().Where(c => c.Id == categoryId).Select(c => new { c.IsActive }).FirstOrDefaultAsync(cancellationToken);
            if (category is null)
            {
                errors.Add(new Error("product.category_missing", "That category no longer exists.", ProductFields.CategoryId));
            }
            else if (!category.IsActive && existing?.CategoryId != categoryId)
            {
                // An archived category can stay on products that already have it, but cannot be newly chosen.
                errors.Add(new Error("product.category_archived", "That category is archived. Pick an active one.", ProductFields.CategoryId));
            }
        }

        // Friendly pre-checks; the unique indexes remain the real guard (see SaveAsync) for concurrent requests.
        var excludeId = existing?.Id ?? 0;
        if (sku is not null && sku.Length <= Product.SkuMaxLength
            && await db.Products.AnyAsync(p => p.Sku == sku && p.Id != excludeId, cancellationToken))
        {
            errors.Add(SkuTaken(sku));
        }

        var barcode = Clean(input.Barcode);
        if (barcode is not null && barcode.Length <= Product.BarcodeMaxLength
            && await db.Products.AnyAsync(p => p.Barcode == barcode && p.Id != excludeId, cancellationToken))
        {
            errors.Add(BarcodeTaken(barcode));
        }

        return errors;
    }

    private static void TooLong(List<Error> errors, string? value, int max, string field, string label)
    {
        if (Clean(value) is { } text && text.Length > max)
        {
            errors.Add(new Error($"product.{field.ToLowerInvariant()}_too_long", $"{label} can be at most {max} characters.", field));
        }
    }

    private static Error SkuTaken(string sku) =>
        new("product.sku_taken", $"SKU '{sku}' is already used by another part (it may be archived).", ProductFields.Sku);

    private static Error BarcodeTaken(string barcode) =>
        new("product.barcode_taken", $"Barcode '{barcode}' is already used by another part (it may be archived).", ProductFields.Barcode);

    private static Error ConflictError() =>
        new("product.conflict", "This part was modified by someone else. Reload the page to see the latest version, then try again.");

    /// <summary>Saves, turning a lost concurrency race or a unique-index race into an <see cref="Error"/>.</summary>
    private async Task<Error?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ConflictError();
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, out var target)
                                           && (target.Contains("Products.Sku", StringComparison.Ordinal) || target.Contains("Products.Barcode", StringComparison.Ordinal)))
        {
            var skuClash = target.Contains("Products.Sku", StringComparison.Ordinal);
            var entry = ex.Entries.FirstOrDefault()?.Entity as Product;
            db.ChangeTracker.Clear();
            return skuClash ? SkuTaken(entry?.Sku ?? "that value") : BarcodeTaken(entry?.Barcode ?? "that value");
        }
    }

    private static ProductDetail ToDetail(Product p, bool skuLocked, int onOrder) => new(
        p.Id,
        p.Sku,
        p.Name,
        p.Description,
        p.CategoryId,
        p.Category?.Name,
        p.Category?.IsActive ?? true,
        p.Unit,
        p.Barcode,
        p.BinLocation,
        p.ReorderPoint,
        p.ReorderQuantity,
        p.UnitCostCents,
        p.QuantityOnHand,
        p.IsActive,
        p.IsActive && p.ReorderPoint > 0 && p.QuantityOnHand + onOrder <= p.ReorderPoint,
        skuLocked,
        p.RowVersion,
        p.CreatedAt,
        p.UpdatedAt);
}
