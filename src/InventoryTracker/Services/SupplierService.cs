using System.Net.Mail;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface ISupplierService
{
    Task<PagedResult<SupplierListItem>> SearchAsync(SupplierListQuery query, CancellationToken cancellationToken = default);

    Task<SupplierDetail?> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<Result<SupplierDetail>> CreateAsync(SupplierInput input, CancellationToken cancellationToken = default);

    /// <summary>Saves the edit if <paramref name="rowVersion"/> (the version the form was loaded with) is still current.</summary>
    Task<Result<SupplierDetail>> UpdateAsync(int id, long rowVersion, SupplierInput input, CancellationToken cancellationToken = default);

    /// <summary>Archives (<c>false</c>) or restores (<c>true</c>) a supplier. Idempotent. Pass <paramref name="rowVersion"/> to detect a stale page.</summary>
    Task<Result> SetActiveAsync(int id, bool isActive, long? rowVersion = null, CancellationToken cancellationToken = default);

    /// <summary>The products a supplier provides (archived products included), by product name.</summary>
    Task<IReadOnlyList<ProductSupplierItem>> GetProductsAsync(int supplierId, CancellationToken cancellationToken = default);

    /// <summary>The suppliers of a product: the preferred one first, then by supplier name.</summary>
    Task<IReadOnlyList<ProductSupplierItem>> GetForProductAsync(int productId, CancellationToken cancellationToken = default);

    Task<ProductSupplierItem?> GetLinkAsync(int productId, int supplierId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the link, or updates it when the pair is already linked. A <em>new</em> link needs an active product and an
    /// active supplier; an existing one can still be edited after either is archived. Setting
    /// <see cref="SupplierLinkInput.IsPreferred"/> clears it on the product's other links in the same transaction.
    /// </summary>
    Task<Result<ProductSupplierItem>> LinkProductAsync(int productId, int supplierId, SupplierLinkInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the link. Idempotent. Existing purchase orders are not affected.</summary>
    Task<Result> UnlinkProductAsync(int productId, int supplierId, CancellationToken cancellationToken = default);

    /// <summary>Makes this supplier the product's preferred one (clearing the previous), or clears the flag when <paramref name="preferred"/> is false.</summary>
    Task<Result> SetPreferredAsync(int productId, int supplierId, bool preferred = true, CancellationToken cancellationToken = default);

    /// <summary>Picker search over name and contact name; an exact name match first. Archived suppliers only when <paramref name="activeOnly"/> is false.</summary>
    Task<IReadOnlyList<SupplierLookupItem>> LookupAsync(string? query, bool activeOnly = true, int take = 20, CancellationToken cancellationToken = default);

    /// <summary>Active suppliers not yet linked to the product (the "add supplier" choices), by name.</summary>
    Task<IReadOnlyList<SupplierLookupItem>> ListUnlinkedForProductAsync(int productId, int take = 200, CancellationToken cancellationToken = default);
}

public sealed class SupplierService(AppDbContext db) : ISupplierService
{
    public const int MaxLookupTake = 200;

    public async Task<PagedResult<SupplierListItem>> SearchAsync(SupplierListQuery query, CancellationToken cancellationToken = default)
    {
        var suppliers = db.Suppliers.AsNoTracking().AsQueryable();

        suppliers = query.Status switch
        {
            SupplierStatus.Active => suppliers.Where(s => s.IsActive),
            SupplierStatus.Archived => suppliers.Where(s => !s.IsActive),
            _ => suppliers,
        };

        if (query.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            suppliers = suppliers.Where(s =>
                EF.Functions.Like(s.Name, pattern, LikePattern.Escape)
                || (s.ContactName != null && EF.Functions.Like(s.ContactName, pattern, LikePattern.Escape))
                || (s.Email != null && EF.Functions.Like(s.Email, pattern, LikePattern.Escape)));
        }

        // Every ordering ends in Id so paging is stable when the sort column has ties.
        var ordered = (query.Sort?.ToLowerInvariant(), query.Desc) switch
        {
            ("contact", false) => suppliers.OrderBy(s => s.ContactName == null).ThenBy(s => s.ContactName).ThenBy(s => s.Name),
            ("contact", true) => suppliers.OrderByDescending(s => s.ContactName).ThenBy(s => s.Name),
            ("products", false) => suppliers.OrderBy(s => s.Products.Count).ThenBy(s => s.Name),
            ("products", true) => suppliers.OrderByDescending(s => s.Products.Count).ThenBy(s => s.Name),
            (_, true) => suppliers.OrderByDescending(s => s.Name),
            _ => suppliers.OrderBy(s => s.Name),
        };

        var page = ordered.ThenBy(s => s.Id).Select(s => new SupplierListItem(
            s.Id, s.Name, s.ContactName, s.Phone, s.Email, s.IsActive, s.Products.Count));

        return await PagedResult<SupplierListItem>.CreateAsync(page, query, cancellationToken);
    }

    public async Task<SupplierDetail?> GetAsync(int id, CancellationToken cancellationToken = default)
        => await db.Suppliers.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new SupplierDetail(
                s.Id, s.Name, s.ContactName, s.Email, s.Phone, s.Address, s.Website, s.Notes,
                s.IsActive, s.Products.Count, s.RowVersion, s.CreatedAt, s.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Result<SupplierDetail>> CreateAsync(SupplierInput input, CancellationToken cancellationToken = default)
    {
        var errors = await ValidateAsync(input, existingId: 0, cancellationToken);
        if (errors.Count > 0)
        {
            return Result.Failure<SupplierDetail>([.. errors]);
        }

        var supplier = new Supplier { Name = Clean(input.Name)! };
        Apply(supplier, input);
        db.Suppliers.Add(supplier);

        if (await SaveSupplierAsync(supplier.Name, cancellationToken) is { } error)
        {
            return Result.Failure<SupplierDetail>(error);
        }

        return Result.Success((await GetAsync(supplier.Id, cancellationToken))!);
    }

    public async Task<Result<SupplierDetail>> UpdateAsync(int id, long rowVersion, SupplierInput input, CancellationToken cancellationToken = default)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (supplier is null)
        {
            return Result.Failure<SupplierDetail>(NotFound());
        }

        if (supplier.RowVersion != rowVersion)
        {
            return Result.Failure<SupplierDetail>(ConflictError());
        }

        var errors = await ValidateAsync(input, id, cancellationToken);
        if (errors.Count > 0)
        {
            return Result.Failure<SupplierDetail>([.. errors]);
        }

        supplier.Name = Clean(input.Name)!;
        Apply(supplier, input);

        if (await SaveSupplierAsync(supplier.Name, cancellationToken) is { } error)
        {
            return Result.Failure<SupplierDetail>(error);
        }

        return Result.Success((await GetAsync(id, cancellationToken))!);
    }

    public async Task<Result> SetActiveAsync(int id, bool isActive, long? rowVersion = null, CancellationToken cancellationToken = default)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (supplier is null)
        {
            return Result.Failure(NotFound());
        }

        if (rowVersion is { } expected && supplier.RowVersion != expected)
        {
            return Result.Failure(ConflictError());
        }

        if (supplier.IsActive == isActive)
        {
            return Result.Success();
        }

        supplier.IsActive = isActive;
        return await SaveSupplierAsync(supplier.Name, cancellationToken) is { } error ? Result.Failure(error) : Result.Success();
    }

    // ---- links -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ProductSupplierItem>> GetProductsAsync(int supplierId, CancellationToken cancellationToken = default)
        => await Items(db.ProductSuppliers.AsNoTracking().Where(l => l.SupplierId == supplierId)
                .OrderBy(l => l.Product!.Name).ThenBy(l => l.Product!.Sku).ThenBy(l => l.ProductId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductSupplierItem>> GetForProductAsync(int productId, CancellationToken cancellationToken = default)
        => await Items(db.ProductSuppliers.AsNoTracking().Where(l => l.ProductId == productId)
                .OrderByDescending(l => l.IsPreferred).ThenBy(l => l.Supplier!.Name).ThenBy(l => l.SupplierId))
            .ToListAsync(cancellationToken);

    public async Task<ProductSupplierItem?> GetLinkAsync(int productId, int supplierId, CancellationToken cancellationToken = default)
        => await Items(db.ProductSuppliers.AsNoTracking().Where(l => l.ProductId == productId && l.SupplierId == supplierId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Result<ProductSupplierItem>> LinkProductAsync(
        int productId, int supplierId, SupplierLinkInput input, CancellationToken cancellationToken = default)
    {
        var product = await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => new { p.IsActive }).FirstOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return Result.Failure<ProductSupplierItem>("supplier.product_missing", "That part no longer exists.", SupplierLinkFields.ProductId);
        }

        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId).Select(s => new { s.IsActive }).FirstOrDefaultAsync(cancellationToken);
        if (supplier is null)
        {
            return Result.Failure<ProductSupplierItem>("supplier.not_found", "That supplier no longer exists.", SupplierLinkFields.SupplierId);
        }

        var errors = ValidateLink(input);

        var link = await db.ProductSuppliers.FirstOrDefaultAsync(l => l.ProductId == productId && l.SupplierId == supplierId, cancellationToken);
        if (link is null)
        {
            if (!product.IsActive)
            {
                errors.Add(new Error("supplier.link_product_archived", "That part is archived. Restore it before adding suppliers.", SupplierLinkFields.ProductId));
            }

            if (!supplier.IsActive)
            {
                errors.Add(new Error("supplier.link_supplier_archived", "That supplier is archived. Restore it before linking parts.", SupplierLinkFields.SupplierId));
            }
        }

        if (input.IsPreferred && link is not { IsPreferred: true } && !supplier.IsActive && errors.All(e => e.Code != "supplier.link_supplier_archived"))
        {
            errors.Add(PreferredArchived());
        }

        if (errors.Count > 0)
        {
            return Result.Failure<ProductSupplierItem>([.. errors]);
        }

        var error = await InTransactionAsync(async () =>
        {
            if (input.IsPreferred)
            {
                await ClearOtherPreferredAsync(productId, supplierId, cancellationToken);
            }

            if (link is null)
            {
                link = new ProductSupplier { ProductId = productId, SupplierId = supplierId };
                db.ProductSuppliers.Add(link);
            }

            link.SupplierSku = Clean(input.SupplierSku);
            link.UnitCostCents = input.UnitCostCents;
            link.LeadTimeDays = input.LeadTimeDays;
            link.MinOrderQuantity = input.MinOrderQuantity;
            link.IsPreferred = input.IsPreferred;
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return error is not null
            ? Result.Failure<ProductSupplierItem>(error)
            : Result.Success((await GetLinkAsync(productId, supplierId, cancellationToken))!);
    }

    public async Task<Result> UnlinkProductAsync(int productId, int supplierId, CancellationToken cancellationToken = default)
    {
        await db.ProductSuppliers.Where(l => l.ProductId == productId && l.SupplierId == supplierId).ExecuteDeleteAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> SetPreferredAsync(int productId, int supplierId, bool preferred = true, CancellationToken cancellationToken = default)
    {
        var link = await db.ProductSuppliers.Include(l => l.Supplier).FirstOrDefaultAsync(l => l.ProductId == productId && l.SupplierId == supplierId, cancellationToken);
        if (link is null)
        {
            return Result.Failure("supplier.link_missing", "That supplier is no longer linked to the part.");
        }

        if (link.IsPreferred == preferred)
        {
            return Result.Success();
        }

        if (preferred && link.Supplier is { IsActive: false })
        {
            return Result.Failure(PreferredArchived());
        }

        var error = await InTransactionAsync(async () =>
        {
            if (preferred)
            {
                await ClearOtherPreferredAsync(productId, supplierId, cancellationToken);
            }

            link.IsPreferred = preferred;
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return error is null ? Result.Success() : Result.Failure(error);
    }

    // ---- pickers -----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SupplierLookupItem>> LookupAsync(string? query, bool activeOnly = true, int take = 20, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, MaxLookupTake);
        var suppliers = db.Suppliers.AsNoTracking().AsQueryable();
        if (activeOnly)
        {
            suppliers = suppliers.Where(s => s.IsActive);
        }

        IOrderedQueryable<Supplier> ordered;
        if (string.IsNullOrWhiteSpace(query))
        {
            ordered = suppliers.OrderBy(s => s.Name);
        }
        else
        {
            var term = query.Trim();
            var pattern = LikePattern.Contains(term);
            suppliers = suppliers.Where(s =>
                EF.Functions.Like(s.Name, pattern, LikePattern.Escape)
                || (s.ContactName != null && EF.Functions.Like(s.ContactName, pattern, LikePattern.Escape)));

            // A fully typed name shows that supplier first, then the rest by name.
            ordered = suppliers.OrderByDescending(s => s.Name == term).ThenBy(s => s.Name);
        }

        return await ordered.ThenBy(s => s.Id)
            .Take(take)
            .Select(s => new SupplierLookupItem(s.Id, s.Name, s.ContactName, s.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SupplierLookupItem>> ListUnlinkedForProductAsync(int productId, int take = 200, CancellationToken cancellationToken = default)
        => await db.Suppliers.AsNoTracking()
            .Where(s => s.IsActive && !s.Products.Any(l => l.ProductId == productId))
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Take(Math.Clamp(take, 1, MaxLookupTake))
            .Select(s => new SupplierLookupItem(s.Id, s.Name, s.ContactName, s.IsActive))
            .ToListAsync(cancellationToken);

    // ---- internals ---------------------------------------------------------------------------------------------

    private static IQueryable<ProductSupplierItem> Items(IQueryable<ProductSupplier> links)
        => links.Select(l => new ProductSupplierItem(
            l.ProductId,
            l.Product!.Sku,
            l.Product.Name,
            l.Product.IsActive,
            l.SupplierId,
            l.Supplier!.Name,
            l.Supplier.IsActive,
            l.SupplierSku,
            l.UnitCostCents,
            l.LeadTimeDays,
            l.MinOrderQuantity,
            l.IsPreferred));

    /// <summary>
    /// The filtered unique index allows one preferred link per product and SQLite checks it statement by statement, so the
    /// old flag has to be saved as cleared <em>before</em> the new one is set (the caller wraps both in one transaction).
    /// </summary>
    private async Task ClearOtherPreferredAsync(int productId, int supplierId, CancellationToken cancellationToken)
    {
        var others = await db.ProductSuppliers
            .Where(l => l.ProductId == productId && l.SupplierId != supplierId && l.IsPreferred)
            .ToListAsync(cancellationToken);
        if (others.Count == 0)
        {
            return;
        }

        foreach (var other in others)
        {
            other.IsPreferred = false;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Runs <paramref name="work"/> in a transaction (joining one that is already open) and turns a lost race into an <see cref="Error"/>.</summary>
    private async Task<Error?> InTransactionAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await work();
            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }

            return null;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || DbErrors.IsUniqueViolation(ex, out _))
        {
            // Two people changed the same product's suppliers at the same moment (preferred index, or the same pair added twice).
            db.ChangeTracker.Clear();
            return new Error("supplier.link_conflict", "Someone else just changed this part's suppliers. Check the list and try again.");
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }
    }

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void Apply(Supplier supplier, SupplierInput input)
    {
        supplier.ContactName = Clean(input.ContactName);
        supplier.Email = Clean(input.Email);
        supplier.Phone = Clean(input.Phone);
        supplier.Address = NormalizeAddress(input.Address);
        supplier.Website = NormalizeWebsite(Clean(input.Website));
        supplier.Notes = Clean(input.Notes);
    }

    /// <summary>Trims each line and the whole, keeps line breaks (as \n); blank = null.</summary>
    private static string? NormalizeAddress(string? address)
    {
        if (Clean(address) is not { } text)
        {
            return null;
        }

        return string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()));
    }

    /// <summary>"acme.example" becomes "https://acme.example"; anything with a scheme is kept as typed.</summary>
    private static string? NormalizeWebsite(string? website)
        => website is null ? null : website.Contains("://", StringComparison.Ordinal) ? website : "https://" + website;

    private static bool IsValidWebsite(string website)
        => Uri.TryCreate(website, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrEmpty(uri.Host)
           && !website.Any(char.IsWhiteSpace);

    private static bool IsValidEmail(string email)
        => !email.Any(char.IsWhiteSpace)
           && MailAddress.TryCreate(email, out var address)
           && address.Address == email
           && address.Host.Contains('.', StringComparison.Ordinal);

    private async Task<List<Error>> ValidateAsync(SupplierInput input, int existingId, CancellationToken cancellationToken)
    {
        var errors = new List<Error>();

        var name = Clean(input.Name);
        if (name is null)
        {
            errors.Add(new Error("supplier.name_required", "Name is required.", SupplierFields.Name));
        }
        else if (name.Length > Supplier.NameMaxLength)
        {
            errors.Add(new Error("supplier.name_too_long", $"Name can be at most {Supplier.NameMaxLength} characters.", SupplierFields.Name));
        }
        else if (await db.Suppliers.AnyAsync(s => s.Name == name && s.Id != existingId, cancellationToken))
        {
            errors.Add(NameTaken(name));
        }

        TooLong(errors, input.ContactName, Supplier.ContactNameMaxLength, SupplierFields.ContactName, "Contact name");
        TooLong(errors, input.Phone, Supplier.PhoneMaxLength, SupplierFields.Phone, "Phone");
        TooLong(errors, NormalizeAddress(input.Address), Supplier.AddressMaxLength, SupplierFields.Address, "Address");
        TooLong(errors, input.Notes, Supplier.NotesMaxLength, SupplierFields.Notes, "Notes");

        if (Clean(input.Email) is { } email)
        {
            if (email.Length > Supplier.EmailMaxLength)
            {
                errors.Add(new Error("supplier.email_too_long", $"Email can be at most {Supplier.EmailMaxLength} characters.", SupplierFields.Email));
            }
            else if (!IsValidEmail(email))
            {
                errors.Add(new Error("supplier.email_invalid", "Enter a valid email address, like orders@acme.example.", SupplierFields.Email));
            }
        }

        if (NormalizeWebsite(Clean(input.Website)) is { } website)
        {
            if (website.Length > Supplier.WebsiteMaxLength)
            {
                errors.Add(new Error("supplier.website_too_long", $"Website can be at most {Supplier.WebsiteMaxLength} characters.", SupplierFields.Website));
            }
            else if (!IsValidWebsite(website))
            {
                errors.Add(new Error("supplier.website_invalid", "Enter a web address starting with http:// or https://, like https://acme.example.", SupplierFields.Website));
            }
        }

        return errors;
    }

    private static List<Error> ValidateLink(SupplierLinkInput input)
    {
        var errors = new List<Error>();

        if (Clean(input.SupplierSku) is { } sku && sku.Length > ProductSupplier.SupplierSkuMaxLength)
        {
            errors.Add(new Error("supplier.sku_too_long", $"Supplier SKU can be at most {ProductSupplier.SupplierSkuMaxLength} characters.", SupplierLinkFields.SupplierSku));
        }

        if (input.UnitCostCents < 0)
        {
            errors.Add(new Error("supplier.cost_negative", "Unit cost cannot be negative.", SupplierLinkFields.UnitCost));
        }

        if (input.LeadTimeDays is < 0 or > ProductSupplier.MaxLeadTimeDays)
        {
            errors.Add(new Error("supplier.lead_time_invalid", $"Lead time must be between 0 and {ProductSupplier.MaxLeadTimeDays} days.", SupplierLinkFields.LeadTimeDays));
        }

        if (input.MinOrderQuantity is < 1 or > ProductSupplier.MaxMinOrderQuantity)
        {
            errors.Add(new Error("supplier.min_order_invalid", "Minimum order quantity must be a whole number, 1 or more.", SupplierLinkFields.MinOrderQuantity));
        }

        return errors;
    }

    private static void TooLong(List<Error> errors, string? value, int max, string field, string label)
    {
        if (Clean(value) is { } text && text.Length > max)
        {
            errors.Add(new Error($"supplier.{field.ToLowerInvariant()}_too_long", $"{label} can be at most {max} characters.", field));
        }
    }

    private static Error NameTaken(string name) =>
        new("supplier.name_taken", $"A supplier named '{name}' already exists (it may be archived).", SupplierFields.Name);

    private static Error NotFound() => new("supplier.not_found", "That supplier no longer exists.");

    private static Error PreferredArchived() =>
        new("supplier.preferred_archived", "An archived supplier cannot be the preferred one. Restore it first.");

    private static Error ConflictError() =>
        new("supplier.conflict", "This supplier was modified by someone else. Reload the page to see the latest version, then try again.");

    /// <summary>Saves a supplier, turning a lost concurrency race or a duplicate-name race into an <see cref="Error"/>.</summary>
    private async Task<Error?> SaveSupplierAsync(string name, CancellationToken cancellationToken)
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
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, out var target) && target.Contains("Suppliers.Name", StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return NameTaken(name);
        }
    }
}
