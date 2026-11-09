using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

public sealed class ImportServiceTests : ImportTestBase
{
    [Fact]
    public async Task Products_create_and_update_by_sku()
    {
        await CreateProductAsync(Input(sku: "WID-1", name: "Old", costCents: 100));

        var preview = await PreviewAsync(ImportKind.Products, ProductsCsv(
            "WID-1,Widget,,Hardware,each,,,5,10,12.50,true",
            "NEW-1,Gadget,,Hardware,each,,,0,0,1.00,true"));

        Assert.Equal(ImportRowAction.Update, preview.Rows[0].Action);
        Assert.Equal(ImportRowAction.Create, preview.Rows[1].Action);

        Assert.True((await CommitAsync(preview.Token)).Succeeded);

        await using var context = Db.CreateContext();
        var old = await context.Products.SingleAsync(p => p.Sku == "WID-1");
        var created = await context.Products.SingleAsync(p => p.Sku == "NEW-1");
        Assert.Equal("Widget", old.Name);
        Assert.Equal(1250, old.UnitCostCents);
        Assert.Equal("Gadget", created.Name);
        Assert.NotNull(await context.Categories.SingleOrDefaultAsync(c => c.Name == "Hardware"));
        Assert.Single(ImportEvents.Events);
    }

    [Fact]
    public async Task Error_rows_block_commit_unless_skip_invalid_is_set()
    {
        var preview = await PreviewAsync(ImportKind.Products, ProductsCsv(
            "OK-1,Good,,,,,,0,0,1.00,true",
            ",Missing sku,,,,,,0,0,1.00,true"));

        Assert.Equal(1, preview.ErrorCount);
        var blocked = await CommitAsync(preview.Token);
        Assert.False(blocked.Succeeded);

        var preview2 = await PreviewAsync(ImportKind.Products, ProductsCsv(
            "OK-1,Good,,,,,,0,0,1.00,true",
            ",Missing sku,,,,,,0,0,1.00,true"));
        var committed = await CommitAsync(preview2.Token, skipInvalid: true);
        Assert.True(committed.Succeeded, string.Join("; ", committed.Errors.Select(e => e.Message)));
        Assert.Equal(1, committed.Value.CreatedCount);
        Assert.Equal(1, committed.Value.SkippedCount);

        await using var context = Db.CreateContext();
        Assert.Equal(1, await context.Products.CountAsync(p => p.Sku == "OK-1"));
    }

    [Fact]
    public async Task Duplicate_keys_in_the_file_are_errors()
    {
        var preview = await PreviewAsync(ImportKind.Products, ProductsCsv(
            "DUP-1,A,,,,,,0,0,1.00,true",
            "DUP-1,B,,,,,,0,0,1.00,true"));
        Assert.Equal(ImportRowAction.Error, preview.Rows[1].Action);
        Assert.Contains("more than once", preview.Rows[1].Error);
    }

    [Fact]
    public async Task Opening_stock_posts_count_transactions_tied_to_the_batch()
    {
        await CreateProductAsync(Input(sku: "STK-1", name: "Stocked"));
        var csv = "sku,quantity,note\nSTK-1,40,Opening count\n";
        var preview = await PreviewAsync(ImportKind.OpeningStock, csv);
        Assert.Equal(ImportRowAction.Update, preview.Rows[0].Action);

        var committed = await CommitAsync(preview.Token);
        Assert.True(committed.Succeeded, string.Join("; ", committed.Errors.Select(e => e.Message)));

        await using var context = Db.CreateContext();
        var product = await context.Products.SingleAsync(p => p.Sku == "STK-1");
        Assert.Equal(40, product.QuantityOnHand);
        var tx = await context.StockTransactions.SingleAsync();
        Assert.Equal(committed.Value.Id, tx.ImportBatchId);
        Assert.Equal(StockTransactionType.Count, tx.Type);
    }

