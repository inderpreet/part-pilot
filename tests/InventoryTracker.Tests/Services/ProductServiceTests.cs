using System.Diagnostics;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace InventoryTracker.Tests.Services;

public class ProductServiceTests(ITestOutputHelper output) : CatalogTestBase
{
    private static string[] Fields(Result result) => [.. result.Errors.Select(e => e.Field ?? "")];

    private static string[] Codes(Result result) => [.. result.Errors.Select(e => e.Code)];

    // ---- create ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_stores_trimmed_values_and_applies_defaults()
    {
        var product = await CreateProductAsync(Input(sku: "  AB-1  ", name: "  Blue Widget ", description: "   ", unit: " ", barcode: "  ", bin: " A1 ", costCents: 1250));

        Assert.True(product.Id > 0);
        Assert.Equal("AB-1", product.Sku);
        Assert.Equal("Blue Widget", product.Name);
        Assert.Null(product.Description);
        Assert.Equal("each", product.Unit);
        Assert.Null(product.Barcode);
        Assert.Equal("A1", product.BinLocation);
        Assert.Equal(1250, product.UnitCostCents);
        Assert.Equal(0, product.QuantityOnHand);
        Assert.True(product.IsActive);
        Assert.False(product.IsLowStock);
        Assert.False(product.SkuLocked);
        Assert.Equal(1L, product.RowVersion);
        Assert.Equal(DateTimeKind.Utc, product.CreatedAt.Kind);
    }

    [Fact]
    public async Task Create_keeps_a_custom_unit_and_a_category()
    {
        var category = await CreateCategoryAsync("Hardware");

        var product = await CreateProductAsync(Input(unit: "box", categoryId: category.Id));

        Assert.Equal("box", product.Unit);
        Assert.Equal(category.Id, product.CategoryId);
        Assert.Equal("Hardware", product.CategoryName);
    }

    [Theory]
    [InlineData(null, "Widget", "Sku", "product.sku_required")]
    [InlineData("  ", "Widget", "Sku", "product.sku_required")]
    [InlineData("SKU-1", null, "Name", "product.name_required")]
    [InlineData("SKU-1", "   ", "Name", "product.name_required")]
    public async Task Create_requires_sku_and_name(string? sku, string? name, string field, string code)
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.CreateAsync(Input(sku: sku!, name: name!));

