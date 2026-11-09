using System.Reflection;
using System.Text;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>Covers the issues in Product/reviews/2026-11-09-1204-F07-code-review.md.</summary>
public sealed class ImportRegressionTests : ImportTestBase
{
    private const string SuppliersHeader = "name,contact_name,email,phone,address,website,notes,active\n";
    private const string LinksHeader = "sku,supplier,supplier_sku,unit_cost,lead_time_days,min_order_quantity,preferred\n";

    private async Task<Supplier> NewSupplierAsync(string name, string? phone = null, bool active = true)
    {
        await using var context = Db.CreateContext();
        var service = new SupplierService(context);
        var created = await service.CreateAsync(new SupplierInput(name, null, null, phone, null, null, null));
        Assert.True(created.Succeeded);
        if (!active)
        {
            Assert.True((await service.SetActiveAsync(created.Value.Id, false)).Succeeded);
        }

        return await context.Suppliers.AsNoTracking().SingleAsync(s => s.Id == created.Value.Id);
    }

    private async Task ArchiveProductAsync(int id)
    {
        await using var context = Db.CreateContext();
        Assert.True((await new ProductService(context).SetActiveAsync(id, false)).Succeeded);
    }

    private async Task<string> ExportAsync(Func<ExportService, Stream, Task> write)
    {
        var (context, _, exports, _) = NewTransfer();
        await using (context)
        {
            await using var stream = new MemoryStream();
            await write(exports, stream);
            return Encoding.UTF8.GetString(stream.ToArray()).TrimStart('﻿');
        }
    }

    // ---- issue 1: Replace + skip invalid ------------------------------------------------------------------------

    [Fact]
    public async Task Replace_with_skip_invalid_leaves_a_project_with_bad_rows_untouched()
    {
        var a = await CreateProductAsync(Input(sku: "A-1", name: "A"));
        var b = await CreateProductAsync(Input(sku: "B-1", name: "B"));
        var project = await NewProjectAsync("Robot", number: "PRJ-1");
        await AddPartAsync(project.Id, a.Id, 1);
        await AddPartAsync(project.Id, b.Id, 2);

        var preview = await PreviewAsync(
            ImportKind.Boms,
            BomCsv("PRJ-1,Robot,1,,,A-1,5,,,,", "PRJ-1,Robot,1,,,B-1,abc,,,,"),
            new ImportOptions(BomImportMode.Replace));
        Assert.Equal(1, preview.ErrorCount);
        Assert.Empty(preview.Replacements);

        var committed = await CommitAsync(preview.Token, skipInvalid: true);
        Assert.True(committed.Succeeded, string.Join("; ", committed.Errors.Select(e => e.Message)));
        Assert.Equal(2, committed.Value.SkippedCount);

        var detail = await DetailAsync(project.Id);
        Assert.Equal(2, detail.Lines.Count);
        Assert.Equal(1, detail.Lines.Single(l => l.Sku == "A-1").QuantityPerUnit);
    }

    [Fact]
    public async Task Merge_with_skip_invalid_applies_the_good_rows_of_a_project()
    {
        var a = await CreateProductAsync(Input(sku: "A-1", name: "A"));
        var project = await NewProjectAsync("Robot", number: "PRJ-1");
        await AddPartAsync(project.Id, a.Id, 1);

        var preview = await PreviewAsync(ImportKind.Boms, BomCsv("PRJ-1,Robot,1,,,A-1,5,,,,", "PRJ-1,Robot,1,,,B-1,abc,,,,"));
        var committed = await CommitAsync(preview.Token, skipInvalid: true);
        Assert.True(committed.Succeeded);
        Assert.Equal(1, committed.Value.SkippedCount);
        Assert.Equal(5, (await DetailAsync(project.Id)).Lines.Single().QuantityPerUnit);
    }

    // ---- issue 3: malformed CSV ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_stray_quote_in_a_plain_field_is_kept()
    {
        var preview = await PreviewAsync(ImportKind.Products, ProductsCsv("Q-1,3.5\" screw,,,,,,0,0,1.00,true"));
        Assert.Equal(ImportRowAction.Create, preview.Rows[0].Action);
        Assert.True((await CommitAsync(preview.Token)).Succeeded);

        await using var context = Db.CreateContext();
        Assert.Equal("3.5\" screw", (await context.Products.SingleAsync(p => p.Sku == "Q-1")).Name);
    }

