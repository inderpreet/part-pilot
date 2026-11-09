using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace InventoryTracker.Tests.Services;

public abstract class ImportTestBase : ProjectTestBase
{
    protected ImportStaging Staging { get; }

    protected ImportTestBase() => Staging = new ImportStaging(Clock);

    protected RecordingImportEvents ImportEvents { get; } = new();

    protected ImportActor Importer => new(UserId, IsAdmin: true);

    protected (AppDbContext Context, ImportService Imports, ExportService Exports, BackupService Backup) NewTransfer()
    {
        var (context, projects, _) = NewProjects();
        var products = new ProductService(context);
        var cats = new CategoryService(context);
        var supplierService = new SupplierService(context);
        var stockService = new StockService(context, Settings, Clock, NullLogger<StockService>.Instance);
        var imports = new ImportService(
            context, products, cats, supplierService, stockService, projects, Staging, Clock,
            new ImportEventPublisher([ImportEvents], NullLogger<ImportEventPublisher>.Instance));
        var exports = new ExportService(context);
        var backup = new BackupService(context, Clock, new BackupEventPublisher([], NullLogger<BackupEventPublisher>.Instance));
        return (context, imports, exports, backup);
    }

    protected static MemoryStream CsvStream(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.Replace("\n", "\r\n", StringComparison.Ordinal));
        return new MemoryStream(bytes);
    }

    protected async Task<ImportPreview> PreviewAsync(ImportKind kind, string csv, ImportOptions? options = null)
    {
        var (context, imports, _, _) = NewTransfer();
        await using (context)
        {
            var result = await imports.PreviewAsync(kind, CsvStream(csv), "upload.csv", options ?? new ImportOptions(), Importer);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            return result.Value;
        }
    }

    protected async Task<Result<ImportBatchResult>> CommitAsync(string token, bool skipInvalid = false)
    {
        var (context, imports, _, _) = NewTransfer();
        await using (context)
        {
            return await imports.CommitAsync(token, Importer, skipInvalid);
        }
    }

    protected static string ProductsCsv(params string[] rows)
        => "sku,name,description,category,unit,barcode,bin_location,reorder_point,reorder_quantity,unit_cost,active\n"
           + string.Join('\n', rows);

    protected static string BomCsv(params string[] rows)
        => "project_number,project_name,target_units,description,notes,sku,quantity_per_unit,note,part_name,part_unit,part_unit_cost\n"
           + string.Join('\n', rows);
}

public sealed class RecordingImportEvents : IImportEvents
{
    public List<ImportCommittedEvent> Events { get; } = [];

    public Task HandleAsync(ImportCommittedEvent importEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(importEvent);
        return Task.CompletedTask;
    }
}