        Assert.False(result.Succeeded);
        Assert.Contains(field, Fields(result));
        Assert.Contains(code, Codes(result));
        Assert.Equal(0, await context.Products.CountAsync());
    }

    [Fact]
    public async Task Create_enforces_maximum_lengths()
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.CreateAsync(new ProductInput(
            new string('s', 41), new string('n', 201), new string('d', 2001), null, new string('u', 21), new string('b', 65), new string('l', 51), 0, 0, 0));

        Assert.Equivalent(new[] { "Sku", "Name", "Description", "Unit", "Barcode", "BinLocation" }, Fields(result));
    }

    [Fact]
    public async Task Create_accepts_values_exactly_at_the_maximum_length()
    {
        var product = await CreateProductAsync(new ProductInput(
            new string('s', 40), new string('n', 200), new string('d', 2000), null, new string('u', 20), new string('b', 64), new string('l', 50), 0, 0, 0));

        Assert.Equal(40, product.Sku.Length);
    }

    [Theory]
    [InlineData(-1, 0, 0, "ReorderPoint")]
    [InlineData(0, -1, 0, "ReorderQuantity")]
    [InlineData(0, 0, -1, "UnitCost")]
    public async Task Create_rejects_negative_numbers(int point, int quantity, long cost, string field)
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.CreateAsync(Input(reorderPoint: point, reorderQuantity: quantity, costCents: cost));

        Assert.False(result.Succeeded);
        Assert.Equal([field], Fields(result));
    }

    [Fact]
    public async Task Create_reports_every_problem_at_once()
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.CreateAsync(Input(sku: "", name: "", reorderPoint: -1));

        Assert.Equivalent(new[] { "Sku", "Name", "ReorderPoint" }, Fields(result));
    }

    // ---- uniqueness --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_sku_is_a_field_error_regardless_of_case()
    {
        await CreateProductAsync(Input(sku: "ABC-1"));
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.CreateAsync(Input(sku: "abc-1", name: "Other"));

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("product.sku_taken", error.Code);
        Assert.Equal("Sku", error.Field);
        Assert.Equal(1, await context.Products.CountAsync());
    }

    [Fact]
    public async Task Archived_products_still_hold_their_sku_and_barcode()
    {
        var archived = await CreateProductAsync(Input(sku: "OLD-1", barcode: "111"));
        await ArchiveAsync(archived.Id);
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var sku = await products.CreateAsync(Input(sku: "old-1"));
        var barcode = await products.CreateAsync(Input(sku: "NEW-1", barcode: "111"));

        Assert.Equal("product.sku_taken", Assert.Single(sku.Errors).Code);
        Assert.Equal("product.barcode_taken", Assert.Single(barcode.Errors).Code);
    }

    [Fact]
    public async Task Duplicate_barcode_is_rejected_case_insensitively_but_many_products_may_have_none()
    {
        await CreateProductAsync(Input(sku: "A", barcode: "abc123"));
        await CreateProductAsync(Input(sku: "B"));
        await CreateProductAsync(Input(sku: "C", barcode: ""));
        await CreateProductAsync(Input(sku: "D", barcode: "   "));
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.CreateAsync(Input(sku: "E", barcode: "ABC123"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("product.barcode_taken", error.Code);
        Assert.Equal("Barcode", error.Field);
        Assert.Equal(3, await context.Products.CountAsync(p => p.Barcode == null));
    }

    [Fact]
    public async Task Losing_a_unique_race_on_sku_is_reported_as_a_field_error_not_an_exception()
    {
        // A concurrent request inserts the same SKU between our validation and our save.
        var race = new RunSqlBeforeSave("INSERT INTO Products (Sku, Name, Unit, ReorderPoint, ReorderQuantity, UnitCostCents, QuantityOnHand, IsActive, RowVersion, CreatedAt, UpdatedAt) VALUES ('RACE-1', 'Winner', 'each', 0, 0, 0, 0, 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        var (context, products, _) = NewRequest(race);
        await using var _ = context;

        var result = await products.CreateAsync(Input(sku: "race-1", name: "Loser"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("product.sku_taken", error.Code);
        Assert.Equal("Sku", error.Field);
    }

    [Fact]
    public async Task Losing_a_unique_race_on_barcode_is_reported_as_a_field_error()
    {
        var race = new RunSqlBeforeSave("INSERT INTO Products (Sku, Name, Barcode, Unit, ReorderPoint, ReorderQuantity, UnitCostCents, QuantityOnHand, IsActive, RowVersion, CreatedAt, UpdatedAt) VALUES ('W', 'Winner', 'BC-1', 'each', 0, 0, 0, 0, 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        var (context, products, _) = NewRequest(race);
        await using var _ = context;

        var result = await products.CreateAsync(Input(sku: "L", barcode: "bc-1"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("product.barcode_taken", error.Code);
        Assert.Equal("Barcode", error.Field);
    }

    // ---- categories on products --------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_category_is_rejected()
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.CreateAsync(Input(categoryId: 9999));

        Assert.Equal("product.category_missing", Assert.Single(result.Errors).Code);
        Assert.Equal("CategoryId", result.Errors[0].Field);
    }

    [Fact]
    public async Task An_archived_category_cannot_be_newly_chosen_but_stays_on_products_that_have_it()
    {
        var category = await CreateCategoryAsync("Seasonal");
        var product = await CreateProductAsync(Input(sku: "KEEP", categoryId: category.Id));

        var (context, products, categories) = NewRequest();
        await using var _ = context;
        Assert.True((await categories.SetActiveAsync(category.Id, false)).Succeeded);

        // New product: refused.
        var created = await products.CreateAsync(Input(sku: "NEW", categoryId: category.Id));
        Assert.Equal("product.category_archived", Assert.Single(created.Errors).Code);

        // Existing product keeps it through an unrelated edit, and still shows it.
        var updated = await products.UpdateAsync(product.Id, product.RowVersion, Input(sku: "KEEP", name: "Renamed", categoryId: category.Id));
        Assert.True(updated.Succeeded, string.Join("; ", updated.Errors.Select(e => e.Message)));
        Assert.Equal("Seasonal", updated.Value.CategoryName);
        Assert.False(updated.Value.CategoryIsActive);
    }

    // ---- update ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_changes_fields_and_bumps_the_row_version()
    {
        var category = await CreateCategoryAsync("Tools");
        var created = await CreateProductAsync(Input());
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.UpdateAsync(created.Id, created.RowVersion,
            Input(sku: "NEW-SKU", name: "Renamed", barcode: "999", categoryId: category.Id, description: "Desc", unit: "box", bin: "B2", reorderPoint: 5, reorderQuantity: 20, costCents: 999));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        var p = result.Value;
        Assert.Equal(("NEW-SKU", "Renamed", "999", "Desc", "box", "B2", 5, 20, 999L), (p.Sku, p.Name, p.Barcode, p.Description, p.Unit, p.BinLocation, p.ReorderPoint, p.ReorderQuantity, p.UnitCostCents));
        Assert.Equal(category.Id, p.CategoryId);
        Assert.Equal(created.RowVersion + 1, p.RowVersion);
        Assert.True(p.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task Update_can_keep_its_own_sku_and_barcode_and_change_only_their_case()
    {
        var created = await CreateProductAsync(Input(sku: "abc", barcode: "xyz"));
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.UpdateAsync(created.Id, created.RowVersion, Input(sku: "ABC", barcode: "XYZ", name: "Same product"));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal("ABC", result.Value.Sku);
    }

    [Fact]
    public async Task Update_cannot_take_another_products_sku_or_barcode()
    {
        await CreateProductAsync(Input(sku: "ONE", barcode: "1"));
        var two = await CreateProductAsync(Input(sku: "TWO", barcode: "2"));
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.UpdateAsync(two.Id, two.RowVersion, Input(sku: "one", barcode: "1"));

        Assert.Equivalent(new[] { "product.sku_taken", "product.barcode_taken" }, Codes(result));
    }

    [Fact]
    public async Task Update_with_a_stale_row_version_reports_a_conflict_and_saves_nothing()
    {
        var created = await CreateProductAsync(Input(name: "Original"));

        var (firstContext, first, _) = NewRequest();
        var (secondContext, second, _) = NewRequest();
        await using var _1 = firstContext;
        await using var _2 = secondContext;

        // Both users open the edit form at version 1; the first saves.
        Assert.True((await first.UpdateAsync(created.Id, created.RowVersion, Input(name: "First edit"))).Succeeded);

        var result = await second.UpdateAsync(created.Id, created.RowVersion, Input(name: "Second edit"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("product.conflict", error.Code);
        Assert.Null(error.Field); // shown at the top of the form, not on one input
        Assert.Contains("modified by someone else", error.Message);

        var (verify, products, _) = NewRequest();
        await using var _3 = verify;
        Assert.Equal("First edit", (await products.GetAsync(created.Id))!.Name);
    }

    [Fact]
    public async Task A_concurrent_edit_landing_during_the_save_is_also_a_conflict()
    {
        var created = await CreateProductAsync(Input(name: "Original"));

        // Another request bumps the row between our version check and our UPDATE.
        var race = new RunSqlBeforeSave($"UPDATE Products SET Name = 'Sneaky', RowVersion = RowVersion + 1 WHERE Id = {created.Id}");
        var (context, products, _) = NewRequest(race);
        await using var _ = context;

        var result = await products.UpdateAsync(created.Id, created.RowVersion, Input(name: "Mine"));

        Assert.Equal("product.conflict", Assert.Single(result.Errors).Code);
        Assert.Equal("Sneaky", (await products.GetAsync(created.Id))!.Name);
    }

    [Fact]
    public async Task Update_of_a_missing_product_reports_not_found()
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.UpdateAsync(12345, 1, Input());

        Assert.Equal("product.not_found", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Update_never_changes_quantity_on_hand()
    {
        var created = await CreateProductAsync(Input());
        await SetOnHandAsync(created.Id, 42);
        var (context, products, _) = NewRequest();
        await using var _ = context;
        var current = (await products.GetAsync(created.Id))!;

        var result = await products.UpdateAsync(created.Id, current.RowVersion, Input(name: "Edited"));

        Assert.Equal(42, result.Value.QuantityOnHand);
    }

    // ---- archive / restore -------------------------------------------------------------------------------------

    [Fact]
    public async Task Archive_and_restore_round_trip_and_are_idempotent()
    {
        var created = await CreateProductAsync(Input());
        var (context, products, _) = NewRequest();
        await using var _ = context;

        Assert.True((await products.SetActiveAsync(created.Id, false)).Succeeded);
        Assert.False((await products.GetAsync(created.Id))!.IsActive);
        Assert.True((await products.SetActiveAsync(created.Id, false)).Succeeded); // again: no-op

        Assert.True((await products.SetActiveAsync(created.Id, true)).Succeeded);
        Assert.True((await products.GetAsync(created.Id))!.IsActive);
    }

    [Fact]
    public async Task Archive_with_a_stale_row_version_is_a_conflict()
    {
        var created = await CreateProductAsync(Input());
        var (context, products, _) = NewRequest();
        await using var _ = context;
        Assert.True((await products.UpdateAsync(created.Id, created.RowVersion, Input(name: "Edited"))).Succeeded);

        var result = await products.SetActiveAsync(created.Id, false, rowVersion: created.RowVersion);

        Assert.Equal("product.conflict", Assert.Single(result.Errors).Code);
        Assert.True((await products.GetAsync(created.Id))!.IsActive);
    }

    [Fact]
    public async Task Archiving_a_missing_product_reports_not_found()
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;

        Assert.Equal("product.not_found", Assert.Single((await products.SetActiveAsync(404, false)).Errors).Code);
    }

    // ---- get / find by code ------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_returns_null_for_a_missing_product()
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;

        Assert.Null(await products.GetAsync(404));
    }

    [Fact]
    public async Task Find_by_code_matches_sku_or_barcode_exactly_and_case_insensitively()
    {
        var bySku = await CreateProductAsync(Input(sku: "Hammer-1", name: "Hammer", barcode: "0012345"));
        var (context, products, _) = NewRequest();
        await using var _ = context;

        Assert.Equal(bySku.Id, (await products.FindByCodeAsync("hammer-1"))!.Id);
        Assert.Equal(bySku.Id, (await products.FindByCodeAsync("  HAMMER-1\n"))!.Id); // scanners often append whitespace
        Assert.Equal(bySku.Id, (await products.FindByCodeAsync("0012345"))!.Id);
        Assert.Null(await products.FindByCodeAsync("Hammer")); // partial: not an exact match
        Assert.Null(await products.FindByCodeAsync("001234"));
        Assert.Null(await products.FindByCodeAsync(""));
        Assert.Null(await products.FindByCodeAsync(null));
    }

    [Fact]
    public async Task Find_by_code_prefers_a_sku_match_over_another_products_barcode_and_finds_archived_products()
    {
        var barcodeOwner = await CreateProductAsync(Input(sku: "X", name: "Barcode owner", barcode: "SAME"));
        var skuOwner = await CreateProductAsync(Input(sku: "SAME", name: "Sku owner"));
        await ArchiveAsync(skuOwner.Id);
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var found = await products.FindByCodeAsync("same");

        Assert.Equal(skuOwner.Id, found!.Id); // archived, but still found by an exact code
        Assert.NotEqual(barcodeOwner.Id, found.Id);
    }

    // ---- search ------------------------------------------------------------------------------------------------

    private async Task<(int Hammer, int Nails, int Saw, int Old)> SeedCatalogAsync()
    {
        var tools = await CreateCategoryAsync("Tools");
        var fixings = await CreateCategoryAsync("Fixings");
        var hammer = await CreateProductAsync(Input(sku: "H-100", name: "Claw hammer", barcode: "5000001", categoryId: tools.Id, costCents: 1500));
        var nails = await CreateProductAsync(Input(sku: "N-200", name: "Steel nails 50mm", barcode: "5000002", categoryId: fixings.Id));
        var saw = await CreateProductAsync(Input(sku: "S-300", name: "hand saw", categoryId: tools.Id));
        var old = await CreateProductAsync(Input(sku: "O-400", name: "Obsolete anvil", categoryId: tools.Id));
        await ArchiveAsync(old.Id);
        await SetOnHandAsync(hammer.Id, 10);
        await SetOnHandAsync(nails.Id, 500);
        await SetOnHandAsync(saw.Id, 2);
        return (hammer.Id, nails.Id, saw.Id, old.Id);
    }

    private async Task<PagedResult<ProductListItem>> SearchAsync(ProductListQuery query)
    {
        var (context, products, _) = NewRequest();
        await using var _ = context;
        return await products.SearchAsync(query);
    }

    private static string[] Skus(PagedResult<ProductListItem> page) => [.. page.Items.Select(i => i.Sku)];

    [Fact]
    public async Task Search_shows_only_active_products_by_default_ordered_by_name()
    {
        await SeedCatalogAsync();

        var page = await SearchAsync(new ProductListQuery());

        Assert.Equal(["H-100" /* Claw hammer */, "S-300" /* hand saw */, "N-200" /* Steel nails */], Skus(page));
        Assert.Equal(3, page.TotalCount);
    }

    [Theory]
    [InlineData(ProductStatus.Active, new[] { "H-100", "S-300", "N-200" })]
    [InlineData(ProductStatus.Archived, new[] { "O-400" })]
    [InlineData(ProductStatus.All, new[] { "H-100", "S-300", "O-400", "N-200" })]
    public async Task Search_filters_by_status(ProductStatus status, string[] expected)
    {
        await SeedCatalogAsync();

        var page = await SearchAsync(new ProductListQuery { Status = status });

        Assert.Equal(expected, Skus(page));
    }

    [Fact]
    public async Task Search_filters_by_category()
    {
        await SeedCatalogAsync();
        var (context, _, categories) = NewRequest();
        await using var _ = context;
        var fixings = (await categories.ListAsync()).Single(c => c.Name == "Fixings");

        var page = await SearchAsync(new ProductListQuery { CategoryId = fixings.Id });

        Assert.Equal(["N-200"], Skus(page));
        Assert.Equal("Fixings", page.Items[0].CategoryName);
    }

    [Theory]
    [InlineData("h-100", new[] { "H-100" })] // sku, case-insensitive
    [InlineData("HAMMER", new[] { "H-100" })] // name, substring
    [InlineData("hand", new[] { "S-300" })]
    [InlineData("5000002", new[] { "N-200" })] // barcode
    [InlineData("500000", new[] { "H-100", "N-200" })] // partial barcode, two hits
    [InlineData("  nails  ", new[] { "N-200" })] // trimmed
    [InlineData("zzz-nothing", new string[0])]
    public async Task Search_matches_sku_name_and_barcode(string search, string[] expected)
    {
        await SeedCatalogAsync();

        var page = await SearchAsync(new ProductListQuery { Search = search });

        Assert.Equal(expected, Skus(page));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task Search_treats_like_wildcards_literally(string search)
    {
        await SeedCatalogAsync();

        Assert.Empty((await SearchAsync(new ProductListQuery { Search = search })).Items);
    }

    [Fact]
    public async Task Search_finds_a_literal_percent_sign()
    {
        await CreateProductAsync(Input(sku: "PCT", name: "50% off bundle"));
        await CreateProductAsync(Input(sku: "OTHER", name: "Plain"));

        Assert.Equal(["PCT"], Skus(await SearchAsync(new ProductListQuery { Search = "50%" })));
    }

    [Theory]
    [InlineData("sku", false, new[] { "H-100", "N-200", "S-300" })]
    [InlineData("sku", true, new[] { "S-300", "N-200", "H-100" })]
    [InlineData("name", false, new[] { "H-100", "S-300", "N-200" })]
    [InlineData("name", true, new[] { "N-200", "S-300", "H-100" })]
    [InlineData("onhand", false, new[] { "S-300", "H-100", "N-200" })]
    [InlineData("onhand", true, new[] { "N-200", "H-100", "S-300" })]
    [InlineData("category", false, new[] { "N-200", "H-100", "S-300" })] // Fixings, then Tools by name
    [InlineData("category", true, new[] { "H-100", "S-300", "N-200" })]
    [InlineData("nonsense", false, new[] { "H-100", "S-300", "N-200" })] // unknown key falls back to name
    public async Task Search_sorts_by_each_column(string sort, bool desc, string[] expected)
    {
        await SeedCatalogAsync();

        var page = await SearchAsync(new ProductListQuery { Sort = sort, Desc = desc });

        Assert.Equal(expected, Skus(page));
    }

    [Fact]
    public async Task Search_pages_25_at_a_time_and_snaps_a_too_high_page_to_the_last()
    {
        var (context, _, _) = NewRequest();
        await using (context)
        {
            context.Products.AddRange(Enumerable.Range(1, 60).Select(i => new Product { Sku = $"P{i:000}", Name = $"Item {i:000}" }));
            await context.SaveChangesAsync();
        }

        var first = await SearchAsync(new ProductListQuery());
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(60, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal("P001", first.Items[0].Sku);

        var third = await SearchAsync(new ProductListQuery { Page = 3 });
        Assert.Equal(10, third.Items.Count);
        Assert.Equal("P060", third.Items[^1].Sku);

        var beyond = await SearchAsync(new ProductListQuery { Page = 99 });
        Assert.Equal(3, beyond.Page);
        Assert.Equal(10, beyond.Items.Count);
    }

    [Fact]
    public async Task Paging_is_stable_when_the_sort_column_has_ties()
    {
        var (context, _, _) = NewRequest();
        await using (context)
        {
            context.Products.AddRange(Enumerable.Range(1, 30).Select(i => new Product { Sku = $"T{i:000}", Name = "Same name" }));
            await context.SaveChangesAsync();
        }

        var one = await SearchAsync(new ProductListQuery { Page = 1, PageSize = 10 });
        var two = await SearchAsync(new ProductListQuery { Page = 2, PageSize = 10 });
        var three = await SearchAsync(new ProductListQuery { Page = 3, PageSize = 10 });

        var all = Skus(one).Concat(Skus(two)).Concat(Skus(three)).ToArray();
        Assert.Equal(30, all.Distinct().Count()); // no row repeated or skipped across pages
    }

    [Fact]
    public async Task Search_flags_low_stock_only_for_active_products_with_a_reorder_point()
    {
        var below = await CreateProductAsync(Input(sku: "LOW", name: "a low", reorderPoint: 10));
        var equal = await CreateProductAsync(Input(sku: "EQ", name: "b equal", reorderPoint: 10));
        var above = await CreateProductAsync(Input(sku: "OK", name: "c ok", reorderPoint: 10));
        var noPoint = await CreateProductAsync(Input(sku: "NOPT", name: "d no point", reorderPoint: 0));
        var archived = await CreateProductAsync(Input(sku: "ARCH", name: "e archived", reorderPoint: 10));
        await SetOnHandAsync(below.Id, 3);
        await SetOnHandAsync(equal.Id, 10);
        await SetOnHandAsync(above.Id, 11);
        await SetOnHandAsync(noPoint.Id, 0);
        await SetOnHandAsync(archived.Id, 0);
        await ArchiveAsync(archived.Id);

        var page = await SearchAsync(new ProductListQuery { Status = ProductStatus.All });

        var low = page.Items.ToDictionary(i => i.Sku, i => i.IsLowStock);
        Assert.Equal(new Dictionary<string, bool> { ["LOW"] = true, ["EQ"] = true, ["OK"] = false, ["NOPT"] = false, ["ARCH"] = false }, low);
        Assert.True((await SearchAsync(new ProductListQuery { Search = "LOW" })).Items[0].IsLowStock);
    }

    [Fact]
    public async Task Search_reports_on_hand_cost_and_category()
    {
        await SeedCatalogAsync();

        var hammer = (await SearchAsync(new ProductListQuery { Search = "H-100" })).Items.Single();

        Assert.Equal(10, hammer.QuantityOnHand);
        Assert.Equal(1500, hammer.UnitCostCents);
        Assert.Equal("Tools", hammer.CategoryName);
        Assert.Equal("each", hammer.Unit);
    }

    [Fact]
    public async Task Search_over_ten_thousand_products_is_fast()
    {
        var categoryId = (await CreateCategoryAsync("Bulk")).Id;
        var (context, _, _) = NewRequest();
        await using (context)
        {
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            context.Products.AddRange(Enumerable.Range(1, 10_000).Select(i => new Product
            {
                Sku = $"BULK-{i:00000}",
                Name = $"Bulk product number {i}",
                Barcode = $"9{i:000000000}",
                CategoryId = categoryId,
                QuantityOnHand = i % 100,
                ReorderPoint = i % 7,
            }));
            await context.SaveChangesAsync();
        }

        var (searchContext, products, _) = NewRequest();
        await using var _ = searchContext;
        await products.SearchAsync(new ProductListQuery()); // warm up (query compilation, connection)

        async Task<long> Time(ProductListQuery query)
        {
            var watch = Stopwatch.StartNew();
            var page = await products.SearchAsync(query);
            watch.Stop();
            Assert.True(page.TotalCount > 0);
            return watch.ElapsedMilliseconds;
        }

        var listing = await Time(new ProductListQuery());
        var bySku = await Time(new ProductListQuery { Search = "BULK-0500" });
        var byName = await Time(new ProductListQuery { Search = "number 77" });
        var byBarcode = await Time(new ProductListQuery { Search = "9000000042" });
        var sorted = await Time(new ProductListQuery { Sort = "onhand", Desc = true, CategoryId = categoryId });
        output.WriteLine($"10k products: listing {listing} ms, sku {bySku} ms, name {byName} ms, barcode {byBarcode} ms, sorted+filtered {sorted} ms");

        // Target is < 100 ms (informational); the hard limit is loose so slow CI machines do not flake.
        Assert.True(Math.Max(Math.Max(listing, bySku), Math.Max(Math.Max(byName, byBarcode), sorted)) < 1000, "Catalog search is unexpectedly slow.");
    }

    // ---- lookup ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lookup_hides_archived_products_unless_asked()
    {
        var ids = await SeedCatalogAsync();
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var active = await products.LookupAsync("a");
        var all = await products.LookupAsync("anvil", activeOnly: false);

        Assert.DoesNotContain(active, i => i.Id == ids.Old);
        Assert.Equal(ids.Old, Assert.Single(all).Id);
        Assert.False(all[0].IsActive);
        Assert.Empty(await products.LookupAsync("anvil"));
    }

    [Fact]
    public async Task Lookup_puts_an_exact_sku_or_barcode_match_first()
    {
        await CreateProductAsync(Input(sku: "KIT-0", name: "Aardvark bolt kit"));
        var exact = await CreateProductAsync(Input(sku: "BOLT-1", name: "Zebra", barcode: "bolt"));
        await CreateProductAsync(Input(sku: "OTHER", name: "Bolt cutter"));
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var results = await products.LookupAsync("bolt");

        Assert.Equal(exact.Id, results[0].Id); // barcode == "bolt" beats name order
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Lookup_with_no_query_lists_by_name_and_respects_take()
    {
        var (seedContext, _, _) = NewRequest();
        await using (seedContext)
        {
            seedContext.Products.AddRange(Enumerable.Range(1, 80).Select(i => new Product { Sku = $"L{i:000}", Name = $"Lookup {i:000}" }));
            await seedContext.SaveChangesAsync();
        }

        var (context, products, _) = NewRequest();
        await using var _ = context;

        Assert.Equal(20, (await products.LookupAsync(null)).Count); // default take
        Assert.Equal("L001", (await products.LookupAsync("  "))[0].Sku);
        Assert.Equal(5, (await products.LookupAsync(null, take: 5)).Count);
        Assert.Equal(ProductService.MaxLookupTake, (await products.LookupAsync(null, take: 1000)).Count);
        Assert.Single(await products.LookupAsync(null, take: 0)); // clamped up to 1
    }

    [Fact]
    public async Task Lookup_treats_wildcards_literally()
    {
        await CreateProductAsync(Input(sku: "A", name: "Plain"));

        var (context, products, _) = NewRequest();
        await using var _ = context;

        Assert.Empty(await products.LookupAsync("%"));
        Assert.Empty(await products.LookupAsync("_"));
    }

    // ---- misc --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sku_is_editable_until_stock_history_exists()
    {
        // The "locked after a transaction" case lives in StockServiceTests.
        var created = await CreateProductAsync(Input(sku: "EDITABLE"));
        var (context, products, _) = NewRequest();
        await using var _ = context;

        var result = await products.UpdateAsync(created.Id, created.RowVersion, Input(sku: "RENAMED"));

        Assert.True(result.Succeeded);
        Assert.False(result.Value.SkuLocked);
    }
}