    [Fact]
    public async Task Bom_merge_adds_and_replace_removes_missing_lines()
    {
        var a = await CreateProductAsync(Input(sku: "MCU-1", name: "MCU"));
        var b = await CreateProductAsync(Input(sku: "CAP-1", name: "Cap"));
        var extra = await CreateProductAsync(Input(sku: "RES-1", name: "Res"));
        var project = await NewProjectAsync("Robot", number: "PRJ-1");
        await AddPartAsync(project.Id, extra.Id, 2);

        var csv = BomCsv(
            "PRJ-1,Robot,1,,,MCU-1,1,controller,Microcontroller,each,4.20",
            "PRJ-1,Robot,1,,,CAP-1,4,,100nF capacitor,each,0.02");

        var merge = await PreviewAsync(ImportKind.Boms, csv, new ImportOptions(BomImportMode.Merge));
        Assert.DoesNotContain(merge.Rows, r => r.Action == ImportRowAction.Error);
        Assert.True((await CommitAsync(merge.Token)).Succeeded);

        var afterMerge = await DetailAsync(project.Id);
        Assert.Equal(3, afterMerge.Lines.Count);

        var replacePreview = await PreviewAsync(ImportKind.Boms, csv, new ImportOptions(BomImportMode.Replace));
        Assert.Contains(replacePreview.Replacements, r => r.RemovedCount == 1 && r.RemovedSkus.Contains("RES-1"));
        Assert.True((await CommitAsync(replacePreview.Token)).Succeeded);

        var afterReplace = await DetailAsync(project.Id);
        Assert.Equal(2, afterReplace.Lines.Count);
        Assert.DoesNotContain(afterReplace.Lines, l => l.Sku == "RES-1");
    }

    [Fact]
    public async Task Bom_unknown_sku_is_an_error_unless_create_missing_parts_is_set()
    {
        var csv = BomCsv(",Arm,1,,,NEW-MCU,1,,Microcontroller,each,4.20");
        var blocked = await PreviewAsync(ImportKind.Boms, csv);
        Assert.Equal(ImportRowAction.Error, blocked.Rows[0].Action);

        var preview = await PreviewAsync(ImportKind.Boms, csv, new ImportOptions(CreateMissingParts: true));
        Assert.Equal(ImportRowAction.Create, preview.Rows[0].Action);
        Assert.True((await CommitAsync(preview.Token)).Succeeded);

        await using var context = Db.CreateContext();
        Assert.True(await context.Products.AnyAsync(p => p.Sku == "NEW-MCU"));
        Assert.True(await context.Projects.AnyAsync(p => p.Name == "Arm"));
    }

    [Fact]
    public async Task Failed_commit_leaves_the_database_unchanged()
    {
        var tooLong = new string('x', 300);
        var preview = await PreviewAsync(ImportKind.Products, ProductsCsv(
            "A-1,First,,,,,,0,0,1.00,true",
            $"B-1,{tooLong},,,,,,0,0,1.00,true"));

        var result = await CommitAsync(preview.Token);
        Assert.False(result.Succeeded);

        await using var context = Db.CreateContext();
        Assert.False(await context.Products.AnyAsync(p => p.Sku == "A-1"));
        Assert.Equal(0, await context.ImportBatches.CountAsync());
    }

    [Fact]
    public async Task Bom_export_then_import_is_a_no_op()
    {
        var mcu = await CreateProductAsync(Input(sku: "MCU-1", name: "MCU", costCents: 420, unit: "each"));
        var project = await NewProjectAsync("Robot", number: "PRJ-2026-0001");
        await AddPartAsync(project.Id, mcu.Id, 1, "controller");

        var (exportContext, _, exports, _) = NewTransfer();
        await using (exportContext)
        {
            await using var stream = new MemoryStream();
            await exports.WriteBomsAsync(stream, new BomExportQuery());
            var csv = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            Assert.Contains("MCU-1", csv);

            var preview = await PreviewAsync(ImportKind.Boms, csv.TrimStart('\uFEFF'));
            Assert.Equal(0, preview.ErrorCount);
            Assert.All(preview.Rows, r => Assert.Equal(ImportRowAction.Skip, r.Action));
            Assert.True((await CommitAsync(preview.Token)).Succeeded);
        }

        var detail = await DetailAsync(project.Id);
        Assert.Equal("Robot", detail.Name);
        Assert.Single(detail.Lines);
        Assert.Equal(1, detail.Lines[0].QuantityPerUnit);
    }

    [Fact]
    public async Task Archived_project_stays_archived_after_a_bom_import()
    {
        var part = await CreateProductAsync(Input(sku: "P-1", name: "Part"));
        var project = await NewProjectAsync("Old", number: "PRJ-9");
        await AddPartAsync(project.Id, part.Id, 1);
        Assert.True((await WithProjectsAsync(p => p.SetActiveAsync(project.Id, false))).Succeeded);

        var csv = BomCsv("PRJ-9,Old,2,,,P-1,2,,,");
        var preview = await PreviewAsync(ImportKind.Boms, csv, new ImportOptions(BomImportMode.Merge));
        Assert.True((await CommitAsync(preview.Token)).Succeeded);

        var detail = await DetailAsync(project.Id);
        Assert.False(detail.IsActive);
        Assert.Equal(2, detail.Lines[0].QuantityPerUnit);
    }
}