    [Fact]
    public async Task An_unclosed_quote_is_a_file_error_not_a_crash()
    {
        var (context, imports, _, _) = NewTransfer();
        await using (context)
        {
            var result = await imports.PreviewAsync(
                ImportKind.Products, CsvStream(ProductsCsv("Q-1,\"open,,,,,,0,0,1.00,true", "Q-2,Two,,,,,,0,0,1.00,true")), "x.csv", new ImportOptions(), Importer);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Code == "import.malformed");
        }
    }

    // ---- issues 4, 16: round trips ------------------------------------------------------------------------------

    [Fact]
    public async Task Neutralised_text_comes_back_unchanged()
    {
        Assert.Equal("=x", CsvExport.Restore(CsvExport.Neutralise("=x")!));
        Assert.Equal("'=x", CsvExport.Restore(CsvExport.Neutralise("'=x")!));
        Assert.Equal("'plain", CsvExport.Restore(CsvExport.Neutralise("'plain")!));

        await CreateProductAsync(Input(sku: "-X1", name: "Dash"));
        var csv = await ExportAsync((e, s) => e.WriteProductsAsync(s));
        Assert.Contains("'-X1", csv);

        var preview = await PreviewAsync(ImportKind.Products, csv);
        Assert.All(preview.Rows, r => Assert.Equal(ImportRowAction.Skip, r.Action));
    }

    [Fact]
    public async Task Supplier_export_then_import_is_a_no_op()
    {
        await NewSupplierAsync("Acme", phone: "+1 555-0100");
        await NewSupplierAsync("Old", active: false);

        var preview = await PreviewAsync(ImportKind.Suppliers, await ExportAsync((e, s) => e.WriteSuppliersAsync(s)));
        Assert.Equal(2, preview.RowCount);
        Assert.All(preview.Rows, r => Assert.Equal(ImportRowAction.Skip, r.Action));
    }

    [Fact]
    public async Task Link_export_then_import_is_a_no_op()
    {
        var part = await CreateProductAsync(Input(sku: "L-1", name: "Linked"));
        var supplier = await NewSupplierAsync("Acme");
        await using (var context = Db.CreateContext())
        {
            Assert.True((await new SupplierService(context).LinkProductAsync(part.Id, supplier.Id, new SupplierLinkInput("A-9", 1250, 7, 5, true))).Succeeded);
        }

        var preview = await PreviewAsync(ImportKind.ProductSuppliers, await ExportAsync((e, s) => e.WriteProductSuppliersAsync(s)));
        Assert.Equal(ImportRowAction.Skip, Assert.Single(preview.Rows).Action);
    }

    [Fact]
    public async Task Empty_project_previews_as_a_no_op_after_export()
    {
        await NewProjectAsync("Empty", number: "PRJ-E");
        var preview = await PreviewAsync(ImportKind.Boms, await ExportAsync((e, s) => e.WriteBomsAsync(s, new BomExportQuery())));
        Assert.Equal(ImportRowAction.Skip, Assert.Single(preview.Rows).Action);
        Assert.Empty(preview.ProjectChanges);
    }

    // ---- issue 5: rows the services would refuse are errors in the preview --------------------------------------

    [Fact]
    public async Task Archived_part_in_opening_stock_is_a_row_error_and_can_be_skipped()
    {
        var archived = await CreateProductAsync(Input(sku: "ARC-1", name: "Gone"));
        await ArchiveProductAsync(archived.Id);
        await CreateProductAsync(Input(sku: "OK-1", name: "Here"));

        var preview = await PreviewAsync(ImportKind.OpeningStock, "sku,quantity,note\nARC-1,5,\nOK-1,7,\n");
        Assert.Contains("archived", preview.Rows[0].Error);
        Assert.Equal(ImportRowAction.Update, preview.Rows[1].Action);

        Assert.True((await CommitAsync(preview.Token, skipInvalid: true)).Succeeded);
        await using var context = Db.CreateContext();
        Assert.Equal(7, (await context.Products.SingleAsync(p => p.Sku == "OK-1")).QuantityOnHand);
    }

    [Fact]
    public async Task Part_rows_report_length_barcode_and_archived_category_problems()
    {
        await CreateProductAsync(Input(sku: "BC-1", name: "Has barcode") with { Barcode = "111" });
        await using (var context = Db.CreateContext())
        {
            var categories = new CategoryService(context);
            var created = await categories.CreateAsync(new CategoryInput("Retired", null));
            Assert.True(created.Succeeded);
            Assert.True((await categories.SetActiveAsync(created.Value.Id, false)).Succeeded);
        }

        var preview = await PreviewAsync(ImportKind.Products, ProductsCsv(
            $"LONG-1,{new string('x', Product.NameMaxLength + 1)},,,,,,0,0,1.00,true",
            "DUP-1,Taken barcode,,,,111,,0,0,1.00,true",
            "NEW-1,First,,,,222,,0,0,1.00,true",
            "NEW-2,Same barcode,,,,222,,0,0,1.00,true",
            "CAT-1,Archived category,,Retired,,,,0,0,1.00,true"));

        Assert.Contains("at most", preview.Rows[0].Error);
        Assert.Contains("already used", preview.Rows[1].Error);
        Assert.Equal(ImportRowAction.Create, preview.Rows[2].Action);
        Assert.Contains("more than once", preview.Rows[3].Error);
        Assert.Contains("archived", preview.Rows[4].Error);
    }

    [Fact]
    public async Task Supplier_and_link_rows_report_service_rules()
    {
        var suppliers = await PreviewAsync(ImportKind.Suppliers, SuppliersHeader + "Bad,,not-an-email,,,,,true\n");
        Assert.Contains("valid email", suppliers.Rows[0].Error);

        var archivedPart = await CreateProductAsync(Input(sku: "AP-1", name: "Gone"));
        await ArchiveProductAsync(archivedPart.Id);
        await CreateProductAsync(Input(sku: "OK-1", name: "Here"));
        await NewSupplierAsync("Acme");
        await NewSupplierAsync("Old", active: false);

        var links = await PreviewAsync(ImportKind.ProductSuppliers, LinksHeader + "AP-1,Acme,,1.00,,,\nOK-1,Old,,1.00,,,\n");
        Assert.Contains("archived", links.Rows[0].Error);
        Assert.Contains("archived", links.Rows[1].Error);
    }

    [Fact]
    public async Task Bom_rows_report_archived_parts_and_name_collisions()
    {
        var gone = await CreateProductAsync(Input(sku: "GONE-1", name: "Gone"));
        await ArchiveProductAsync(gone.Id);
        await NewProjectAsync("Taken", number: "PRJ-T");

        var archivedPart = await PreviewAsync(ImportKind.Boms, BomCsv("PRJ-N,New,1,,,GONE-1,1,,,,"));
        Assert.Contains("archived", archivedPart.Rows[0].Error);

        var blankNumber = await PreviewAsync(ImportKind.Boms, BomCsv(",Taken,1,,,,,,,,"));
        Assert.Contains("already exists", blankNumber.Rows[0].Error);

        var sameNameTwice = await PreviewAsync(ImportKind.Boms, BomCsv("A,Twin,1,,,,,,,,", "B,Twin,1,,,,,,,,"));
        Assert.Equal(ImportRowAction.Create, sameNameTwice.Rows[0].Action);
        Assert.Contains("also named", sameNameTwice.Rows[1].Error);
    }

    // ---- issues 6, 7: staged file lifecycle ---------------------------------------------------------------------

    private static string StagedPath(string token) => Path.Combine(Path.GetTempPath(), $"pp-import-{token}.csv");

    [Fact]
    public async Task A_committed_upload_is_removed_from_disk()
    {
        var preview = await PreviewAsync(ImportKind.Products, ProductsCsv("T-1,Temp,,,,,,0,0,1.00,true"));
        Assert.True(File.Exists(StagedPath(preview.Token)));
        Assert.True((await CommitAsync(preview.Token)).Succeeded);
        Assert.False(File.Exists(StagedPath(preview.Token)));
    }

    [Fact]
    public async Task A_failed_commit_keeps_the_upload_so_the_preview_can_be_shown_again()
    {
        await CreateProductAsync(Input(sku: "S-1", name: "Stocked"));
        var preview = await PreviewAsync(ImportKind.OpeningStock, "sku,quantity,note\nS-1,3,\n");

        var (context, projects, _) = NewProjects();
        await using (context)
        {
            var imports = new ImportService(
                context, new ProductService(context), new CategoryService(context), new SupplierService(context), DispatchProxy.Create<IStockService, FailingStock>(),
                projects, Staging, Clock, new ImportEventPublisher([], NullLogger<ImportEventPublisher>.Instance));

            var result = await imports.CommitAsync(preview.Token, Importer, skipInvalid: false);
            Assert.False(result.Succeeded);
            Assert.Null(result.Errors[0].Field);
            Assert.NotNull(await imports.PeekAsync(preview.Token, Importer));
        }

        await using var check = Db.CreateContext();
        Assert.Equal(0, await check.ImportBatches.CountAsync());
    }

    public class FailingStock : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(IStockService.PostAsync)
                ? Task.FromResult(Result.Failure<IReadOnlyList<PostedMovement>>("stock.boom", "Refused."))
                : throw new NotSupportedException(targetMethod.Name);
    }

    // ---- issues 11, 12, 24: BOM details -------------------------------------------------------------------------

    [Fact]
    public async Task A_part_created_by_a_bom_import_keeps_the_line_note()
    {
        var preview = await PreviewAsync(ImportKind.Boms, BomCsv("PRJ-N,New,1,,,NEW-1,2,the note,New part,each,1.00"), new ImportOptions(CreateMissingParts: true));
        var committed = await CommitAsync(preview.Token);
        Assert.True(committed.Succeeded);

        await using var context = Db.CreateContext();
        Assert.Equal("the note", (await context.BomLines.SingleAsync()).Note);
    }

    [Fact]
    public async Task Project_header_changes_are_listed_in_the_preview()
    {
        var part = await CreateProductAsync(Input(sku: "P-1", name: "Part"));
        var project = await NewProjectAsync("Old name", number: "PRJ-1");
        await AddPartAsync(project.Id, part.Id, 1);

        var preview = await PreviewAsync(ImportKind.Boms, BomCsv("PRJ-1,New name,3,,,P-1,1,,,,"));
        Assert.Equal(ImportRowAction.Skip, preview.Rows[0].Action);
        var change = Assert.Single(preview.ProjectChanges);
        Assert.Equal("PRJ-1", change.ProjectNumber);
        Assert.Contains(change.Changes, c => c.Contains("New name", StringComparison.Ordinal));
        Assert.Contains(change.Changes, c => c.Contains("target units", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_number_and_another_projects_name_do_not_merge()
    {
        var preview = await PreviewAsync(ImportKind.Boms, BomCsv("X,Alpha,1,,,,,,,,", ",X,1,,,,,,,,"));
        Assert.Equal(0, preview.ErrorCount);
        Assert.Equal(new[] { "X — Alpha", "X" }, preview.Rows.Select(r => r.Project));
        Assert.True((await CommitAsync(preview.Token)).Succeeded);

        await using var context = Db.CreateContext();
        Assert.Equal(2, await context.Projects.CountAsync());
    }

    // ---- issue 15: suppliers and links, create and update -------------------------------------------------------

    [Fact]
    public async Task Suppliers_create_and_update_by_name()
    {
        await NewSupplierAsync("Acme", phone: "111");

        var preview = await PreviewAsync(ImportKind.Suppliers, SuppliersHeader + "acme,,,222,,,,\nNew Co,Jo,jo@new.example,,,,Net 30,false\n");
        Assert.Equal(ImportRowAction.Update, preview.Rows[0].Action);
        Assert.Equal(ImportRowAction.Create, preview.Rows[1].Action);
        Assert.True((await CommitAsync(preview.Token)).Succeeded);

        await using var context = Db.CreateContext();
        Assert.Equal("222", (await context.Suppliers.SingleAsync(s => s.Name == "Acme")).Phone);
        var created = await context.Suppliers.SingleAsync(s => s.Name == "New Co");
        Assert.False(created.IsActive);
        Assert.Equal("Net 30", created.Notes);
    }

    [Fact]
    public async Task Links_create_and_update_by_sku_and_supplier()
    {
        var part = await CreateProductAsync(Input(sku: "L-1", name: "Linked"));
        await NewSupplierAsync("Acme");
        await NewSupplierAsync("Beta");

        var first = await PreviewAsync(ImportKind.ProductSuppliers, LinksHeader + "L-1,Acme,A-1,1.00,3,2,true\n");
        Assert.Equal(ImportRowAction.Create, first.Rows[0].Action);
        Assert.True((await CommitAsync(first.Token)).Succeeded);

        var second = await PreviewAsync(ImportKind.ProductSuppliers, LinksHeader + "L-1,Acme,,2.50,,,\nL-1,Beta,,1.00,,,\nL-1,Nobody,,1.00,,,\n");
        Assert.Equal(ImportRowAction.Update, second.Rows[0].Action);
        Assert.Equal(ImportRowAction.Create, second.Rows[1].Action);
        Assert.Contains("No supplier", second.Rows[2].Error);
        Assert.True((await CommitAsync(second.Token, skipInvalid: true)).Succeeded);

        await using var context = Db.CreateContext();
        var links = await context.ProductSuppliers.Where(l => l.ProductId == part.Id).Include(l => l.Supplier).ToListAsync();
        Assert.Equal(2, links.Count);
        var updated = links.Single(l => l.Supplier!.Name == "Acme");
        Assert.Equal(250, updated.UnitCostCents);
        Assert.Equal("A-1", updated.SupplierSku); // a blank cell leaves the value alone
    }

    [Fact]
    public async Task Bom_project_picker_lists_more_than_a_page_of_projects()
    {
        await NewProjectAsync("One", number: "PRJ-1");
        var (context, _, exports, _) = NewTransfer();
        await using (context)
        {
            Assert.Equal("PRJ-1", Assert.Single(await exports.ListBomProjectsAsync()).Number);
        }
    }
}
