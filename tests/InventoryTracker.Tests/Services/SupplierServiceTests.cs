using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-04-2: SupplierService rules (F04 business rules, validation, links, preferred switching, lookups).</summary>
public class SupplierServiceTests : CatalogTestBase
{
    private static string[] Codes(Result result) => [.. result.Errors.Select(e => e.Code)];

    private static SupplierInput Supplier(
        string? name = "Acme", string? contact = null, string? email = null, string? phone = null,
        string? address = null, string? website = null, string? notes = null) =>
        new(name, contact, email, phone, address, website, notes);

    private static SupplierLinkInput Link(string? sku = null, long cost = 0, int? lead = null, int min = 1, bool preferred = false) =>
        new(sku, cost, lead, min, preferred);

    private (AppDbContext Context, SupplierService Suppliers) New(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] extra)
    {
        var (context, _, _) = NewRequest(extra);
        return (context, new SupplierService(context));
    }

    private async Task<T> RunAsync<T>(Func<SupplierService, Task<T>> action)
    {
        var (context, suppliers) = New();
        await using (context)
        {
            return await action(suppliers);
        }
    }

    private async Task<SupplierDetail> CreateSupplierAsync(string name = "Acme", string? contact = null, string? email = null, bool archived = false)
    {
        var created = await RunAsync(s => s.CreateAsync(Supplier(name, contact, email)));
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Message)));
        if (archived)
        {
            Assert.True((await RunAsync(s => s.SetActiveAsync(created.Value.Id, false))).Succeeded);
        }

        return created.Value;
    }

    private async Task<int> ProductAsync(string sku, bool archived = false)
    {
        var id = (await CreateProductAsync(Input(sku: sku, name: "Product " + sku))).Id;
        if (archived)
        {
            await ArchiveAsync(id);
        }

        return id;
    }

    private async Task<List<ProductSupplier>> LinksAsync()
    {
        var (context, _) = New();
        await using (context)
        {
            return await context.ProductSuppliers.AsNoTracking().OrderBy(l => l.ProductId).ThenBy(l => l.SupplierId).ToListAsync();
        }
    }

    // ---- create / update -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_stores_trimmed_values_and_applies_defaults()
    {
        var result = await RunAsync(s => s.CreateAsync(Supplier("  Acme Ltd ", " Jo ", " jo@acme.example ", " 555 0100 ", "  1 Main St  \r\n  Springfield  ", "acme.example", " note ")));

        Assert.True(result.Succeeded);
        var supplier = result.Value;
        Assert.True(supplier.Id > 0);
        Assert.Equal("Acme Ltd", supplier.Name);
        Assert.Equal("Jo", supplier.ContactName);
        Assert.Equal("jo@acme.example", supplier.Email);
        Assert.Equal("555 0100", supplier.Phone);
        Assert.Equal("1 Main St\nSpringfield", supplier.Address);
        Assert.Equal("https://acme.example", supplier.Website);
        Assert.Equal("note", supplier.Notes);
        Assert.True(supplier.IsActive);
        Assert.Equal(0, supplier.ProductCount);
        Assert.Equal(1L, supplier.RowVersion);
    }

    [Fact]
    public async Task Create_turns_blank_optional_text_into_null_and_keeps_an_explicit_scheme()
    {
        var supplier = (await RunAsync(s => s.CreateAsync(Supplier("Bare", " ", "", "  ", "  \n ", "  ", "")))).Value;
        Assert.All(new[] { supplier.ContactName, supplier.Email, supplier.Phone, supplier.Address, supplier.Website, supplier.Notes }, Assert.Null);

        var http = (await RunAsync(s => s.CreateAsync(Supplier("Plain", website: "http://old.example/shop?x=1")))).Value;
        Assert.Equal("http://old.example/shop?x=1", http.Website);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_name_is_required(string? name)
    {
        var result = await RunAsync(s => s.CreateAsync(Supplier(name)));

        Assert.Equal(["supplier.name_required"], Codes(result));
        Assert.Equal(SupplierFields.Name, result.Errors[0].Field);
    }

    [Fact]
    public async Task Names_are_unique_case_insensitively_including_archived_suppliers()
    {
        await CreateSupplierAsync("Acme", archived: true);

        var clash = await RunAsync(s => s.CreateAsync(Supplier("  ACME ")));
        Assert.Equal(["supplier.name_taken"], Codes(clash));
        Assert.Contains("may be archived", clash.Errors[0].Message);

        Assert.True((await RunAsync(s => s.CreateAsync(Supplier("Acme 2")))).Succeeded);
    }

    [Fact]
    public async Task Field_lengths_and_formats_are_checked_and_all_problems_are_reported_together()
    {
        var result = await RunAsync(s => s.CreateAsync(new SupplierInput(
            new string('n', 201), new string('c', 101), "not-an-email", new string('p', 51), new string('a', 501), "javascript:alert(1)", new string('x', 2001))));

        Assert.Equal(
            ["supplier.name_too_long", "supplier.contactname_too_long", "supplier.phone_too_long", "supplier.address_too_long", "supplier.notes_too_long", "supplier.email_invalid", "supplier.website_invalid"],
            Codes(result));
        Assert.Equal(
            [SupplierFields.Name, SupplierFields.ContactName, SupplierFields.Phone, SupplierFields.Address, SupplierFields.Notes, SupplierFields.Email, SupplierFields.Website],
            result.Errors.Select(e => e.Field));
    }

    [Theory]
    [InlineData("orders@acme.example", true)]
    [InlineData("a.b+c@sub.acme.co.uk", true)]
    [InlineData("nobody", false)]
    [InlineData("a@b", false)]
    [InlineData("@acme.example", false)]
    [InlineData("two words@acme.example", false)]
    [InlineData("Jo <jo@acme.example>", false)]
    [InlineData("a@b.example, c@d.example", false)]
    public async Task Email_must_be_a_single_plain_address(string email, bool valid)
    {
        var result = await RunAsync(s => s.CreateAsync(Supplier("E-" + Guid.NewGuid().ToString("N"), email: email)));

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Equal(["supplier.email_invalid"], Codes(result));
        }
    }

    [Theory]
    [InlineData("https://acme.example", true)]
    [InlineData("acme.example/shop", true)]
    [InlineData("HTTP://ACME.EXAMPLE", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://acme.example", false)]
    [InlineData("data:text/html;base64,AAAA", false)]
    [InlineData("http://", false)]
    [InlineData("acme example", false)]
    [InlineData("https://acme example.com", false)]
    public async Task Website_must_be_a_web_address_so_it_is_safe_to_render_as_a_link(string website, bool valid)
    {
        var result = await RunAsync(s => s.CreateAsync(Supplier("W-" + Guid.NewGuid().ToString("N"), website: website)));

        Assert.Equal(valid, result.Succeeded);
        if (valid)
        {
            Assert.StartsWith("http", result.Value.Website!, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(["supplier.website_invalid"], Codes(result));
        }
    }

    [Fact]
    public async Task Update_changes_the_fields_bumps_the_version_and_may_keep_its_own_name()
    {
        var created = await CreateSupplierAsync("Acme");

        var result = await RunAsync(s => s.UpdateAsync(created.Id, created.RowVersion, Supplier("acme", contact: "New Contact", phone: "123")));

        Assert.True(result.Succeeded);
        Assert.Equal("acme", result.Value.Name);
        Assert.Equal("New Contact", result.Value.ContactName);
        Assert.Equal(created.RowVersion + 1, result.Value.RowVersion);
        Assert.True(result.Value.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task Update_rejects_a_stale_version_a_duplicate_name_and_an_unknown_id()
    {
        var acme = await CreateSupplierAsync("Acme");
        await CreateSupplierAsync("Globex");

        Assert.True((await RunAsync(s => s.UpdateAsync(acme.Id, acme.RowVersion, Supplier("Acme", phone: "1")))).Succeeded);

        var stale = await RunAsync(s => s.UpdateAsync(acme.Id, acme.RowVersion, Supplier("Acme", phone: "2")));
        Assert.Equal(["supplier.conflict"], Codes(stale));

        var fresh = (await RunAsync(s => s.GetAsync(acme.Id)))!;
        var duplicate = await RunAsync(s => s.UpdateAsync(acme.Id, fresh.RowVersion, Supplier("GLOBEX")));
        Assert.Equal(["supplier.name_taken"], Codes(duplicate));

        var missing = await RunAsync(s => s.UpdateAsync(9999, 1, Supplier("Whoever")));
        Assert.Equal(["supplier.not_found"], Codes(missing));
    }

    [Fact]
    public async Task A_concurrent_save_between_load_and_save_is_reported_as_a_conflict()
    {
        var acme = await CreateSupplierAsync("Acme");
        var race = new RunSqlBeforeSave($"UPDATE Suppliers SET Phone = 'other', RowVersion = RowVersion + 1 WHERE Id = {acme.Id}");
        var (context, suppliers) = New(race);
        await using (context)
        {
            var result = await suppliers.UpdateAsync(acme.Id, acme.RowVersion, Supplier("Acme", phone: "mine"));

            Assert.Equal(["supplier.conflict"], Codes(result));
        }

        Assert.Equal("other", (await RunAsync(s => s.GetAsync(acme.Id)))!.Phone);
    }

    [Fact]
    public async Task A_duplicate_name_that_slips_past_the_precheck_is_still_a_field_error()
    {
        // The pre-check sees no clash; the unique index (hit on SaveChanges) is the real guard.
        var race = new RunSqlBeforeSave($"UPDATE Suppliers SET Name = 'Wanted' WHERE Id = {(await CreateSupplierAsync("Racer")).Id}");
        var (context, suppliers) = New(race);
        await using (context)
        {
            var result = await suppliers.CreateAsync(Supplier("Wanted"));

            Assert.Equal(["supplier.name_taken"], Codes(result));
            Assert.Equal(SupplierFields.Name, result.Errors[0].Field);
        }

    }

    [Fact]
    public async Task Archive_and_restore_are_idempotent_and_check_the_version_when_given()
    {
        var supplier = await CreateSupplierAsync("Acme");

        Assert.True((await RunAsync(s => s.SetActiveAsync(supplier.Id, false, supplier.RowVersion))).Succeeded);
        Assert.False((await RunAsync(s => s.GetAsync(supplier.Id)))!.IsActive);
        Assert.True((await RunAsync(s => s.SetActiveAsync(supplier.Id, false))).Succeeded); // already archived

        var stale = await RunAsync(s => s.SetActiveAsync(supplier.Id, true, supplier.RowVersion));
        Assert.Equal(["supplier.conflict"], Codes(stale));

        Assert.True((await RunAsync(s => s.SetActiveAsync(supplier.Id, true))).Succeeded);
        Assert.True((await RunAsync(s => s.GetAsync(supplier.Id)))!.IsActive);
        Assert.Equal(["supplier.not_found"], Codes(await RunAsync(s => s.SetActiveAsync(9999, false))));
    }

    // ---- search --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_matches_name_contact_and_email_and_treats_wildcards_literally()
    {
        await CreateSupplierAsync("Alpha Tools", contact: "Jo Bloggs", email: "sales@alpha.example");
        await CreateSupplierAsync("Bravo 50% Off", contact: "Pat");
        await CreateSupplierAsync("Charlie", email: "orders@charlie.example");

        async Task<string[]> Find(string text) =>
            [.. (await RunAsync(s => s.SearchAsync(new SupplierListQuery { Search = text }))).Items.Select(i => i.Name)];

        Assert.Equal(["Alpha Tools"], await Find("tools"));
        Assert.Equal(["Alpha Tools"], await Find("bloggs"));
        Assert.Equal(["Charlie"], await Find("charlie.example"));
        Assert.Equal(["Bravo 50% Off"], await Find("50%"));
        Assert.Empty(await Find("%zzz"));
        Assert.Equal(3, (await RunAsync(s => s.SearchAsync(new SupplierListQuery { Search = "  " }))).TotalCount);
    }

    [Fact]
    public async Task Search_filters_by_status_sorts_pages_and_counts_products()
    {
        var a = await CreateSupplierAsync("Alpha", contact: "Zed");
        var b = await CreateSupplierAsync("Bravo", contact: "Amy");
        await CreateSupplierAsync("Charlie");
        var old = await CreateSupplierAsync("Old Co", archived: true);

        var p1 = await ProductAsync("P-1");
        var p2 = await ProductAsync("P-2");
        Assert.True((await RunAsync(s => s.LinkProductAsync(p1, b.Id, Link()))).Succeeded);
        Assert.True((await RunAsync(s => s.LinkProductAsync(p2, b.Id, Link()))).Succeeded);
        Assert.True((await RunAsync(s => s.LinkProductAsync(p1, a.Id, Link()))).Succeeded);

        async Task<string[]> Names(SupplierListQuery query) => [.. (await RunAsync(s => s.SearchAsync(query))).Items.Select(i => i.Name)];

        Assert.Equal(["Alpha", "Bravo", "Charlie"], await Names(new()));
        Assert.Equal(["Old Co"], await Names(new() { Status = SupplierStatus.Archived }));
        Assert.Equal(["Alpha", "Bravo", "Charlie", "Old Co"], await Names(new() { Status = SupplierStatus.All }));
        Assert.Equal(["Charlie", "Bravo", "Alpha"], await Names(new() { Sort = "name", Desc = true }));
        Assert.Equal(["Bravo", "Alpha", "Charlie"], await Names(new() { Sort = "contact" })); // Amy, Zed, none last
        Assert.Equal(["Bravo", "Alpha", "Charlie"], await Names(new() { Sort = "products", Desc = true }));

        var page = await RunAsync(s => s.SearchAsync(new SupplierListQuery { PageSize = 2, Page = 2 }));
        Assert.Equal(["Charlie"], page.Items.Select(i => i.Name));
        Assert.Equal(3, page.TotalCount);

        var counts = (await RunAsync(s => s.SearchAsync(new SupplierListQuery()))).Items.ToDictionary(i => i.Name, i => i.ProductCount);
        Assert.Equal(1, counts["Alpha"]);
        Assert.Equal(2, counts["Bravo"]);
        Assert.Equal(0, counts["Charlie"]);
        Assert.Equal(0, (await RunAsync(s => s.GetAsync(old.Id)))!.ProductCount);
        Assert.Equal(2, (await RunAsync(s => s.GetAsync(b.Id)))!.ProductCount);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_supplier()
    {
        Assert.Null(await RunAsync(s => s.GetAsync(12345)));
    }

    // ---- links ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Linking_stores_the_terms_and_shows_both_sides()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");

        var result = await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link("  AC-77 ", 1250, 7, 12)));

        Assert.True(result.Succeeded);
        var item = result.Value;
        Assert.Equal("W-1", item.Sku);
        Assert.Equal("Product W-1", item.ProductName);
        Assert.Equal("Acme", item.SupplierName);
        Assert.Equal("AC-77", item.SupplierSku);
        Assert.Equal(1250, item.UnitCostCents);
        Assert.Equal(7, item.LeadTimeDays);
        Assert.Equal(12, item.MinOrderQuantity);
        Assert.False(item.IsPreferred);

        Assert.Single(await RunAsync(s => s.GetProductsAsync(supplier.Id)));
        Assert.Single(await RunAsync(s => s.GetForProductAsync(product)));
        Assert.Equal(1, (await RunAsync(s => s.GetAsync(supplier.Id)))!.ProductCount);
    }

    [Fact]
    public async Task Linking_the_same_pair_again_updates_the_link_instead_of_duplicating_it()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link("OLD", 100, 3, 2)))).Succeeded);

        var again = await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link(null, 250, null, 5)));

        Assert.True(again.Succeeded);
        var link = Assert.Single(await LinksAsync());
        Assert.Null(link.SupplierSku);
        Assert.Equal(250, link.UnitCostCents);
        Assert.Null(link.LeadTimeDays);
        Assert.Equal(5, link.MinOrderQuantity);
        Assert.True(link.UpdatedAt >= link.CreatedAt);
    }

    [Theory]
    [InlineData(-1, null, 1, "supplier.cost_negative")]
    [InlineData(0, -1, 1, "supplier.lead_time_invalid")]
    [InlineData(0, 3651, 1, "supplier.lead_time_invalid")]
    [InlineData(0, null, 0, "supplier.min_order_invalid")]
    [InlineData(0, null, -4, "supplier.min_order_invalid")]
    [InlineData(0, null, 1_000_000_001, "supplier.min_order_invalid")]
    public async Task Link_numbers_are_validated(long cost, int? lead, int min, string code)
    {
        var supplier = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");

        var result = await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link(null, cost, lead, min)));

        Assert.Equal([code], Codes(result));
        Assert.Empty(await LinksAsync());
    }

    [Fact]
    public async Task Link_boundaries_are_accepted_and_a_long_supplier_sku_is_not()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");

        Assert.True((await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link(new string('s', 64), 0, 0, 1)))).Succeeded);
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link(null, 0, 3650, 1_000_000_000)))).Succeeded);

        var tooLong = await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link(new string('s', 65))));
        Assert.Equal(["supplier.sku_too_long"], Codes(tooLong));
        Assert.Equal(SupplierLinkFields.SupplierSku, tooLong.Errors[0].Field);
    }

    [Fact]
    public async Task Linking_needs_an_existing_product_and_supplier()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");

        Assert.Equal(["supplier.product_missing"], Codes(await RunAsync(s => s.LinkProductAsync(9999, supplier.Id, Link()))));
        Assert.Equal(["supplier.not_found"], Codes(await RunAsync(s => s.LinkProductAsync(product, 9999, Link()))));
        Assert.Empty(await LinksAsync());
    }

    [Fact]
    public async Task A_new_link_needs_an_active_supplier_and_an_active_product()
    {
        var archivedSupplier = await CreateSupplierAsync("Gone", archived: true);
        var active = await CreateSupplierAsync("Here");
        var archivedProduct = await ProductAsync("OLD-1", archived: true);
        var product = await ProductAsync("W-1");

        Assert.Equal(["supplier.link_supplier_archived"], Codes(await RunAsync(s => s.LinkProductAsync(product, archivedSupplier.Id, Link()))));
        Assert.Equal(["supplier.link_product_archived"], Codes(await RunAsync(s => s.LinkProductAsync(archivedProduct, active.Id, Link()))));
        Assert.Equal(
            ["supplier.link_product_archived", "supplier.link_supplier_archived"],
            Codes(await RunAsync(s => s.LinkProductAsync(archivedProduct, archivedSupplier.Id, Link()))));
        Assert.Empty(await LinksAsync());
    }

    [Fact]
    public async Task An_existing_link_can_still_be_edited_after_the_supplier_or_product_is_archived()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link(cost: 100)))).Succeeded);

        Assert.True((await RunAsync(s => s.SetActiveAsync(supplier.Id, false))).Succeeded);
        await ArchiveAsync(product);

        var edit = await RunAsync(s => s.LinkProductAsync(product, supplier.Id, Link("NEW", 300)));
        Assert.True(edit.Succeeded);
        Assert.Equal(300, edit.Value.UnitCostCents);
        Assert.False(edit.Value.SupplierIsActive);
        Assert.False(edit.Value.ProductIsActive);

        Assert.True((await RunAsync(s => s.UnlinkProductAsync(product, supplier.Id))).Succeeded);
        Assert.Empty(await LinksAsync());
    }

    [Fact]
    public async Task Making_a_link_preferred_clears_the_flag_on_the_products_other_links()
    {
        var acme = await CreateSupplierAsync("Acme");
        var globex = await CreateSupplierAsync("Globex");
        var initech = await CreateSupplierAsync("Initech");
        var product = await ProductAsync("W-1");
        var other = await ProductAsync("W-2");

        Assert.True((await RunAsync(s => s.LinkProductAsync(product, acme.Id, Link(preferred: true)))).Succeeded);
        Assert.True((await RunAsync(s => s.LinkProductAsync(other, acme.Id, Link(preferred: true)))).Succeeded);
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, globex.Id, Link()))).Succeeded);

        // Linking a third supplier as preferred switches it.
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, initech.Id, Link(preferred: true)))).Succeeded);

        var preferred = (await LinksAsync()).Where(l => l.IsPreferred).Select(l => (l.ProductId, l.SupplierId)).ToList();
        Assert.Equal([(product, initech.Id), (other, acme.Id)], preferred);

        // SetPreferred switches again; the other product is untouched.
        Assert.True((await RunAsync(s => s.SetPreferredAsync(product, globex.Id))).Succeeded);
        preferred = [.. (await LinksAsync()).Where(l => l.IsPreferred).Select(l => (l.ProductId, l.SupplierId))];
        Assert.Equal([(product, globex.Id), (other, acme.Id)], preferred);
    }

    [Fact]
    public async Task Editing_a_link_with_preferred_off_clears_it_and_leaves_the_product_without_a_preferred_supplier()
    {
        var acme = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, acme.Id, Link(preferred: true)))).Succeeded);

        Assert.True((await RunAsync(s => s.LinkProductAsync(product, acme.Id, Link(preferred: false)))).Succeeded);

        Assert.DoesNotContain(await LinksAsync(), l => l.IsPreferred);
    }

    [Fact]
    public async Task SetPreferred_toggles_is_idempotent_and_needs_an_existing_link()
    {
        var acme = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, acme.Id, Link()))).Succeeded);

        Assert.True((await RunAsync(s => s.SetPreferredAsync(product, acme.Id, true))).Succeeded);
        Assert.True((await RunAsync(s => s.SetPreferredAsync(product, acme.Id, true))).Succeeded);
        Assert.True((await LinksAsync()).Single().IsPreferred);

        Assert.True((await RunAsync(s => s.SetPreferredAsync(product, acme.Id, false))).Succeeded);
        Assert.False((await LinksAsync()).Single().IsPreferred);

        Assert.Equal(["supplier.link_missing"], Codes(await RunAsync(s => s.SetPreferredAsync(product, 9999))));
    }

    [Fact]
    public async Task An_archived_supplier_cannot_become_preferred_but_can_stop_being_preferred()
    {
        var acme = await CreateSupplierAsync("Acme");
        var product = await ProductAsync("W-1");
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, acme.Id, Link()))).Succeeded);
        Assert.True((await RunAsync(s => s.SetActiveAsync(acme.Id, false))).Succeeded);

        Assert.Equal(["supplier.preferred_archived"], Codes(await RunAsync(s => s.SetPreferredAsync(product, acme.Id))));
        Assert.Equal(["supplier.preferred_archived"], Codes(await RunAsync(s => s.LinkProductAsync(product, acme.Id, Link(preferred: true)))));
        Assert.DoesNotContain(await LinksAsync(), l => l.IsPreferred);

        // Preferred before it was archived: it stays until someone changes it, and can be cleared.
        var globex = await CreateSupplierAsync("Globex");
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, globex.Id, Link(preferred: true)))).Succeeded);
        Assert.True((await RunAsync(s => s.SetActiveAsync(globex.Id, false))).Succeeded);
        Assert.True((await LinksAsync()).Single(l => l.SupplierId == globex.Id).IsPreferred);
        Assert.True((await RunAsync(s => s.SetPreferredAsync(product, globex.Id, false))).Succeeded);
        Assert.DoesNotContain(await LinksAsync(), l => l.IsPreferred);
    }

    [Fact]
    public async Task A_preferred_race_that_slips_past_the_checks_is_reported_and_saves_nothing()
    {
        var acme = await CreateSupplierAsync("Acme");
        var globex = await CreateSupplierAsync("Globex");
        var product = await ProductAsync("W-1");

        // Between our checks and our insert, another request makes Acme the preferred supplier.
        var race = new RunSqlBeforeSave(
            $"INSERT INTO ProductSuppliers (ProductId, SupplierId, UnitCostCents, MinOrderQuantity, IsPreferred, CreatedAt, UpdatedAt) VALUES ({product}, {acme.Id}, 0, 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        var (context, suppliers) = New(race);
        await using (context)
        {
            var result = await suppliers.LinkProductAsync(product, globex.Id, Link(preferred: true));

            Assert.Equal(["supplier.link_conflict"], Codes(result));
        }

        // Nothing half-done is left behind (the injected row shared our transaction, so it is rolled back with it).
        Assert.DoesNotContain(await LinksAsync(), l => l.SupplierId == globex.Id);
    }

    [Fact]
    public async Task Unlinking_removes_only_that_link_and_is_idempotent()
    {
        var acme = await CreateSupplierAsync("Acme");
        var globex = await CreateSupplierAsync("Globex");
        var product = await ProductAsync("W-1");
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, acme.Id, Link()))).Succeeded);
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, globex.Id, Link()))).Succeeded);

        Assert.True((await RunAsync(s => s.UnlinkProductAsync(product, acme.Id))).Succeeded);
        Assert.True((await RunAsync(s => s.UnlinkProductAsync(product, acme.Id))).Succeeded);
        Assert.True((await RunAsync(s => s.UnlinkProductAsync(9999, 9999))).Succeeded);

        Assert.Equal(globex.Id, Assert.Single(await LinksAsync()).SupplierId);
        Assert.Null(await RunAsync(s => s.GetLinkAsync(product, acme.Id)));
        Assert.NotNull(await RunAsync(s => s.GetLinkAsync(product, globex.Id)));
        Assert.NotNull(await RunAsync(s => s.GetAsync(acme.Id))); // the supplier itself stays
    }

    [Fact]
    public async Task A_products_suppliers_list_the_preferred_one_first_and_a_suppliers_products_include_archived_ones()
    {
        var acme = await CreateSupplierAsync("Acme");
        var globex = await CreateSupplierAsync("Globex");
        var initech = await CreateSupplierAsync("Initech");
        var product = await ProductAsync("W-1");
        foreach (var s in new[] { acme, globex, initech })
        {
            Assert.True((await RunAsync(x => x.LinkProductAsync(product, s.Id, Link()))).Succeeded);
        }

        Assert.True((await RunAsync(s => s.SetPreferredAsync(product, initech.Id))).Succeeded);

        Assert.Equal(["Initech", "Acme", "Globex"], (await RunAsync(s => s.GetForProductAsync(product))).Select(i => i.SupplierName));

        var archivedProduct = await ProductAsync("OLD-1");
        Assert.True((await RunAsync(s => s.LinkProductAsync(archivedProduct, acme.Id, Link()))).Succeeded);
        await ArchiveAsync(archivedProduct);

        var products = await RunAsync(s => s.GetProductsAsync(acme.Id));
        Assert.Equal(["OLD-1", "W-1"], products.Select(i => i.Sku)); // by product name: "Product OLD-1" < "Product W-1"
        Assert.False(products[0].ProductIsActive);
        Assert.Empty(await RunAsync(s => s.GetProductsAsync(9999)));
    }

    // ---- lookups -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lookup_excludes_archived_suppliers_by_default_puts_an_exact_name_first_and_escapes_wildcards()
    {
        await CreateSupplierAsync("Acme Tools", contact: "Jo");
        await CreateSupplierAsync("Acme");
        await CreateSupplierAsync("Zeta 100%");
        await CreateSupplierAsync("Acme Archived", archived: true);

        async Task<string[]> Find(string? text, bool activeOnly = true, int take = 20) =>
            [.. (await RunAsync(s => s.LookupAsync(text, activeOnly, take))).Select(i => i.Name)];

        Assert.Equal(["Acme", "Acme Tools"], await Find("acme"));
        Assert.Equal(["Acme", "Acme Archived", "Acme Tools"], await Find("ACME", activeOnly: false));
        Assert.Equal(["Acme Tools"], await Find("jo")); // by contact
        Assert.Equal(["Zeta 100%"], await Find("100%"));
        Assert.Empty(await Find("%zz"));
        Assert.Equal(["Acme", "Acme Tools", "Zeta 100%"], await Find(null));
        Assert.Single(await Find(null, take: 1));
        Assert.Single(await Find(null, take: 0)); // clamped to at least 1
    }

    [Fact]
    public async Task Unlinked_lists_only_active_suppliers_the_product_does_not_have_yet()
    {
        var acme = await CreateSupplierAsync("Acme");
        await CreateSupplierAsync("Globex");
        await CreateSupplierAsync("Retired", archived: true);
        var product = await ProductAsync("W-1");
        Assert.True((await RunAsync(s => s.LinkProductAsync(product, acme.Id, Link()))).Succeeded);

        Assert.Equal(["Globex"], (await RunAsync(s => s.ListUnlinkedForProductAsync(product))).Select(i => i.Name));
        Assert.Equal(["Acme", "Globex"], (await RunAsync(s => s.ListUnlinkedForProductAsync(9999))).Select(i => i.Name));
    }

    // ---- registration --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_service_resolves_from_the_real_app_container()
    {
        using var factory = new AppFactory();
        using var _ = factory.CreateClient();

        await factory.WithScopeAsync(async sp =>
        {
            var suppliers = sp.GetRequiredService<ISupplierService>();
            Assert.True((await suppliers.CreateAsync(Supplier("Registered"))).Succeeded);
            Assert.Single((await suppliers.SearchAsync(new SupplierListQuery())).Items);
        });
    }
}
