using System.Net;
using System.Text.RegularExpressions;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-04-3: supplier list, detail, create/edit, archive and the product–supplier links on both pages.</summary>
public partial class SupplierPagesTests
{
    [GeneratedRegex("name=\"Input.RowVersion\" value=\"(\\d+)\"")]
    private static partial Regex RowVersionPattern();

    private static HttpRequestMessage Htmx(string url, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return request;
    }

    private static async Task<HttpResponseMessage> HtmxPostAsync(
        HttpClient client, string url, IEnumerable<(string, string)> fields, string tokenPage)
    {
        var request = Htmx(url, HttpMethod.Post);
        request.Headers.Add("RequestVerificationToken", await WebForms.TokenAsync(client, tokenPage));
        request.Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.SendAsync(request);
    }

    private static async Task<SupplierDetail> SeedSupplierAsync(AppFactory factory, string name, string? contact = null, string? email = null)
    {
        SupplierDetail? created = null;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<ISupplierService>().CreateAsync(new SupplierInput(name, contact, email, null, null, null, null));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            created = result.Value;
        });
        return created!;
    }

    private static async Task<ProductDetail> SeedProductAsync(AppFactory factory, string sku, string name = "Widget")
    {
        ProductDetail? created = null;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<IProductService>().CreateAsync(new ProductInput(sku, name, null, null, null, null, null, 0, 0, 0));
            Assert.True(result.Succeeded);
            created = result.Value;
        });
        return created!;
    }

    private static Task LinkAsync(AppFactory factory, int productId, int supplierId, bool preferred = false, long cost = 1250) =>
        factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<ISupplierService>()
                .LinkProductAsync(productId, supplierId, new SupplierLinkInput("S-1", cost, 7, 5, preferred));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        });

    private static async Task<IReadOnlyList<ProductSupplierItem>> LinksOfAsync(AppFactory factory, int productId)
    {
        IReadOnlyList<ProductSupplierItem> items = [];
        await factory.WithScopeAsync(async sp => items = await sp.GetRequiredService<ISupplierService>().GetForProductAsync(productId));
        return items;
    }

    private static (string, string)[] AddFields(string view, int productId, int supplierId, string cost = "12.50", bool preferred = false)
    {
        var fields = new List<(string, string)>
        {
            ("mode", "add"), ("view", view), ("productId", productId.ToString()), ("supplierId", supplierId.ToString()),
            ("supplierSku", "X-9"), ("unitCost", cost), ("leadTimeDays", "3"), ("minOrderQuantity", "10"),
        };
        if (preferred)
        {
            fields.Add(("isPreferred", "true"));
        }

        return [.. fields];
    }

    // ---- list ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_shows_suppliers_and_filters_by_search_and_status()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await SeedSupplierAsync(factory, "Acme Tools", "Jo Bloggs", "jo@acme.test");
        var old = await SeedSupplierAsync(factory, "Old Co");
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<ISupplierService>().SetActiveAsync(old.Id, false)).Succeeded));

        var html = await client.GetStringAsync("/Suppliers");
        Assert.Contains("Acme Tools", html);
        Assert.Contains("mailto:jo@acme.test", html);
        Assert.DoesNotContain("Old Co", html);

        var searched = await (await client.SendAsync(Htmx("/Suppliers?search=bloggs"))).Content.ReadAsStringAsync();
        Assert.Contains("Acme Tools", searched);
        Assert.DoesNotContain("<html", searched);

        var archived = await (await client.SendAsync(Htmx("/Suppliers?status=Archived"))).Content.ReadAsStringAsync();
        Assert.Contains("Old Co", archived);
        Assert.DoesNotContain("Acme Tools", archived);

        Assert.Contains("No suppliers match", await (await client.SendAsync(Htmx("/Suppliers?search=zzz"))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_empty_list_shows_an_empty_state_with_a_create_link_only_for_editors()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        using var viewer = factory.CreateClientAs("Viewer");

        var adminHtml = await admin.GetStringAsync("/Suppliers");
        Assert.Contains("No suppliers yet", adminHtml);
        Assert.Contains("/Suppliers/Edit", adminHtml);

        var viewerHtml = await viewer.GetStringAsync("/Suppliers");
        Assert.Contains("No suppliers yet", viewerHtml);
        Assert.DoesNotContain("/Suppliers/Edit", viewerHtml);
    }

    [Fact]
    public async Task The_main_nav_links_to_suppliers()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");

        Assert.Contains("<span>Suppliers</span></a>", await client.GetStringAsync("/"));
    }

    // ---- create / edit / archive ------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_supplier_redirects_to_its_detail_page_and_keeps_address_line_breaks()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");

        var response = await WebForms.PostFormAsync(client, "/Suppliers/Edit",
        [
            ("Input.Name", "Acme"), ("Input.ContactName", "Jo"), ("Input.Email", "jo@acme.test"), ("Input.Phone", "555 0100"),
            ("Input.Address", "1 Main St\r\nSpringfield"), ("Input.Website", "acme.test"), ("Input.Notes", "Net 30"), ("Input.RowVersion", "0"),
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var path = response.RedirectPath();
        Assert.Matches("^/Suppliers/\\d+$", path);

        var html = await client.GetStringAsync(path);
        Assert.Contains("Acme", html);
        Assert.Contains("https://acme.test", html);
        Assert.Contains("1 Main St&#xA;Springfield", html); // the encoder writes the line break as a character reference; pre-line renders it
        Assert.Contains("Net 30", html);
    }

    [Fact]
    public async Task Invalid_input_and_duplicate_names_are_reported_on_the_form()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await SeedSupplierAsync(factory, "Acme");

        var blank = await WebForms.PostFormAsync(client, "/Suppliers/Edit", [("Input.Name", " "), ("Input.RowVersion", "0")]);
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        Assert.Contains("Name is required", await blank.Content.ReadAsStringAsync());

        var duplicate = await WebForms.PostFormAsync(client, "/Suppliers/Edit", [("Input.Name", "ACME"), ("Input.RowVersion", "0")]);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("already", await duplicate.Content.ReadAsStringAsync());

        var badEmail = await WebForms.PostFormAsync(client, "/Suppliers/Edit", [("Input.Name", "Other"), ("Input.Email", "nope"), ("Input.RowVersion", "0")]);
        Assert.Equal(HttpStatusCode.OK, badEmail.StatusCode);
        Assert.Contains("aria-invalid=\"true\"", await badEmail.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_supplier_form_has_Contact_and_Notes_sections_and_shows_field_errors_beside_the_control()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var fresh = await client.GetStringAsync("/Suppliers/Edit");
        Assert.Contains(">Contact</h2>", fresh);
        Assert.Contains(">Notes</h2>", fresh);
        Assert.DoesNotContain("class=\"field-error\"", fresh);

        var bad = await WebForms.PostFormAsync(client, "/Suppliers/Edit", [("Input.Name", "Other"), ("Input.Email", "nope"), ("Input.RowVersion", "0")]);
        var html = await bad.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("id=\"Input_Email-error\"", html);
        Assert.Matches("name=\"Input.Email\"[^>]*aria-invalid=\"true\"[^>]*aria-describedby=\"Input_Email-error\"", html);
        Assert.Contains("alert alert-error", html);

        var ok = await WebForms.PostFormAsync(client, "/Suppliers/Edit", [("Input.Name", "Fine Supply"), ("Input.RowVersion", "0")]);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [Fact]
    public async Task Editing_saves_changes_and_a_stale_version_is_a_conflict()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");

        var form = await client.GetStringAsync($"/Suppliers/Edit/{supplier.Id}");
        var version = RowVersionPattern().Match(form).Groups[1].Value;

        var saved = await WebForms.PostFormAsync(client, $"/Suppliers/Edit/{supplier.Id}", [("Input.Name", "Acme Ltd"), ("Input.RowVersion", version)]);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("Acme Ltd", await client.GetStringAsync($"/Suppliers/{supplier.Id}"));

        var stale = await WebForms.PostFormAsync(client, $"/Suppliers/Edit/{supplier.Id}", [("Input.Name", "Late"), ("Input.RowVersion", version)]);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Contains("Someone else saved", await stale.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Suppliers/Edit/9999")).StatusCode);
    }

    [Fact]
    public async Task Archiving_and_restoring_toggle_the_supplier_and_work_with_htmx()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var page = $"/Suppliers/{supplier.Id}";
        var html = await client.GetStringAsync(page);
        var version = Regex.Match(html, "name=\"rowVersion\" value=\"(\\d+)\"").Groups[1].Value;

        var archived = await HtmxPostAsync(client, $"{page}?handler=Archive", [("active", "false"), ("rowVersion", version)], page);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        Assert.Equal(page, archived.Headers.GetValues("HX-Redirect").Single());
        var after = await client.GetStringAsync(page);
        Assert.Contains("This supplier is archived", after);
        Assert.Contains("Restore", after);

        var restored = await WebForms.PostFormAsync(client, $"{page}?handler=Archive", [("active", "true"), ("rowVersion", Regex.Match(after, "name=\"rowVersion\" value=\"(\\d+)\"").Groups[1].Value)], page);
        Assert.Equal(HttpStatusCode.Redirect, restored.StatusCode);
        Assert.DoesNotContain("This supplier is archived", await client.GetStringAsync(page));
    }

    // ---- authorization ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Viewers_can_read_but_cannot_edit_archive_or_change_links()
    {
        using var factory = new AppFactory();
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1");
        await LinkAsync(factory, product.Id, supplier.Id);
        using var viewer = factory.CreateClientAs("Viewer");
        using var admin = factory.CreateClientAs("Admin");

        var detail = await viewer.GetStringAsync($"/Suppliers/{supplier.Id}");
        Assert.Contains("W-1", detail);
        Assert.DoesNotContain("/Suppliers/Edit", detail);
        Assert.DoesNotContain("Archive", detail);
        Assert.DoesNotContain("/Suppliers/Links", detail);
        Assert.DoesNotContain("/Suppliers/Links", await viewer.GetStringAsync($"/Products/{product.Id}"));

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Suppliers/Edit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Suppliers/Links?handler=Edit&view=Supplier&productId=1&supplierId=1")).StatusCode);

        var token = await WebForms.TokenAsync(admin, "/Suppliers");
        var post = await WebForms.PostFormAsync(viewer, $"/Suppliers/{supplier.Id}?handler=Archive", [("active", "false"), ("rowVersion", "0")], $"/Suppliers/{supplier.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        var link = await WebForms.PostFormAsync(viewer, "/Suppliers/Links?handler=Remove", [("view", "Supplier"), ("productId", product.Id.ToString()), ("supplierId", supplier.Id.ToString())], "/Suppliers");
        Assert.Equal(HttpStatusCode.Forbidden, link.StatusCode);
        Assert.Single(await LinksOfAsync(factory, product.Id));
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task Anonymous_requests_are_sent_to_sign_in()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/Suppliers");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", response.RedirectPath());
    }

    // ---- links: supplier page ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_supplier_page_lists_linked_products_with_terms_and_the_preferred_star()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1", "Widget");
        await LinkAsync(factory, product.Id, supplier.Id, preferred: true, cost: 1250);

        var html = await client.GetStringAsync($"/Suppliers/{supplier.Id}");

        Assert.Contains("W-1", html);
        Assert.Contains("Widget", html);
        Assert.Contains("12.50", html);
        Assert.Contains("7 d", html);
        Assert.Contains("star-on", html);
        Assert.Contains("id=\"supplier-links\"", html);
    }

    [Fact]
    public async Task Adding_a_link_from_the_supplier_page_returns_the_refreshed_block()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1");

        var response = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save", AddFields("Supplier", product.Id, supplier.Id, preferred: true), $"/Suppliers/{supplier.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"supplier-links\"", html);
        Assert.DoesNotContain("<html", html);
        Assert.Contains("X-9", html);
        Assert.Contains("12.50", html);
        Assert.Contains("HX-Trigger", string.Join(",", response.Headers.Select(h => h.Key)));

        var link = Assert.Single(await LinksOfAsync(factory, product.Id));
        Assert.Equal(1250, link.UnitCostCents);
        Assert.Equal(10, link.MinOrderQuantity);
        Assert.True(link.IsPreferred);
    }

    [Fact]
    public async Task A_bad_add_returns_422_with_the_form_values_and_errors_and_changes_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1");

        var response = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save", AddFields("Supplier", product.Id, supplier.Id, cost: "abc"), $"/Suppliers/{supplier.Id}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"supplier-links\"", html);
        Assert.Contains("amount like 12.50", html);
        Assert.Contains("value=\"abc\"", html);
        Assert.Contains("X-9", html);
        Assert.Empty(await LinksOfAsync(factory, product.Id));

        var noProduct = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save",
            [("mode", "add"), ("view", "Supplier"), ("supplierId", supplier.Id.ToString()), ("unitCost", "1")], $"/Suppliers/{supplier.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noProduct.StatusCode);
        Assert.Contains("Choose a part", await noProduct.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Adding_the_same_pair_again_updates_the_terms_instead_of_failing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1");
        await LinkAsync(factory, product.Id, supplier.Id, cost: 100);

        var response = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save", AddFields("Supplier", product.Id, supplier.Id, cost: "2.00"), $"/Suppliers/{supplier.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, Assert.Single(await LinksOfAsync(factory, product.Id)).UnitCostCents);
    }

    // ---- links: inline edit, remove, preferred ------------------------------------------------------------------------

    [Fact]
    public async Task Inline_edit_loads_a_form_row_saves_and_cancels()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1");
        await LinkAsync(factory, product.Id, supplier.Id, cost: 1250);
        var query = $"view=Supplier&productId={product.Id}&supplierId={supplier.Id}";

        var edit = await client.SendAsync(Htmx($"/Suppliers/Links?handler=Edit&{query}"));
        var editHtml = await edit.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Contains("name=\"unitCost\" value=\"12.50\"", editHtml);
        Assert.Contains("<tr", editHtml);

        var row = await client.SendAsync(Htmx($"/Suppliers/Links?handler=Row&{query}"));
        Assert.Contains($"id=\"link-{product.Id}-{supplier.Id}\"", await row.Content.ReadAsStringAsync());

        var saved = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save",
        [
            ("mode", "edit"), ("view", "Supplier"), ("productId", product.Id.ToString()), ("supplierId", supplier.Id.ToString()),
            ("supplierSku", "NEW"), ("unitCost", "3.00"), ("leadTimeDays", ""), ("minOrderQuantity", "2"),
        ], $"/Suppliers/{supplier.Id}");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("#supplier-links", saved.Headers.GetValues("HX-Retarget").Single());
        Assert.Equal("outerHTML", saved.Headers.GetValues("HX-Reswap").Single());
        var link = Assert.Single(await LinksOfAsync(factory, product.Id));
        Assert.Equal((300, "NEW", 2, null), (link.UnitCostCents, link.SupplierSku, link.MinOrderQuantity, link.LeadTimeDays));

        var invalid = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save",
        [
            ("mode", "edit"), ("view", "Supplier"), ("productId", product.Id.ToString()), ("supplierId", supplier.Id.ToString()),
            ("unitCost", "-1"), ("minOrderQuantity", "0"),
        ], $"/Suppliers/{supplier.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Contains("inline-edit", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(300, Assert.Single(await LinksOfAsync(factory, product.Id)).UnitCostCents);

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Htmx("/Suppliers/Links?handler=Edit&view=Supplier&productId=999&supplierId=999"))).StatusCode);
    }

    [Fact]
    public async Task Removing_a_link_returns_the_block_without_it()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1");
        await LinkAsync(factory, product.Id, supplier.Id);

        var response = await HtmxPostAsync(client, "/Suppliers/Links?handler=Remove",
            [("view", "Supplier"), ("productId", product.Id.ToString()), ("supplierId", supplier.Id.ToString())], $"/Suppliers/{supplier.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No parts linked yet", await response.Content.ReadAsStringAsync());
        Assert.Empty(await LinksOfAsync(factory, product.Id));
    }

    [Fact]
    public async Task The_star_moves_the_preferred_supplier_and_can_clear_it()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var first = await SeedSupplierAsync(factory, "First");
        var second = await SeedSupplierAsync(factory, "Second");
        var product = await SeedProductAsync(factory, "W-1");
        await LinkAsync(factory, product.Id, first.Id, preferred: true);
        await LinkAsync(factory, product.Id, second.Id);
        var page = $"/Products/{product.Id}";

        var move = await HtmxPostAsync(client, "/Suppliers/Links?handler=Preferred",
            [("view", "Product"), ("productId", product.Id.ToString()), ("supplierId", second.Id.ToString()), ("preferred", "true")], page);

        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        var html = await move.Content.ReadAsStringAsync();
        Assert.Contains("id=\"product-suppliers\"", html);
        Assert.Single(Regex.Matches(html, "aria-pressed=\"true\""));
        Assert.Equal("Second", (await LinksOfAsync(factory, product.Id)).Single(l => l.IsPreferred).SupplierName);

        var clear = await HtmxPostAsync(client, "/Suppliers/Links?handler=Preferred",
            [("view", "Product"), ("productId", product.Id.ToString()), ("supplierId", second.Id.ToString()), ("preferred", "false")], page);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.DoesNotContain(await LinksOfAsync(factory, product.Id), l => l.IsPreferred);
    }

    [Fact]
    public async Task Without_htmx_link_actions_redirect_back_to_the_owning_page()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1");

        var added = await WebForms.PostFormAsync(client, "/Suppliers/Links?handler=Save", AddFields("Supplier", product.Id, supplier.Id), $"/Suppliers/{supplier.Id}");
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        Assert.Equal($"/Suppliers/{supplier.Id}", added.RedirectPath());

        var removed = await WebForms.PostFormAsync(client, "/Suppliers/Links?handler=Remove",
            [("view", "Product"), ("productId", product.Id.ToString()), ("supplierId", supplier.Id.ToString())], $"/Suppliers/{supplier.Id}");
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Equal($"/Products/{product.Id}", removed.RedirectPath());
        Assert.Empty(await LinksOfAsync(factory, product.Id));
    }

    // ---- links: product page ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_product_page_lists_its_suppliers_and_offers_only_unlinked_active_ones()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var linked = await SeedSupplierAsync(factory, "Linked Co");
        await SeedSupplierAsync(factory, "Free Co");
        var gone = await SeedSupplierAsync(factory, "Archived Co");
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<ISupplierService>().SetActiveAsync(gone.Id, false)).Succeeded));
        var product = await SeedProductAsync(factory, "W-1");
        await LinkAsync(factory, product.Id, linked.Id, preferred: true);

        var html = await client.GetStringAsync($"/Products/{product.Id}");

        Assert.Contains("id=\"product-suppliers\"", html);
        Assert.Contains("href=\"#suppliers\"", html);
        Assert.Contains($"/Suppliers/{linked.Id}\">Linked Co</a>", html);
        Assert.Contains(">Free Co</option>", html);
        Assert.DoesNotContain(">Linked Co</option>", html);
        Assert.DoesNotContain("Archived Co", html);
    }

    [Fact]
    public async Task Adding_a_supplier_from_the_product_page_returns_the_product_block()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var product = await SeedProductAsync(factory, "W-1");

        var response = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save", AddFields("Product", product.Id, supplier.Id, preferred: true), $"/Products/{product.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"product-suppliers\"", html);
        Assert.Contains("Acme", html);
        Assert.Contains("aria-pressed=\"true\"", html);

        var bad = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save",
            [("mode", "add"), ("view", "Product"), ("productId", product.Id.ToString()), ("unitCost", "1")], $"/Products/{product.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("Choose a supplier", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Archived_owners_cannot_take_new_links_but_keep_existing_ones_visible()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var other = await SeedProductAsync(factory, "W-2");
        var product = await SeedProductAsync(factory, "W-1");
        await LinkAsync(factory, product.Id, supplier.Id);
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<ISupplierService>().SetActiveAsync(supplier.Id, false)).Succeeded));

        var html = await client.GetStringAsync($"/Suppliers/{supplier.Id}");
        Assert.Contains("W-1", html);
        Assert.Contains("Restore it to link more parts", html);
        Assert.DoesNotContain("Add a part", html);

        var response = await HtmxPostAsync(client, "/Suppliers/Links?handler=Save", AddFields("Supplier", other.Id, supplier.Id), $"/Suppliers/{supplier.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await LinksOfAsync(factory, other.Id));
    }
}
