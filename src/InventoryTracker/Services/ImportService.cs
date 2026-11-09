using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

namespace InventoryTracker.Services;

public interface IImportService
{
    Task<Result<ImportPreview>> PreviewAsync(ImportKind kind, Stream stream, string fileName, ImportOptions options, ImportActor actor, CancellationToken cancellationToken = default);

    Task<Result<ImportBatchResult>> CommitAsync(string token, ImportActor actor, bool skipInvalid, CancellationToken cancellationToken = default);

    Task CancelAsync(string token, string userId, CancellationToken cancellationToken = default);

    /// <summary>Re-reads a staged file and returns the preview, or null when the token is missing or expired.</summary>
    Task<ImportPreview?> PeekAsync(string token, ImportActor actor, CancellationToken cancellationToken = default);

    /// <summary>The kind of a staged upload, so the page can check that kind's permission; null when the token is missing or expired.</summary>
    ImportKind? StagedKind(string token, string userId);

    Task<PagedResult<ImportBatchResult>> SearchBatchesAsync(ListQuery query, CancellationToken cancellationToken = default);

    Task<ImportBatchResult?> GetBatchAsync(long id, CancellationToken cancellationToken = default);

    Stream OpenTemplate(ImportKind kind);
}

public sealed class ImportService(
    AppDbContext db,
    IProductService products,
    ICategoryService categories,
    ISupplierService suppliers,
    IStockService stock,
    IProjectService projects,
    ImportStaging staging,
    IClock clock,
    ImportEventPublisher events) : IImportService
{
    private static readonly ManifestEmbeddedFileProvider Templates = new(typeof(ImportService).Assembly, "wwwroot");

    public async Task<Result<ImportPreview>> PreviewAsync(
        ImportKind kind, Stream stream, string fileName, ImportOptions options, ImportActor actor, CancellationToken cancellationToken = default)
    {
        string token;
        string path;
        try
        {
            (token, path) = await staging.SaveAsync(kind, fileName, options, actor.UserId, stream, cancellationToken);
        }
        catch (ImportTooLargeException)
        {
            return Result.Failure<ImportPreview>("import.too_large", $"The file is larger than {CsvImport.MaxBytes / (1024 * 1024)} MB.");
        }

        var table = ReadStaged(path, kind);
        if (!table.Succeeded)
        {
            staging.Remove(token);
            return Result.Failure<ImportPreview>([.. table.Errors]);
        }

        var classified = await ClassifyAsync(kind, table.Value, options, cancellationToken);
        return Result.Success(ToPreview(token, kind, staging.TryGet(token, actor.UserId)!.FileName, options, classified));
    }

    public async Task<Result<ImportBatchResult>> CommitAsync(string token, ImportActor actor, bool skipInvalid, CancellationToken cancellationToken = default)
    {
        var slot = staging.TryGet(token, actor.UserId);
        if (slot is null)
        {
            return Result.Failure<ImportBatchResult>("import.expired", "That preview has expired. Upload the file again.");
        }

        var table = ReadStaged(slot.Path, slot.Kind);
        if (!table.Succeeded)
        {
            return Result.Failure<ImportBatchResult>([.. table.Errors]);
        }

        var classified = await ClassifyAsync(slot.Kind, table.Value, slot.Options, cancellationToken);
        if (classified.Rows.Any(r => r.Action == ImportRowAction.Error) && !skipInvalid)
        {
            return Result.Failure<ImportBatchResult>("import.has_errors", "Fix the errors or tick “skip invalid rows”.");
        }

        var result = db.Database.CurrentTransaction is null
            ? await ApplyInOwnTransactionAsync(slot, actor, skipInvalid, classified, cancellationToken)
            : await ApplyInTransactionAsync(slot, actor, skipInvalid, classified, cancellationToken);

        if (result.Succeeded)
        {
            staging.Remove(token);
        }
        else
        {
            // The upload stays staged so the preview can be shown again; what the rolled-back work left tracked must not leak into it.
            db.ChangeTracker.Clear();
        }

        return result;
    }

    public Task CancelAsync(string token, string userId, CancellationToken cancellationToken = default)
    {
        if (staging.TryGet(token, userId) is not null)
        {
            staging.Remove(token);
        }

        return Task.CompletedTask;
    }

    public async Task<ImportPreview?> PeekAsync(string token, ImportActor actor, CancellationToken cancellationToken = default)
    {
        var slot = staging.TryGet(token, actor.UserId);
        if (slot is null)
        {
            return null;
        }

        var table = ReadStaged(slot.Path, slot.Kind);
        if (!table.Succeeded)
        {
            return null;
        }

        var classified = await ClassifyAsync(slot.Kind, table.Value, slot.Options, cancellationToken);
        return ToPreview(token, slot.Kind, slot.FileName, slot.Options, classified);
    }

    public ImportKind? StagedKind(string token, string userId) => staging.TryGet(token, userId)?.Kind;

    public Task<PagedResult<ImportBatchResult>> SearchBatchesAsync(ListQuery query, CancellationToken cancellationToken = default)
        => PagedResult<ImportBatchResult>.CreateAsync(
            db.ImportBatches.AsNoTracking().OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id).Select(ToListItem),
            query,
            cancellationToken);

    public Task<ImportBatchResult?> GetBatchAsync(long id, CancellationToken cancellationToken = default)
        => db.ImportBatches.AsNoTracking().Where(b => b.Id == id).Select(ToListItem).FirstOrDefaultAsync(cancellationToken);

    public Stream OpenTemplate(ImportKind kind)
    {
        var file = Templates.GetFileInfo("templates/" + ImportKinds.TemplateFileName(kind));
        if (!file.Exists)
        {
            throw new InvalidOperationException($"Missing template {kind}.");
        }

        return file.CreateReadStream();
    }

    private static System.Linq.Expressions.Expression<Func<ImportBatch, ImportBatchResult>> ToListItem =>
        b => new ImportBatchResult(
            b.Id, b.Kind, b.FileName, b.RowCount, b.CreatedCount, b.UpdatedCount, b.SkippedCount, b.Options,
            b.CreatedBy == null ? "" : b.CreatedBy.DisplayName, b.CreatedAt);

    /// <summary>Reads and closes the staged file, so it can be deleted afterwards (Windows refuses to delete an open file).</summary>
    private static Result<CsvTable> ReadStaged(string path, ImportKind kind)
    {
        using var file = File.OpenRead(path);
        return CsvImport.Read(file, ImportKinds.RequiredHeaders(kind));
    }

    private async Task<Result<ImportBatchResult>> ApplyInOwnTransactionAsync(
        ImportStaging.Slot slot, ImportActor actor, bool skipInvalid, Classified classified, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await ApplyInTransactionAsync(slot, actor, skipInvalid, classified, cancellationToken);
        if (result.Succeeded)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    private async Task<Result<ImportBatchResult>> ApplyInTransactionAsync(
        ImportStaging.Slot slot, ImportActor actor, bool skipInvalid, Classified classified, CancellationToken cancellationToken)
    {
        var batch = new ImportBatch
        {
            Kind = slot.Kind,
            FileName = slot.FileName,
            RowCount = classified.Rows.Count,
            Options = slot.Kind == ImportKind.Boms ? slot.Options.ToStored() : null,
            CreatedById = actor.UserId,
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);

        var counts = await ApplyAsync(slot.Kind, slot.Options, actor, skipInvalid, classified, batch.Id, cancellationToken);
        if (!counts.Succeeded)
        {
            return Result.Failure<ImportBatchResult>([.. counts.Errors]);
        }

        // The apply loops clear the tracker between rows, which detached the batch.
        db.ChangeTracker.Clear();
        db.ImportBatches.Attach(batch);
        batch.CreatedCount = counts.Value.Created;
        batch.UpdatedCount = counts.Value.Updated;
        batch.SkippedCount = counts.Value.Skipped;
        await db.SaveChangesAsync(cancellationToken);

        await events.PublishAsync(
            new ImportCommittedEvent(batch.Id, batch.Kind, batch.FileName, batch.CreatedCount, batch.UpdatedCount, batch.SkippedCount, actor.UserId, clock.UtcNow),
            cancellationToken);

        var createdBy = await db.Users.AsNoTracking().Where(u => u.Id == actor.UserId).Select(u => u.DisplayName).FirstAsync(cancellationToken);
        return Result.Success(new ImportBatchResult(
            batch.Id, batch.Kind, batch.FileName, batch.RowCount, batch.CreatedCount, batch.UpdatedCount, batch.SkippedCount,
            batch.Options, createdBy, batch.CreatedAt));
    }

    private async Task<Classified> ClassifyAsync(ImportKind kind, CsvTable table, ImportOptions options, CancellationToken cancellationToken)
        => kind switch
        {
            ImportKind.Products => await ClassifyProductsAsync(table, cancellationToken),
            ImportKind.Suppliers => await ClassifySuppliersAsync(table, cancellationToken),
            ImportKind.ProductSuppliers => await ClassifyLinksAsync(table, cancellationToken),
            ImportKind.OpeningStock => await ClassifyStockAsync(table, cancellationToken),
            ImportKind.Boms => await ClassifyBomsAsync(table, options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private async Task<Result<Counts>> ApplyAsync(
        ImportKind kind, ImportOptions options, ImportActor actor, bool skipInvalid, Classified classified, long batchId, CancellationToken cancellationToken)
        => kind switch
        {
            ImportKind.Products => await ApplyProductsAsync(classified, skipInvalid, cancellationToken),
            ImportKind.Suppliers => await ApplySuppliersAsync(classified, skipInvalid, cancellationToken),
            ImportKind.ProductSuppliers => await ApplyLinksAsync(classified, skipInvalid, cancellationToken),
            ImportKind.OpeningStock => await ApplyStockAsync(classified, skipInvalid, actor, batchId, cancellationToken),
            ImportKind.Boms => await ApplyBomsAsync(classified, options, skipInvalid, actor, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    // ---- products ----------------------------------------------------------------------------------------------

    private async Task<Classified> ClassifyProductsAsync(CsvTable table, CancellationToken cancellationToken)
    {
        var existing = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Sku, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var categoryByName = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var barcodeOwner = existing.Values.Where(p => p.Barcode != null).ToDictionary(p => p.Barcode!, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var fileBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>(table.Rows.Count);

        foreach (var csv in table.Rows)
        {
            var sku = csv.Cell("sku");
            var name = csv.Cell("name");
            if (sku.Length == 0)
            {
                rows.Add(Error(csv, "", "SKU is required."));
                continue;
            }

            if (!seen.Add(sku))
            {
                rows.Add(Error(csv, sku, "This SKU appears more than once in the file."));
                continue;
            }

            var errors = new List<string>();
            var active = CsvImport.ParseBool(csv.Cell("active"), out var activeErr);
            if (activeErr is not null) errors.Add(activeErr);
            var reorderPoint = CsvImport.ParseInt(csv.Cell("reorder_point"), out var rpErr) ?? 0;
            if (rpErr is not null) errors.Add(rpErr);
            var reorderQty = CsvImport.ParseInt(csv.Cell("reorder_quantity"), out var rqErr) ?? 0;
            if (rqErr is not null) errors.Add(rqErr);
            var cost = CsvImport.ParseMoneyCents(csv.Cell("unit_cost"), out var costErr) ?? 0;
            if (costErr is not null) errors.Add(costErr);
            if (errors.Count > 0)
            {
                rows.Add(Error(csv, sku, string.Join(" ", errors)));
                continue;
            }

            existing.TryGetValue(sku, out var product);
            if (product is null && name.Length == 0)
            {
                rows.Add(Error(csv, sku, "Name is required for a new part."));
                continue;
            }

            var categoryName = csv.Cell("category");
            categoryByName.TryGetValue(categoryName, out var category);
            var problems = new List<Error>();
            ProductService.ValidateFields(ToProductInput(csv, product, product?.CategoryId), problems);
            errors.AddRange(problems.Select(e => e.Message));
            if (categoryName.Length > Category.NameMaxLength)
            {
                errors.Add($"Category can be at most {Category.NameMaxLength} characters.");
            }

            if (category is { IsActive: false } && category.Id != product?.CategoryId)
            {
                errors.Add($"Category '{category.Name}' is archived. Restore it or pick an active one.");
            }

            var barcode = csv.Cell("barcode");
            if (barcode.Length > 0)
            {
                if (barcodeOwner.TryGetValue(barcode, out var ownerId) && ownerId != product?.Id)
                {
                    errors.Add($"Barcode '{barcode}' is already used by another part (it may be archived).");
                }
                else if (!fileBarcodes.Add(barcode))
                {
                    errors.Add($"Barcode '{barcode}' appears more than once in the file.");
                }
            }

            if (errors.Count > 0)
            {
                rows.Add(Error(csv, sku, string.Join(" ", errors)));
                continue;
            }

            if (product is null)
            {
                rows.Add(new Row(csv.LineNumber, ImportRowAction.Create, sku, name, null, csv));
                continue;
            }

            var unchanged = (name.Length == 0 || name == product.Name)
                && (!csv.HasValue("description") || csv.Cell("description") == (product.Description ?? ""))
                && (!csv.HasValue("unit") || csv.Cell("unit") == product.Unit)
                && (!csv.HasValue("barcode") || csv.Cell("barcode") == (product.Barcode ?? ""))
                && (!csv.HasValue("bin_location") || csv.Cell("bin_location") == (product.BinLocation ?? ""))
                && (!csv.HasValue("reorder_point") || reorderPoint == product.ReorderPoint)
                && (!csv.HasValue("reorder_quantity") || reorderQty == product.ReorderQuantity)
                && (!csv.HasValue("unit_cost") || cost == product.UnitCostCents)
                && (active is null || active == product.IsActive)
                && (categoryName.Length == 0 || (category is not null && category.Id == product.CategoryId));

            rows.Add(new Row(csv.LineNumber, unchanged ? ImportRowAction.Skip : ImportRowAction.Update, sku, product.Name, null, csv));
        }

        return new Classified(rows, [], []);
    }

    private async Task<Result<Counts>> ApplyProductsAsync(Classified classified, bool skipInvalid, CancellationToken cancellationToken)
    {
        var counts = new Counts();
        var categoryByName = (await categories.ListAsync(cancellationToken: cancellationToken))
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var row in Work(classified, skipInvalid, counts))
        {
            db.ChangeTracker.Clear(); // Otherwise every SaveChanges rescans every row so far: 10k parts took minutes.
            var csv = row.Csv!;
            var sku = csv.Cell("sku");
            var existing = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Sku == sku, cancellationToken);

            int? categoryId = existing?.CategoryId;
            var categoryName = csv.Cell("category");
            if (categoryName.Length > 0)
            {
                if (!categoryByName.TryGetValue(categoryName, out var cat))
                {
                    var createdCat = await categories.CreateAsync(new CategoryInput(categoryName, null), cancellationToken);
                    if (!createdCat.Succeeded)
                    {
                        return RowFailure(row, createdCat.Errors);
                    }

                    categoryByName[categoryName] = createdCat.Value;
                    cat = createdCat.Value;
                }

                categoryId = cat.Id;
            }

            if (existing is null)
            {
                var created = await products.CreateAsync(ToProductInput(csv, existing: null, categoryId), cancellationToken);
                if (!created.Succeeded) return RowFailure(row, created.Errors);
                var active = CsvImport.ParseBool(csv.Cell("active"), out _);
                if (active is false)
                {
                    var archived = await products.SetActiveAsync(created.Value.Id, false, cancellationToken: cancellationToken);
                    if (!archived.Succeeded) return RowFailure(row, archived.Errors);
                }

                counts.Created++;
                continue;
            }

            var updated = await products.UpdateAsync(existing.Id, existing.RowVersion, ToProductInput(csv, existing, categoryId), cancellationToken);
            if (!updated.Succeeded) return RowFailure(row, updated.Errors);
            var flag = CsvImport.ParseBool(csv.Cell("active"), out _);
            if (flag is { } isActive && isActive != existing.IsActive)
            {
                var set = await products.SetActiveAsync(existing.Id, isActive, cancellationToken: cancellationToken);
                if (!set.Succeeded) return RowFailure(row, set.Errors);
            }

            counts.Updated++;
        }

        return Result.Success(counts);
    }

    private static ProductInput ToProductInput(CsvDataRow csv, Product? existing, int? categoryId)
        => new(
            existing?.Sku ?? csv.Cell("sku"),
            csv.HasValue("name") || existing is null ? csv.Cell("name") : existing.Name,
            csv.HasValue("description") ? NullIfEmpty(csv.Cell("description")) : existing?.Description,
            categoryId,
            csv.HasValue("unit") || existing is null ? NullIfEmpty(csv.Cell("unit")) : existing.Unit,
            csv.HasValue("barcode") ? NullIfEmpty(csv.Cell("barcode")) : existing?.Barcode,
            csv.HasValue("bin_location") ? NullIfEmpty(csv.Cell("bin_location")) : existing?.BinLocation,
            csv.HasValue("reorder_point") || existing is null ? CsvImport.ParseInt(csv.Cell("reorder_point"), out _) ?? 0 : existing.ReorderPoint,
            csv.HasValue("reorder_quantity") || existing is null ? CsvImport.ParseInt(csv.Cell("reorder_quantity"), out _) ?? 0 : existing.ReorderQuantity,
            csv.HasValue("unit_cost") || existing is null ? CsvImport.ParseMoneyCents(csv.Cell("unit_cost"), out _) ?? 0 : existing.UnitCostCents);

    // ---- suppliers ---------------------------------------------------------------------------------------------

    private async Task<Classified> ClassifySuppliersAsync(CsvTable table, CancellationToken cancellationToken)
    {
        var existing = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>(table.Rows.Count);
        foreach (var csv in table.Rows)
        {
            var name = csv.Cell("name");
            if (name.Length == 0)
            {
                rows.Add(Error(csv, "", "Name is required."));
                continue;
            }

            if (!seen.Add(name))
            {
                rows.Add(Error(csv, name, "This supplier appears more than once in the file."));
                continue;
            }

            var active = CsvImport.ParseBool(csv.Cell("active"), out var activeErr);
            if (activeErr is not null)
            {
                rows.Add(Error(csv, name, activeErr));
                continue;
            }

            existing.TryGetValue(name, out var supplier);
            var problems = SupplierService.ValidateFields(ToSupplierInput(csv, name, supplier));
            if (problems.Count > 0)
            {
                rows.Add(Error(csv, name, Messages(problems)));
                continue;
            }

            if (supplier is null)
            {
                rows.Add(new Row(csv.LineNumber, ImportRowAction.Create, name, name, null, csv));
                continue;
            }

            var unchanged = (!csv.HasValue("contact_name") || csv.Cell("contact_name") == (supplier.ContactName ?? ""))
                && (!csv.HasValue("email") || csv.Cell("email") == (supplier.Email ?? ""))
                && (!csv.HasValue("phone") || csv.Cell("phone") == (supplier.Phone ?? ""))
                && (!csv.HasValue("address") || csv.Cell("address") == (supplier.Address ?? ""))
                && (!csv.HasValue("website") || csv.Cell("website") == (supplier.Website ?? ""))
                && (!csv.HasValue("notes") || csv.Cell("notes") == (supplier.Notes ?? ""))
                && (active is null || active == supplier.IsActive);
            rows.Add(new Row(csv.LineNumber, unchanged ? ImportRowAction.Skip : ImportRowAction.Update, name, name, null, csv));
        }

        return new Classified(rows, [], []);
    }

    private async Task<Result<Counts>> ApplySuppliersAsync(Classified classified, bool skipInvalid, CancellationToken cancellationToken)
    {
        var counts = new Counts();
        foreach (var row in Work(classified, skipInvalid, counts))
        {
            db.ChangeTracker.Clear();
            var csv = row.Csv!;
            var name = csv.Cell("name");
            var existing = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name, cancellationToken);
            var input = ToSupplierInput(csv, name, existing);

            if (existing is null)
            {
                var created = await suppliers.CreateAsync(input, cancellationToken);
                if (!created.Succeeded) return RowFailure(row, created.Errors);
                var active = CsvImport.ParseBool(csv.Cell("active"), out _);
                if (active is false)
                {
                    var archived = await suppliers.SetActiveAsync(created.Value.Id, false, cancellationToken: cancellationToken);
                    if (!archived.Succeeded) return RowFailure(row, archived.Errors);
                }

                counts.Created++;
                continue;
            }

            var updated = await suppliers.UpdateAsync(existing.Id, existing.RowVersion, input, cancellationToken);
            if (!updated.Succeeded) return RowFailure(row, updated.Errors);
            var flag = CsvImport.ParseBool(csv.Cell("active"), out _);
            if (flag is { } isActive && isActive != existing.IsActive)
            {
                var set = await suppliers.SetActiveAsync(existing.Id, isActive, cancellationToken: cancellationToken);
                if (!set.Succeeded) return RowFailure(row, set.Errors);
            }

            counts.Updated++;
        }

        return Result.Success(counts);
    }

    private static SupplierInput ToSupplierInput(CsvDataRow csv, string name, Supplier? existing)
        => new(
            name,
            csv.HasValue("contact_name") || existing is null ? NullIfEmpty(csv.Cell("contact_name")) : existing.ContactName,
            csv.HasValue("email") || existing is null ? NullIfEmpty(csv.Cell("email")) : existing.Email,
            csv.HasValue("phone") || existing is null ? NullIfEmpty(csv.Cell("phone")) : existing.Phone,
            csv.HasValue("address") || existing is null ? NullIfEmpty(csv.Cell("address")) : existing.Address,
            csv.HasValue("website") || existing is null ? NullIfEmpty(csv.Cell("website")) : existing.Website,
            csv.HasValue("notes") || existing is null ? NullIfEmpty(csv.Cell("notes")) : existing.Notes);

    // ---- links -------------------------------------------------------------------------------------------------

    private async Task<Classified> ClassifyLinksAsync(CsvTable table, CancellationToken cancellationToken)
    {
        var productBySku = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Sku, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var supplierByName = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var links = await db.ProductSuppliers.AsNoTracking().ToDictionaryAsync(l => (l.ProductId, l.SupplierId), cancellationToken);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>(table.Rows.Count);

        foreach (var csv in table.Rows)
        {
            var sku = csv.Cell("sku");
            var supplierName = csv.Cell("supplier");
            var key = sku + " / " + supplierName;
            if (sku.Length == 0 || supplierName.Length == 0)
            {
                rows.Add(Error(csv, key, "SKU and supplier are required."));
                continue;
            }

            if (!seen.Add(key))
            {
                rows.Add(Error(csv, key, "This part–supplier pair appears more than once in the file."));
                continue;
            }

            if (!productBySku.TryGetValue(sku, out var product))
            {
                rows.Add(Error(csv, key, $"No part with SKU '{sku}'."));
                continue;
            }

            if (!supplierByName.TryGetValue(supplierName, out var supplier))
            {
                rows.Add(Error(csv, key, $"No supplier named '{supplierName}'."));
                continue;
            }

            var preferred = CsvImport.ParseBool(csv.Cell("preferred"), out var prefErr);
            var cost = CsvImport.ParseMoneyCents(csv.Cell("unit_cost"), out var costErr);
            var lead = CsvImport.ParseInt(csv.Cell("lead_time_days"), out var leadErr);
            var min = CsvImport.ParseInt(csv.Cell("min_order_quantity"), out var minErr, min: 1);
            var err = prefErr ?? costErr ?? leadErr ?? minErr;
            if (err is not null)
            {
                rows.Add(Error(csv, key, err));
                continue;
            }

            links.TryGetValue((product.Id, supplier.Id), out var link);
            var input = ToLinkInput(csv, link is null ? null : new LinkValues(link.SupplierSku, link.UnitCostCents, link.LeadTimeDays, link.MinOrderQuantity, link.IsPreferred));
            var problems = SupplierService.ValidateLink(input);
            if (link is null && !product.IsActive)
            {
                problems.Add(new Error("supplier.link_product_archived", "That part is archived. Restore it before adding suppliers."));
            }

            if (!supplier.IsActive && (link is null || (input.IsPreferred && !link.IsPreferred)))
            {
                problems.Add(new Error("supplier.link_supplier_archived", "That supplier is archived. Restore it before linking parts or marking it preferred."));
            }

            if (problems.Count > 0)
            {
                rows.Add(Error(csv, key, Messages(problems)));
                continue;
            }

            if (link is null)
            {
                rows.Add(new Row(csv.LineNumber, ImportRowAction.Create, key, key, null, csv));
                continue;
            }

            var unchanged = (!csv.HasValue("supplier_sku") || csv.Cell("supplier_sku") == (link.SupplierSku ?? ""))
                && (!csv.HasValue("unit_cost") || cost == link.UnitCostCents)
                && (!csv.HasValue("lead_time_days") || lead == link.LeadTimeDays)
                && (!csv.HasValue("min_order_quantity") || min == link.MinOrderQuantity)
                && (preferred is null || preferred == link.IsPreferred);
            rows.Add(new Row(csv.LineNumber, unchanged ? ImportRowAction.Skip : ImportRowAction.Update, key, key, null, csv));
        }

        return new Classified(rows, [], []);
    }

    private async Task<Result<Counts>> ApplyLinksAsync(Classified classified, bool skipInvalid, CancellationToken cancellationToken)
    {
        var counts = new Counts();
        foreach (var row in Work(classified, skipInvalid, counts))
        {
            db.ChangeTracker.Clear();
            var csv = row.Csv!;
            var product = await db.Products.AsNoTracking().FirstAsync(p => p.Sku == csv.Cell("sku"), cancellationToken);
            var supplier = await db.Suppliers.AsNoTracking().FirstAsync(s => s.Name == csv.Cell("supplier"), cancellationToken);
            var existing = await suppliers.GetLinkAsync(product.Id, supplier.Id, cancellationToken);
            var input = ToLinkInput(csv, existing is null ? null : new LinkValues(existing.SupplierSku, existing.UnitCostCents, existing.LeadTimeDays, existing.MinOrderQuantity, existing.IsPreferred));

            var result = await suppliers.LinkProductAsync(product.Id, supplier.Id, input, cancellationToken);
            if (!result.Succeeded) return RowFailure(row, result.Errors);
            if (existing is null) counts.Created++; else counts.Updated++;
        }

        return Result.Success(counts);
    }

    private static SupplierLinkInput ToLinkInput(CsvDataRow csv, LinkValues? existing)
        => new(
            csv.HasValue("supplier_sku") || existing is null ? NullIfEmpty(csv.Cell("supplier_sku")) : existing.SupplierSku,
            csv.HasValue("unit_cost") || existing is null ? CsvImport.ParseMoneyCents(csv.Cell("unit_cost"), out _) ?? 0 : existing.UnitCostCents,
            csv.HasValue("lead_time_days") || existing is null ? CsvImport.ParseInt(csv.Cell("lead_time_days"), out _) : existing.LeadTimeDays,
            csv.HasValue("min_order_quantity") || existing is null ? CsvImport.ParseInt(csv.Cell("min_order_quantity"), out _, min: 1) ?? 1 : existing.MinOrderQuantity,
            csv.HasValue("preferred") || existing is null ? CsvImport.ParseBool(csv.Cell("preferred"), out _) ?? false : existing.IsPreferred);

    // ---- opening stock -----------------------------------------------------------------------------------------

    private async Task<Classified> ClassifyStockAsync(CsvTable table, CancellationToken cancellationToken)
    {
        var productBySku = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Sku, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>(table.Rows.Count);
        foreach (var csv in table.Rows)
        {
            var sku = csv.Cell("sku");
            if (sku.Length == 0)
            {
                rows.Add(Error(csv, "", "SKU is required."));
                continue;
            }

            if (!seen.Add(sku))
            {
                rows.Add(Error(csv, sku, "This SKU appears more than once in the file."));
                continue;
            }

            if (!productBySku.TryGetValue(sku, out var product))
            {
                rows.Add(Error(csv, sku, $"No part with SKU '{sku}'."));
                continue;
            }

            var qty = CsvImport.ParseInt(csv.Cell("quantity"), out var qtyErr);
            if (qtyErr is not null || qty is null)
            {
                rows.Add(Error(csv, sku, qtyErr ?? "Quantity is required."));
                continue;
            }

            if (qty > StockService.MaxQuantity)
            {
                rows.Add(Error(csv, sku, $"Quantities cannot exceed {StockService.MaxQuantity:N0}."));
                continue;
            }

            if (csv.Cell("note").Length > StockTransaction.NoteMaxLength)
            {
                rows.Add(Error(csv, sku, $"Note can be at most {StockTransaction.NoteMaxLength} characters."));
                continue;
            }

            if (qty == product.QuantityOnHand)
            {
                rows.Add(new Row(csv.LineNumber, ImportRowAction.Skip, sku, $"{product.Name}: {product.QuantityOnHand} → {qty}", null, csv));
                continue;
            }

            if (!product.IsActive)
            {
                rows.Add(Error(csv, sku, $"{product.Sku} is archived. Restore it before recording stock for it."));
                continue;
            }

            rows.Add(new Row(csv.LineNumber, ImportRowAction.Update, sku, $"{product.Name}: {product.QuantityOnHand} → {qty}", null, csv));
        }

        return new Classified(rows, [], []);
    }

    private async Task<Result<Counts>> ApplyStockAsync(Classified classified, bool skipInvalid, ImportActor actor, long batchId, CancellationToken cancellationToken)
    {
        var counts = new Counts();
        var lines = new List<StockMovement>();
        foreach (var row in Work(classified, skipInvalid, counts))
        {
            var csv = row.Csv!;
            var product = await db.Products.AsNoTracking().FirstAsync(p => p.Sku == csv.Cell("sku"), cancellationToken);
            var qty = CsvImport.ParseInt(csv.Cell("quantity"), out _)!.Value;
            lines.Add(new StockMovement(product.Id, StockTransactionType.Count, qty, Note: NullIfEmpty(csv.Cell("note")), ImportBatchId: batchId));
            counts.Updated++;
        }

        if (lines.Count == 0)
        {
            return Result.Success(counts);
        }

        var posted = await stock.PostAsync(lines, new StockContext(actor.UserId), cancellationToken);
        return posted.Succeeded ? Result.Success(counts) : Result.Failure<Counts>([.. posted.Errors.Select(e => new Error(e.Code, e.Message))]);
    }

    // ---- BOMs --------------------------------------------------------------------------------------------------

    private async Task<Classified> ClassifyBomsAsync(CsvTable table, ImportOptions options, CancellationToken cancellationToken)
    {
        var existingProjects = await db.Projects.AsNoTracking().Include(p => p.Lines).ThenInclude(l => l.Product).ToListAsync(cancellationToken);
        var catalog = new BomCatalog(
            await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Sku, StringComparer.OrdinalIgnoreCase, cancellationToken),
            existingProjects.ToDictionary(p => p.Number, StringComparer.OrdinalIgnoreCase),
            existingProjects.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            options);

        var groups = new Dictionary<string, List<CsvDataRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var csv in table.Rows)
        {
            var number = csv.Cell("project_number");
            var key = number.Length > 0 ? "n:" + number : "b:" + csv.Cell("project_name");
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }

            list.Add(csv);
        }

        var rows = new List<Row>(table.Rows.Count);
        var replacements = new List<BomReplacePreview>();
        var headerChanges = new List<BomHeaderChangePreview>();

        foreach (var (groupKey, list) in groups)
        {
            var number = list[0].Cell("project_number");
            var name = list[0].Cell("project_name");
            var label = number.Length > 0 && name.Length > 0 ? $"{number} — {name}" : number.Length > 0 ? number : name.Length > 0 ? name : "(no project name)";
            var groupRows = ClassifyBomGroup(list, catalog, replacements, headerChanges);
            rows.AddRange(groupRows.Select(r => r with { ProjectKey = groupKey, ProjectLabel = label }));
        }

        return new Classified(rows, replacements, headerChanges);
    }

    private static List<Row> ClassifyBomGroup(
        List<CsvDataRow> list, BomCatalog catalog, List<BomReplacePreview> replacements, List<BomHeaderChangePreview> headerChanges)
    {
        var first = list[0];
        var number = first.Cell("project_number");
        var name = first.Cell("project_name");
        var projectKey = number.Length > 0 ? number : name;
        List<Row> FailAll(string message) => list.Select(csv => Error(csv, projectKey, message)).ToList();

        if (name.Length == 0)
        {
            return FailAll("Project name is required.");
        }

        var target = CsvImport.ParseInt(first.Cell("target_units"), out var targetErr, min: 1);
        if (targetErr is not null)
        {
            return FailAll(targetErr);
        }

        Project? existing = number.Length > 0 && catalog.ProjectByNumber.TryGetValue(number, out var found) ? found : null;
        var projectErrors = ProjectService.ValidateProject(name, NullIfEmpty(first.Cell("description")), target ?? existing?.TargetUnits ?? 1, NullIfEmpty(first.Cell("notes")));
        if (number.Length > Project.NumberMaxLength)
        {
            projectErrors.Add(new Error("project.number_too_long", $"BOM number can be at most {Project.NumberMaxLength} characters."));
        }

        if (projectErrors.Count > 0)
        {
            return FailAll(Messages(projectErrors));
        }

        if (catalog.ProjectByName.TryGetValue(name, out var nameOwner) && nameOwner.Id != existing?.Id)
        {
            return FailAll($"A BOM named \"{name}\" already exists ({nameOwner.Number}).");
        }

        if (!catalog.ClaimedNames.Add(name))
        {
            return FailAll($"Another project in this file is also named \"{name}\".");
        }

        var changes = existing is null ? [] : DescribeHeaderChanges(existing, first, name, target);
        var headerErrors = HeaderDisagreements(list);
        var rows = new List<Row>(list.Count);
        var fileSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupHasError = false;

        foreach (var csv in list)
        {
            var sku = csv.Cell("sku");
            var qtyText = csv.Cell("quantity_per_unit");

            void Fail(string key, string message)
            {
                rows.Add(Error(csv, key, message));
                groupHasError = true;
            }

            if (headerErrors.TryGetValue(csv.LineNumber, out var disagree))
            {
                Fail(projectKey, disagree);
                continue;
            }

            if (sku.Length == 0 && qtyText.Length == 0)
            {
                var action = existing is null ? ImportRowAction.Create : changes.Count > 0 ? ImportRowAction.Update : ImportRowAction.Skip;
                rows.Add(new Row(csv.LineNumber, action, projectKey, name, null, csv));
                continue;
            }

            if (sku.Length == 0 || qtyText.Length == 0)
            {
                Fail(projectKey, "SKU and quantity_per_unit are required together.");
                continue;
            }

            var qty = CsvImport.ParseInt(qtyText, out var qtyErr, min: 1);
            if (qtyErr is not null || qty is null)
            {
                Fail(sku, qtyErr ?? "Quantity per unit is required.");
                continue;
            }

            if (!fileSkus.Add(sku))
            {
                Fail(sku, "This part appears more than once on this BOM.");
                continue;
            }

            var lineErrors = new List<Error>();
            ProjectService.ValidateLine(qty.Value, NullIfEmpty(csv.Cell("note")), lineErrors);
            if (lineErrors.Count > 0)
            {
                Fail(sku, Messages(lineErrors));
                continue;
            }

            if (!catalog.ProductBySku.TryGetValue(sku, out var product))
            {
                if (!catalog.Options.CreateMissingParts)
                {
                    Fail(sku, $"No part with SKU '{sku}'.");
                    continue;
                }

                if (csv.Cell("part_name").Length == 0)
                {
                    Fail(sku, "part_name is required to create a missing part.");
                    continue;
                }

                var partCost = CsvImport.ParseMoneyCents(csv.Cell("part_unit_cost"), out var partCostErr);
                if (partCostErr is not null)
                {
                    Fail(sku, partCostErr);
                    continue;
                }

                var partErrors = new List<Error>();
                ProductService.ValidateFields(
                    new ProductInput(sku, csv.Cell("part_name"), null, null, NullIfEmpty(csv.Cell("part_unit")), null, null, 0, 0, partCost ?? 0), partErrors);
                if (partErrors.Count > 0)
                {
                    Fail(sku, Messages(partErrors));
                    continue;
                }

                rows.Add(new Row(csv.LineNumber, ImportRowAction.Create, sku, $"{name}: new part {sku}", null, csv));
                continue;
            }

            var line = existing?.Lines.FirstOrDefault(l => l.Product != null && string.Equals(l.Product.Sku, sku, StringComparison.OrdinalIgnoreCase));
            if (line is null)
            {
                if (!product.IsActive)
                {
                    Fail(sku, $"{product.Sku} is archived. Restore it before adding it to a BOM.");
                    continue;
                }

                rows.Add(new Row(csv.LineNumber, ImportRowAction.Create, sku, $"{name}: add {sku}", null, csv));
                continue;
            }

            var unchanged = line.QuantityPerUnit == qty && (csv.Cell("note") == (line.Note ?? "") || !csv.HasValue("note"));
            rows.Add(new Row(csv.LineNumber, unchanged ? ImportRowAction.Skip : ImportRowAction.Update, sku, $"{name}: {sku}", null, csv));
        }

        if (existing is not null && changes.Count > 0 && rows.Any(r => r.Action != ImportRowAction.Error))
        {
            headerChanges.Add(new BomHeaderChangePreview(existing.Number, existing.Name, changes));
        }

        if (catalog.Options.Mode == BomImportMode.Replace && existing is not null && !groupHasError)
        {
            var removed = existing.Lines.Where(l => l.Product != null && !fileSkus.Contains(l.Product.Sku)).Select(l => l.Product!.Sku).ToList();
            if (removed.Count > 0)
            {
                replacements.Add(new BomReplacePreview(existing.Number, existing.Name, removed.Count, removed));
            }
        }

        return rows;
    }

    private static List<string> DescribeHeaderChanges(Project existing, CsvDataRow first, string name, int? target)
    {
        var changes = new List<string>();
        if (name != existing.Name)
        {
            changes.Add($"name “{existing.Name}” to “{name}”");
        }

        if (target is { } units && units != existing.TargetUnits)
        {
            changes.Add($"target units {existing.TargetUnits} to {units}");
        }

        if (first.HasValue("description") && first.Cell("description") != (existing.Description ?? ""))
        {
            changes.Add("description");
        }

        if (first.HasValue("notes") && first.Cell("notes") != (existing.Notes ?? ""))
        {
            changes.Add("notes");
        }

        return changes;
    }

    private async Task<Result<Counts>> ApplyBomsAsync(Classified classified, ImportOptions options, bool skipInvalid, ImportActor actor, CancellationToken cancellationToken)
    {
        var counts = new Counts();
        var projectActor = new ProjectActor(actor.UserId, actor.IsAdmin);

        foreach (var group in classified.Rows.GroupBy(r => r.ProjectKey!, StringComparer.OrdinalIgnoreCase))
        {
            db.ChangeTracker.Clear();
            var errorRows = group.Where(r => r.Action == ImportRowAction.Error).ToList();
            if (errorRows.Count > 0 && options.Mode == BomImportMode.Replace)
            {
                // The file cannot be "the BOM" of a project with rows missing: leave that project untouched.
                counts.Skipped += group.Count();
                continue;
            }

            var work = group.Where(r => r.Action != ImportRowAction.Error).ToList();
            counts.Skipped += errorRows.Count;
            if (work.Count == 0)
            {
                continue;
            }

            var first = work[0].Csv!;
            var number = first.Cell("project_number");
            var name = first.Cell("project_name");
            var existing = number.Length == 0
                ? null
                : await db.Projects.Include(p => p.Lines).ThenInclude(l => l.Product)
                    .FirstOrDefaultAsync(p => p.Number == number, cancellationToken);

            if (existing is null)
            {
                var target = CsvImport.ParseInt(first.Cell("target_units"), out _, min: 1) ?? 1;
                var created = await projects.CreateAsync(
                    new ProjectInput(name, NullIfEmpty(number), NullIfEmpty(first.Cell("description")), target, NullIfEmpty(first.Cell("notes"))),
                    projectActor,
                    cancellationToken);
                if (!created.Succeeded) return RowFailure(work[0], created.Errors);
                existing = await db.Projects.Include(p => p.Lines).ThenInclude(l => l.Product).FirstAsync(p => p.Id == created.Value.Id, cancellationToken);
                counts.Created++;
            }
            else
            {
                var target = CsvImport.ParseInt(first.Cell("target_units"), out _, min: 1) ?? existing.TargetUnits;
                var input = new ProjectInput(
                    name,
                    existing.Number,
                    first.HasValue("description") ? NullIfEmpty(first.Cell("description")) : existing.Description,
                    first.HasValue("target_units") ? target : existing.TargetUnits,
                    first.HasValue("notes") ? NullIfEmpty(first.Cell("notes")) : existing.Notes);
                var headerChanged = input.Name != existing.Name || input.Description != existing.Description
                    || input.TargetUnits != existing.TargetUnits || input.Notes != existing.Notes;
                if (headerChanged)
                {
                    var updated = await projects.UpdateAsync(existing.Id, existing.RowVersion, input, cancellationToken);
                    if (!updated.Succeeded) return RowFailure(work[0], updated.Errors);
                    existing.RowVersion = updated.Value.RowVersion;
                    counts.Updated++;
                }
            }

            var fileSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in work)
            {
                var csv = row.Csv!;
                var sku = csv.Cell("sku");
                if (sku.Length == 0)
                {
                    if (row.Action == ImportRowAction.Skip) counts.Skipped++;
                    continue;
                }

                fileSkus.Add(sku);
                var qty = CsvImport.ParseInt(csv.Cell("quantity_per_unit"), out _, min: 1)!.Value;
                var note = csv.HasValue("note") ? NullIfEmpty(csv.Cell("note")) : null;
                var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Sku == sku, cancellationToken);
                if (product is null)
                {
                    var added = await projects.CreatePartAndAddAsync(
                        existing.Id,
                        new NewPartInput(sku, csv.Cell("part_name"), NullIfEmpty(csv.Cell("part_unit")), CsvImport.ParseMoneyCents(csv.Cell("part_unit_cost"), out _) ?? 0, note),
                        qty,
                        projectActor,
                        cancellationToken);
                    if (!added.Succeeded) return RowFailure(row, added.Errors);
                    counts.Created++;
                    continue;
                }

                var line = existing.Lines.FirstOrDefault(l => l.ProductId == product.Id);
                if (line is null)
                {
                    var added = await projects.AddLineAsync(existing.Id, new BomLineInput(product.Id, qty, note), projectActor, cancellationToken);
                    if (!added.Succeeded) return RowFailure(row, added.Errors);
                    counts.Created++;
                    continue;
                }

                if (row.Action == ImportRowAction.Skip)
                {
                    counts.Skipped++;
                    continue;
                }

                var lineNote = csv.HasValue("note") ? note : line.Note;
                var updatedLine = await projects.UpdateLineAsync(existing.Id, line.Id, qty, lineNote, projectActor, cancellationToken);
                if (!updatedLine.Succeeded) return RowFailure(row, updatedLine.Errors);
                counts.Updated++;
            }

            if (options.Mode == BomImportMode.Replace)
            {
                existing = await db.Projects.Include(p => p.Lines).ThenInclude(l => l.Product).FirstAsync(p => p.Id == existing.Id, cancellationToken);
                foreach (var line in existing.Lines.Where(l => l.Product != null && !fileSkus.Contains(l.Product.Sku)).ToList())
                {
                    var removed = await projects.RemoveLineAsync(existing.Id, line.Id, projectActor, cancellationToken);
                    if (!removed.Succeeded) return RowFailure(work[0], removed.Errors);
                    counts.Updated++;
                }
            }
        }

        return Result.Success(counts);
    }

    private static Dictionary<int, string> HeaderDisagreements(List<CsvDataRow> rows)
    {
        var first = rows[0];
        var errors = new Dictionary<int, string>();
        foreach (var csv in rows.Skip(1))
        {
            foreach (var field in new[] { "project_name", "target_units", "description", "notes" })
            {
                if (csv.Cell(field) != first.Cell(field))
                {
                    errors[csv.LineNumber] = $"Project field '{field}' disagrees with the first row of this project.";
                    break;
                }
            }
        }

        return errors;
    }

    // ---- shared ------------------------------------------------------------------------------------------------

    private static ImportPreview ToPreview(string token, ImportKind kind, string fileName, ImportOptions options, Classified classified)
        => new(
            token, kind, fileName, options, classified.Rows.Count,
            classified.Rows.Count(r => r.Action == ImportRowAction.Create),
            classified.Rows.Count(r => r.Action == ImportRowAction.Update),
            classified.Rows.Count(r => r.Action == ImportRowAction.Skip),
            classified.Rows.Count(r => r.Action == ImportRowAction.Error),
            classified.Rows.Select(r => new ImportPreviewRow(r.LineNumber, r.Action, r.Key, r.Summary, r.Error, r.ProjectLabel)).ToList(),
            classified.Replacements,
            classified.HeaderChanges);

    private static IEnumerable<Row> Work(Classified classified, bool skipInvalid, Counts counts)
    {
        foreach (var row in classified.Rows)
        {
            if (row.Action == ImportRowAction.Error)
            {
                if (skipInvalid) counts.Skipped++;
                continue;
            }

            if (row.Action == ImportRowAction.Skip)
            {
                counts.Skipped++;
                continue;
            }

            yield return row;
        }
    }

    private static Row Error(CsvDataRow csv, string key, string message)
        => new(csv.LineNumber, ImportRowAction.Error, key, key, message, csv);

    /// <summary>A service refused a row that passed the preview: says which row, and drops the form field the service named.</summary>
    private static Result<Counts> RowFailure(Row row, IEnumerable<Error> errors)
        => Result.Failure<Counts>([.. errors.Select(e => new Error(e.Code, $"Row {row.LineNumber}: {e.Message}"))]);

    private static string Messages(IEnumerable<Error> errors) => string.Join(" ", errors.Select(e => e.Message));

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private sealed record Classified(IReadOnlyList<Row> Rows, IReadOnlyList<BomReplacePreview> Replacements, IReadOnlyList<BomHeaderChangePreview> HeaderChanges);

    private sealed record Row(int LineNumber, ImportRowAction Action, string Key, string Summary, string? Error, CsvDataRow? Csv, string? ProjectKey = null, string? ProjectLabel = null);

    private sealed record LinkValues(string? SupplierSku, long UnitCostCents, int? LeadTimeDays, int MinOrderQuantity, bool IsPreferred);

    private sealed record BomCatalog(
        Dictionary<string, Product> ProductBySku,
        Dictionary<string, Project> ProjectByNumber,
        Dictionary<string, Project> ProjectByName,
        HashSet<string> ClaimedNames,
        ImportOptions Options);

    private sealed class Counts
    {
        public int Created;
        public int Updated;
        public int Skipped;
    }
}
