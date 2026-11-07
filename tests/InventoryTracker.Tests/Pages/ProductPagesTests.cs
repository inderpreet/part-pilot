using System.Net;
using System.Text.RegularExpressions;
using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-02-3: product list, detail, create/edit, archive and the lookup endpoint behind the picker.</summary>
public partial class ProductPagesTests
{
    [GeneratedRegex("name=\"Input.RowVersion\" value=\"(\\d+)\"")]
    private static partial Regex RowVersionPattern();

    private static HttpRequestMessage Htmx(string url, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return request;
    }

    private static (string, string)[] Fields(
        string sku, string name = "Widget", string cost = "", string? category = null, string? barcode = null, string rowVersion = "0",
        string? reorderPoint = null, string? reorderQuantity = null) =>
    [
        ("Input.Sku", sku), ("Input.Name", name), ("Input.Description", ""), ("Input.CategoryId", category ?? ""), ("Input.Unit", "each"),
        ("Input.Barcode", barcode ?? ""), ("Input.BinLocation", ""), ("Input.ReorderPoint", reorderPoint ?? ""),
        ("Input.ReorderQuantity", reorderQuantity ?? ""), ("Input.UnitCost", cost), ("Input.RowVersion", rowVersion),
    ];

    private static async Task<ProductDetail> SeedAsync(
        AppFactory factory, string sku, string name = "Widget", string? barcode = null, int? categoryId = null, long costCents = 0, int reorderPoint = 0)
    {
        ProductDetail? created = null;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<IProductService>()
                .CreateAsync(new ProductInput(sku, name, null, categoryId, null, barcode, null, reorderPoint, 0, costCents));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            created = result.Value;
        });
        return created!;
    }

    private static async Task<CategoryListItem> SeedCategoryAsync(AppFactory factory, string name)
    {
        CategoryListItem? created = null;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<ICategoryService>().CreateAsync(new CategoryInput(name, null));
            Assert.True(result.Succeeded);
            created = result.Value;
        });
        return created!;
    }

    private static async Task<ProductDetail> ReloadAsync(AppFactory factory, int id)
    {
        ProductDetail? product = null;
        await factory.WithScopeAsync(async sp => product = await sp.GetRequiredService<IProductService>().GetAsync(id));
        return product!;
    }

    // ---- list ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_renders_products_and_htmx_requests_get_only_the_results()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await SeedAsync(factory, "ABC-1", "Alpha widget", costCents: 1250);
        await SeedAsync(factory, "XYZ-9", "Zulu gadget");

        var full = await client.GetStringAsync("/Products");
        Assert.Contains("<html", full);
        Assert.Contains("ABC-1", full);
        Assert.Contains("XYZ-9", full);
        Assert.Contains("$12.50", full);
        Assert.Contains("id=\"results\"", full);

        var fragment = await (await client.SendAsync(Htmx("/Products?search=zulu"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", fragment);
        Assert.Contains("<table", fragment);
        Assert.Contains("XYZ-9", fragment);
        Assert.DoesNotContain("ABC-1", fragment);
    }

    [Fact]
    public async Task The_list_filters_by_category_and_status_and_sorts()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var tools = await SeedCategoryAsync(factory, "Tools");
        await SeedAsync(factory, "T-1", "Hammer", categoryId: tools.Id);
        await SeedAsync(factory, "O-1", "Other");
        var old = await SeedAsync(factory, "OLD-1", "Retired thing");
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IProductService>().SetActiveAsync(old.Id, false)).Succeeded));

        var byCategory = await (await client.SendAsync(Htmx($"/Products?categoryId={tools.Id}"))).Content.ReadAsStringAsync();
        Assert.Contains("T-1", byCategory);
        Assert.DoesNotContain("O-1", byCategory);

        var active = await (await client.SendAsync(Htmx("/Products"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("OLD-1", active);

        var archived = await (await client.SendAsync(Htmx("/Products?status=Archived"))).Content.ReadAsStringAsync();
        Assert.Contains("OLD-1", archived);
        Assert.Contains("Archived", archived);
        Assert.DoesNotContain("T-1", archived);

        // Descending by SKU: T-1, OLD-1, O-1.
        var sorted = await (await client.SendAsync(Htmx("/Products?status=All&sort=sku&desc=true"))).Content.ReadAsStringAsync();
        var positions = new[] { "T-1", "OLD-1", "O-1" }.Select(sku => sorted.IndexOf($">{sku}</a>", StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public async Task Low_stock_products_are_flagged()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await SeedAsync(factory, "LOW-1", reorderPoint: 5); // on hand 0 <= 5

        Assert.Contains("badge-low", await client.GetStringAsync("/Products"));
    }

    [Fact]
    public async Task An_empty_catalogue_invites_staff_to_add_a_product_but_not_viewers()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        using var viewer = factory.CreateClientAs("Viewer");

        Assert.Contains("No products yet", await admin.GetStringAsync("/Products"));
        Assert.Contains("href=\"/Products/Edit\"", await admin.GetStringAsync("/Products"));
        Assert.DoesNotContain("href=\"/Products/Edit\"", await viewer.GetStringAsync("/Products"));
    }

    [Fact]
    public async Task Pressing_enter_on_an_exact_sku_or_barcode_opens_the_product()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var product = await SeedAsync(factory, "SCAN-1", "Scanned thing", barcode: "5012345678900");

        foreach (var code in new[] { "scan-1", "5012345678900" })
        {
            var response = await client.GetAsync($"/Products?search={code}&go=true");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal($"/Products/{product.Id}", response.RedirectPath());
        }
    }

    [Fact]
    public async Task Pressing_enter_on_an_unknown_code_stays_on_the_list_with_a_notice()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await SeedAsync(factory, "SCAN-1", "Scanned thing");

        var response = await client.GetAsync("/Products?search=SCAN&go=true"); // partial, not exact

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No product has the exact SKU or barcode", html);
        Assert.Contains("SCAN-1", html); // still shows partial matches
    }

    [Fact]
    public async Task Viewers_can_browse_but_see_no_edit_actions()
    {
        using var factory = new AppFactory();
        var product = await SeedAsync(factory, "V-1");
        using var viewer = factory.CreateClientAs("Viewer");
        using var staff = factory.CreateClientAs("Staff");

        var viewerList = await viewer.GetStringAsync("/Products");
        Assert.Contains("V-1", viewerList);
        Assert.DoesNotContain("New product", viewerList);
        Assert.DoesNotContain($"/Products/Edit/{product.Id}", viewerList);

        var viewerDetail = await viewer.GetStringAsync($"/Products/{product.Id}");
        Assert.DoesNotContain("Archive", viewerDetail);
        Assert.DoesNotContain("/Products/Edit", viewerDetail);

        var staffList = await staff.GetStringAsync("/Products");
        Assert.Contains("New product", staffList);
        Assert.Contains($"/Products/Edit/{product.Id}", staffList);
    }

    // ---- detail ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_detail_page_shows_the_product_with_stock_value_and_unknown_ids_are_404()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var category = await SeedCategoryAsync(factory, "Fasteners");
        var product = await SeedAsync(factory, "DET-1", "Hex bolt", barcode: "123456", categoryId: category.Id, costCents: 250);
        // Stands in for F03 until the ledger exists: sets the cached quantity directly.
        await factory.WithScopeAsync(async sp => await sp.GetRequiredService<AppDbContext>().Products
            .Where(p => p.Id == product.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.QuantityOnHand, 4)));

        var html = await client.GetStringAsync($"/Products/{product.Id}");

        Assert.Contains("DET-1", html);
        Assert.Contains("Hex bolt", html);
        Assert.Contains("Fasteners", html);
        Assert.Contains("123456", html);
        Assert.Contains("$2.50", html);  // unit cost
        Assert.Contains("$10.00", html); // stock value: 4 x 2.50

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Products/99999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Products/abc")).StatusCode);
    }

    // ---- create / edit ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_product_shows_it_on_the_detail_page_with_a_flash_and_in_the_list()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");

        var response = await WebForms.PostFormAsync(client, "/Products/Edit",
            Fields("NEW-1", "New thing", cost: "12.50", reorderPoint: "5", reorderQuantity: "20"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var detail = await client.GetStringAsync(response.RedirectPath());
        Assert.Contains("NEW-1", detail);
        Assert.Contains("$12.50", detail);
        Assert.Contains("created", detail); // flash from TempData

        Assert.Contains("NEW-1", await client.GetStringAsync("/Products"));
    }

    [Theory]
    [InlineData("12.50", 1250)]
    [InlineData("0.5", 50)]
    [InlineData("1,234.50", 123450)]
    [InlineData("", 0)]
    public async Task The_unit_cost_is_parsed_into_cents(string typed, long expectedCents)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var response = await WebForms.PostFormAsync(client, "/Products/Edit", Fields("COST-1", cost: typed));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var id = int.Parse(response.RedirectPath().Split('/').Last());
        Assert.Equal(expectedCents, (await ReloadAsync(factory, id)).UnitCostCents);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.234")]
    [InlineData("1,5")]
    public async Task An_unusable_unit_cost_is_rejected_with_a_message(string typed)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var response = await WebForms.PostFormAsync(client, "/Products/Edit", Fields("COST-2", cost: typed));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter an amount like 12.50", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_negative_unit_cost_is_rejected_by_the_service_rule()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var response = await WebForms.PostFormAsync(client, "/Products/Edit", Fields("COST-3", cost: "-5"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Unit cost cannot be negative", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Required_fields_and_duplicate_skus_and_barcodes_are_reported_on_the_form()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await SeedAsync(factory, "DUP-1", barcode: "999");

        var missing = await WebForms.PostFormAsync(client, "/Products/Edit", Fields("", name: ""));
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        var missingHtml = await missing.Content.ReadAsStringAsync();
        Assert.Contains("SKU is required", missingHtml);
        Assert.Contains("Name is required", missingHtml);

        var dupSku = await WebForms.PostFormAsync(client, "/Products/Edit", Fields("dup-1"));
        Assert.Equal(HttpStatusCode.OK, dupSku.StatusCode);
        Assert.Contains("already", await dupSku.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var dupBarcode = await WebForms.PostFormAsync(client, "/Products/Edit", Fields("OTHER-1", barcode: "999"));
        Assert.Equal(HttpStatusCode.OK, dupBarcode.StatusCode);
        Assert.Contains("already", await dupBarcode.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // The form keeps what was typed.
        Assert.Contains("value=\"OTHER-1\"", await dupBarcode.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Editing_a_product_saves_the_changes()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var product = await SeedAsync(factory, "ED-1", "Before");
        var url = $"/Products/Edit/{product.Id}";

        var form = await client.GetStringAsync(url);
        Assert.Contains("value=\"Before\"", form);
        var version = RowVersionPattern().Match(form).Groups[1].Value;

        var response = await WebForms.PostFormAsync(client, url, Fields("ED-1", "After", cost: "3.00", rowVersion: version));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Products/{product.Id}", response.RedirectPath());
        var saved = await ReloadAsync(factory, product.Id);
        Assert.Equal("After", saved.Name);
        Assert.Equal(300, saved.UnitCostCents);
    }

    [Fact]
    public async Task Saving_a_stale_form_reports_the_conflict_and_offers_a_reload()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var product = await SeedAsync(factory, "CON-1", "Original");
        var url = $"/Products/Edit/{product.Id}";

        var form = await client.GetStringAsync(url);
        var staleVersion = RowVersionPattern().Match(form).Groups[1].Value;

        // Someone else saves first.
        await factory.WithScopeAsync(async sp =>
        {
            var current = (await sp.GetRequiredService<IProductService>().GetAsync(product.Id))!;
            var result = await sp.GetRequiredService<IProductService>().UpdateAsync(product.Id, current.RowVersion,
                new ProductInput("CON-1", "Changed by someone else", null, null, null, null, null, 0, 0, 0));
            Assert.True(result.Succeeded);
        });

        var response = await WebForms.PostFormAsync(client, url, Fields("CON-1", "My edit", rowVersion: staleVersion), tokenPage: url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Someone else saved this product", html);
        Assert.Contains($"href=\"/Products/Edit/{product.Id}\"", html);
        Assert.Equal("Changed by someone else", (await ReloadAsync(factory, product.Id)).Name);
    }

    [Fact]
    public async Task Edit_pages_are_closed_to_viewers_and_unknown_products_are_404()
    {
        using var factory = new AppFactory();
        var product = await SeedAsync(factory, "RO-1");
        using var viewer = factory.CreateClientAs("Viewer");
        using var admin = factory.CreateClientAs("Admin");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Products/Edit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/Products/Edit/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/Products/Edit", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/Products/Edit/99999")).StatusCode);
    }

    // ---- archive ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Archiving_hides_a_product_from_the_default_list_and_restoring_brings_it_back()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var product = await SeedAsync(factory, "ARC-1");
        var url = $"/Products/{product.Id}?handler=Archive";

        var archive = await WebForms.PostFormAsync(client, url,
            [("active", "false"), ("rowVersion", product.RowVersion.ToString())], tokenPage: $"/Products/{product.Id}");
        Assert.Equal(HttpStatusCode.Redirect, archive.StatusCode);
        Assert.Equal($"/Products/{product.Id}", archive.RedirectPath());

        var detail = await client.GetStringAsync($"/Products/{product.Id}");
        Assert.Contains("archived", detail);
        Assert.Contains("Restore", detail);
        Assert.DoesNotContain("ARC-1", await client.GetStringAsync("/Products"));
        Assert.Contains("ARC-1", await client.GetStringAsync("/Products?status=Archived"));

        var current = await ReloadAsync(factory, product.Id);
        var restore = await WebForms.PostFormAsync(client, url,
            [("active", "true"), ("rowVersion", current.RowVersion.ToString())], tokenPage: $"/Products/{product.Id}");
        Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
        Assert.Contains("ARC-1", await client.GetStringAsync("/Products"));
    }

    [Fact]
    public async Task Archiving_an_htmx_request_redirects_client_side()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var product = await SeedAsync(factory, "ARC-2");
        var token = await WebForms.TokenAsync(client, $"/Products/{product.Id}");

        var request = Htmx($"/Products/{product.Id}?handler=Archive", HttpMethod.Post);
        request.Headers.Add("RequestVerificationToken", token);
        request.Content = new FormUrlEncodedContent([new("active", "false"), new("rowVersion", product.RowVersion.ToString())]);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal($"/Products/{product.Id}", response.Headers.GetValues("HX-Redirect").Single());
        Assert.False((await ReloadAsync(factory, product.Id)).IsActive);
    }

    [Fact]
    public async Task A_stale_archive_is_refused_and_leaves_the_product_active()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var product = await SeedAsync(factory, "ARC-3");

        var response = await WebForms.PostFormAsync(client, $"/Products/{product.Id}?handler=Archive",
            [("active", "false"), ("rowVersion", (product.RowVersion + 100).ToString())], tokenPage: $"/Products/{product.Id}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True((await ReloadAsync(factory, product.Id)).IsActive);
        Assert.Contains("modified", await client.GetStringAsync($"/Products/{product.Id}"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Viewers_cannot_archive()
    {
        using var factory = new AppFactory();
        var product = await SeedAsync(factory, "ARC-4");
        using var viewer = factory.CreateClientAs("Viewer");
        var token = await WebForms.TokenAsync(viewer, $"/Products/{product.Id}");

        var request = Htmx($"/Products/{product.Id}?handler=Archive", HttpMethod.Post);
        request.Headers.Add("RequestVerificationToken", token);
        request.Content = new FormUrlEncodedContent([new("active", "false"), new("rowVersion", product.RowVersion.ToString())]);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(request)).StatusCode);
        Assert.True((await ReloadAsync(factory, product.Id)).IsActive);
    }

    // ---- lookup (product picker) -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_lookup_endpoint_returns_matching_rows_for_any_signed_in_user()
    {
        using var factory = new AppFactory();
        await SeedAsync(factory, "PICK-1", "Blue pen", barcode: "4006381333931");
        await SeedAsync(factory, "PICK-2", "Red pen");
        var retired = await SeedAsync(factory, "PICK-3", "Old pen");
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IProductService>().SetActiveAsync(retired.Id, false)).Succeeded));
        using var viewer = factory.CreateClientAs("Viewer");

        var html = await viewer.GetStringAsync("/Products/Lookup?q=pen");
        Assert.DoesNotContain("<html", html);
        Assert.Contains("PICK-1", html);
        Assert.Contains("PICK-2", html);
        Assert.DoesNotContain("PICK-3", html); // archived products are hidden unless asked for

        Assert.Contains("PICK-3", await viewer.GetStringAsync("/Products/Lookup?q=pen&activeOnly=false"));

        var exact = await viewer.GetStringAsync("/Products/Lookup?q=4006381333931");
        Assert.Contains("data-exact=\"true\"", exact);
        Assert.Contains("data-picker-select", exact);

        Assert.Contains("No matching products", await viewer.GetStringAsync("/Products/Lookup?q=zzzz"));
    }

    [Fact]
    public async Task The_picker_component_renders_with_its_wiring()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var html = await client.GetStringAsync("/Dev/Components");

        Assert.Contains("data-product-picker", html);
        Assert.Contains("name=\"Input.ProductId\"", html);
        Assert.Contains("hx-get=\"/Products/Lookup\"", html);
        Assert.Contains("lookup-now", html);
    }

    [Fact]
    public async Task Pages_require_a_signed_in_user()
    {
        using var factory = new AppFactory { DefaultTestRole = null };
        using var client = factory.CreateAnonymousClient();

        foreach (var url in new[] { "/Products", "/Products/1", "/Products/Edit", "/Products/Lookup", "/Categories" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/Login", response.RedirectPath());
        }
    }
}
