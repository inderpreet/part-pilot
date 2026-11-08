using System.Net;
using System.Text;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-06-3: dashboard, nav badge, low-stock page with draft PO creation, report pages and their CSV downloads.</summary>
public sealed class ReportPagesTests
{
    private static HttpRequestMessage Htmx(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return request;
    }

    private static async Task<int> SeedSupplierAsync(AppFactory factory, string name)
    {
        var id = 0;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<ISupplierService>().CreateAsync(new SupplierInput(name, null, null, null, null, null, null));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            id = result.Value.Id;
        });
        return id;
    }

    /// <summary>A part with a reorder point, optionally a preferred supplier, and some stock.</summary>
    private static async Task<int> SeedPartAsync(
        AppFactory factory, string sku, int onHand = 0, int reorderPoint = 10, int? supplierId = null, long costCents = 500, string? name = null)
    {
        var id = 0;
        var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        await factory.WithScopeAsync(async sp =>
        {
            var created = await sp.GetRequiredService<IProductService>().CreateAsync(new ProductInput(sku, name ?? "Part " + sku, null, null, null, null, null, reorderPoint, 0, costCents));
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Message)));
            id = created.Value.Id;

            if (supplierId is { } supplier)
            {
                var linked = await sp.GetRequiredService<ISupplierService>().LinkProductAsync(id, supplier, new SupplierLinkInput("S-" + id, costCents, 7, 1, true));
                Assert.True(linked.Succeeded, string.Join("; ", linked.Errors.Select(e => e.Message)));
            }

            if (onHand > 0)
            {
                var posted = await sp.GetRequiredService<IStockService>().PostAsync([new StockMovement(id, StockTransactionType.Receive, onHand)], new StockContext(userId));
                Assert.True(posted.Succeeded, string.Join("; ", posted.Errors.Select(e => e.Message)));
            }
        });
        return id;
    }

    private static async Task<List<PurchaseOrderListItem>> DraftOrdersAsync(AppFactory factory)
    {
        List<PurchaseOrderListItem> drafts = [];
        await factory.WithScopeAsync(async sp =>
        {
            var page = await sp.GetRequiredService<IPurchaseOrderService>().SearchAsync(new PurchaseOrderListQuery { Tab = PurchaseOrderTab.Draft, PageSize = 100 });
            drafts = [.. page.Items];
        });
        return drafts;
    }

    // ---- dashboard -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_dashboard_shows_a_helpful_empty_state_to_everyone()
    {
        using var factory = new AppFactory();

        using var staff = factory.CreateClientAs("Staff");
        var staffHtml = await staff.GetStringAsync("/");
        Assert.Contains("Nothing here yet", staffHtml);
        Assert.Contains("href=\"/Products/Edit\"", staffHtml);

        using var viewer = factory.CreateClientAs("Viewer");
        var response = await viewer.GetAsync("/");
        var viewerHtml = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Nothing here yet", viewerHtml);
        Assert.DoesNotContain("/Products/Edit", viewerHtml);
    }

    [Fact]
    public async Task The_dashboard_shows_tiles_and_lists_and_the_viewer_sees_values_too()
    {
        using var factory = new AppFactory();
        var supplier = await SeedSupplierAsync(factory, "Acme");
        await SeedPartAsync(factory, "LOW-1", onHand: 2, reorderPoint: 10, supplierId: supplier, costCents: 1000);
        await SeedPartAsync(factory, "OK-1", onHand: 50, reorderPoint: 10, costCents: 200);

        using var viewer = factory.CreateClientAs("Viewer");
        var html = await viewer.GetStringAsync("/");

        Assert.Contains("Active parts", html);
        Assert.Contains("$120.00", html);                     // 2 x 10.00 + 50 x 2.00
        Assert.Contains("href=\"/Reports/LowStock\"", html);
        Assert.Contains("Running low", html);
        Assert.Contains("LOW-1", html);
        Assert.DoesNotContain(">OK-1<", html.Split("Latest stock movements")[0]);   // not low, so not in the low-stock list
        Assert.Contains("Latest stock movements", html);
        Assert.Contains("No orders due this week", html);
    }

    [Fact]
    public async Task The_dashboard_refresh_returns_only_the_fragment()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Htmx("/"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<html", html);
        Assert.Contains("Nothing here yet", html);
    }

    [Fact]
    public async Task The_nav_badge_counts_low_stock_parts_and_clears_when_stock_arrives()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        var part = await SeedPartAsync(factory, "LOW-1", onHand: 0, reorderPoint: 5);

        var low = await client.GetStringAsync("/Products");
        Assert.Contains("class=\"rail-badge\"", low);
        Assert.Contains("1 low-stock part", low);
        Assert.Matches("<a href=\"/Reports/LowStock\"[^>]*>", low);

        var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        await factory.WithScopeAsync(async sp =>
        {
            var posted = await sp.GetRequiredService<IStockService>().PostAsync([new StockMovement(part, StockTransactionType.Receive, 20)], new StockContext(userId));
            Assert.True(posted.Succeeded);
        });

        var fine = await client.GetStringAsync("/Products");
        Assert.DoesNotContain("rail-badge", fine);   // the save refreshed the cached count at once
    }

    // ---- report pages ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Reports")]
    [InlineData("/Reports/LowStock")]
    [InlineData("/Reports/Valuation")]
    [InlineData("/Reports/Movements")]
    [InlineData("/Reports/Usage")]
    [InlineData("/Reports/PurchaseOrders")]
    [InlineData("/Reports/DeadStock")]
    public async Task Every_report_page_renders_for_a_viewer_and_needs_a_sign_in(string url)
    {
        using var factory = new AppFactory();

        using var viewer = factory.CreateClientAs("Viewer");
        var response = await viewer.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1 class=\"page-title\">", await response.Content.ReadAsStringAsync());

        using var anonymous = factory.CreateAnonymousClient();
        var denied = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/Account/Login", denied.RedirectPath());
    }

    [Fact]
    public async Task The_reports_index_links_to_every_report()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Reports");

        foreach (var url in new[] { "LowStock", "Valuation", "Movements", "Usage", "PurchaseOrders", "DeadStock" })
        {
            Assert.Contains($"href=\"/Reports/{url}\"", html);
        }
    }

    [Fact]
    public async Task The_valuation_report_lists_parts_with_totals_and_filters_through_htmx()
    {
        using var factory = new AppFactory();
        await SeedPartAsync(factory, "A-1", onHand: 10, costCents: 250);
        await SeedPartAsync(factory, "B-1", onHand: 3, costCents: 1000);
        using var viewer = factory.CreateClientAs("Viewer");

        var full = await viewer.GetStringAsync("/Reports/Valuation");
        Assert.Contains("A-1", full);
        Assert.Contains("B-1", full);
        Assert.Contains("$55.00", full);   // 10 x 2.50 + 3 x 10.00
        Assert.Contains("Download CSV", full);

        var request = Htmx("/Reports/Valuation?search=B-1");
        request.Headers.Add("HX-Target", "results");
        var fragment = await (await viewer.SendAsync(request)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", fragment);
        Assert.Contains("B-1", fragment);
        Assert.DoesNotContain("A-1", fragment);
        Assert.Contains("$30.00", fragment);
        Assert.Contains("search=B-1&amp;handler=Csv", fragment);   // the download keeps the filter
    }

    [Fact]
    public async Task A_filter_with_no_matches_shows_an_empty_state_instead_of_an_empty_table()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Reports/Valuation?search=nothing-like-this");

        Assert.Contains("No parts match", html);
        Assert.DoesNotContain("<table", html);
    }

    [Fact]
    public async Task The_movements_report_filters_by_type_and_shows_totals_in_and_out()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "M-1", onHand: 10);
        var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        await factory.WithScopeAsync(async sp =>
        {
            Assert.True((await sp.GetRequiredService<IStockService>().PostAsync([new StockMovement(part, StockTransactionType.Issue, 4)], new StockContext(userId))).Succeeded);
        });
        using var client = factory.CreateClient();

        var all = await client.GetStringAsync("/Reports/Movements");
        Assert.Contains("+10 in", all);
        Assert.Contains("−4 out", all);

        var issues = await client.GetStringAsync("/Reports/Movements?type=Issue");
        Assert.Contains("−4 out", issues);
        Assert.DoesNotContain("+10 in", issues);
    }

    [Fact]
    public async Task The_usage_report_defaults_to_the_last_thirty_days_and_lists_issued_parts()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "U-1", onHand: 20);
        var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        await factory.WithScopeAsync(async sp =>
        {
            Assert.True((await sp.GetRequiredService<IStockService>().PostAsync([new StockMovement(part, StockTransactionType.Issue, 6)], new StockContext(userId))).Succeeded);
        });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Reports/Usage");

        Assert.Contains("U-1", html);
        Assert.Contains("name=\"from\" value=\"" + DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd"), html);
        Assert.Contains("6 units", html);
    }

    [Fact]
    public async Task The_purchase_order_report_and_dead_stock_report_show_their_rows()
    {
        using var factory = new AppFactory();
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var part = await SeedPartAsync(factory, "P-1", onHand: 5, supplierId: supplier, costCents: 100);
        var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        await factory.WithScopeAsync(async sp =>
        {
            var created = await sp.GetRequiredService<IPurchaseOrderService>().CreateDraftAsync(supplier, new PurchaseOrderActor(userId, true), [new PurchaseOrderLineInput(part, 10)]);
            Assert.True(created.Succeeded);
        });
        using var client = factory.CreateClient();

        var orders = await client.GetStringAsync("/Reports/PurchaseOrders");
        Assert.Contains("Acme", orders);
        Assert.Contains("$10.00", orders);

        var dead = await client.GetStringAsync("/Reports/DeadStock?days=1");
        Assert.Contains("No dead stock", dead);   // it just moved
    }

    // ---- low stock and draft POs ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_low_stock_page_lists_suggestions_and_a_viewer_cannot_order()
    {
        using var factory = new AppFactory();
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var part = await SeedPartAsync(factory, "LOW-1", onHand: 5, reorderPoint: 12, supplierId: supplier);
        await SeedPartAsync(factory, "NOSUP", onHand: 0, reorderPoint: 5);

        using var staff = factory.CreateClientAs("Staff");
        var html = await staff.GetStringAsync("/Reports/LowStock");
        Assert.Contains("LOW-1", html);
        Assert.Contains($"name=\"Quantities[{part}]\" value=\"7\"", html);   // max(0, 12 - 5)
        Assert.Contains("Create draft POs", html);
        Assert.Contains("Set one", html);                                     // the part without a preferred supplier

        using var viewer = factory.CreateClientAs("Viewer");
        var viewerHtml = await viewer.GetStringAsync("/Reports/LowStock");
        Assert.Contains("LOW-1", viewerHtml);
        Assert.DoesNotContain("Create draft POs", viewerHtml);
        Assert.DoesNotContain("name=\"Selected\"", viewerHtml);

        var denied = await WebForms.PostFormAsync(viewer, "/Reports/LowStock", [("Selected", part.ToString()), ($"Quantities[{part}]", "7")], tokenPage: "/Reports/LowStock");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(await DraftOrdersAsync(factory));
    }

    [Fact]
    public async Task Creating_orders_from_five_suggestions_across_two_suppliers_makes_two_drafts_and_reports_them()
    {
        using var factory = new AppFactory();
        var acme = await SeedSupplierAsync(factory, "Acme");
        var bolt = await SeedSupplierAsync(factory, "Bolt Co");
        var parts = new List<int>
        {
            await SeedPartAsync(factory, "A-1", supplierId: acme),
            await SeedPartAsync(factory, "A-2", supplierId: acme),
            await SeedPartAsync(factory, "A-3", supplierId: acme),
            await SeedPartAsync(factory, "B-1", supplierId: bolt),
            await SeedPartAsync(factory, "B-2", supplierId: bolt),
        };
        var noSupplier = await SeedPartAsync(factory, "NOSUP", reorderPoint: 5);
        using var client = factory.CreateClientAs("Staff");

        var fields = parts.SelectMany(id => new[] { ("Selected", id.ToString()), ($"Quantities[{id}]", "12") }).ToList();
        fields.Add(("Selected", noSupplier.ToString()));
        fields.Add(($"Quantities[{noSupplier}]", "3"));
        var response = await WebForms.PostFormAsync(client, "/Reports/LowStock", fields, tokenPage: "/Reports/LowStock");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Reports/LowStock", response.RedirectPath());

        var drafts = await DraftOrdersAsync(factory);
        Assert.Equal(2, drafts.Count);
        Assert.Equal(["Acme", "Bolt Co"], drafts.Select(d => d.SupplierName).Order());

        var summary = await client.GetStringAsync(response.RedirectPath());
        Assert.Contains("Created 2 draft purchase orders", summary);
        Assert.Contains("Not ordered", summary);
        Assert.Contains("NOSUP", summary);
        Assert.Contains("No preferred supplier", summary);
        foreach (var draft in drafts)
        {
            Assert.Contains($"href=\"/PurchaseOrders/{draft.Id}\"", summary);
        }

        // Shown once.
        Assert.DoesNotContain("Created 2 draft purchase orders", await client.GetStringAsync("/Reports/LowStock"));
    }

    [Fact]
    public async Task Posting_with_nothing_ticked_creates_nothing()
    {
        using var factory = new AppFactory();
        var supplier = await SeedSupplierAsync(factory, "Acme");
        await SeedPartAsync(factory, "A-1", supplierId: supplier);
        using var client = factory.CreateClientAs("Staff");

        var response = await WebForms.PostFormAsync(client, "/Reports/LowStock", [], tokenPage: "/Reports/LowStock");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(await DraftOrdersAsync(factory));
        Assert.Contains("Tick at least one part", await client.GetStringAsync(response.RedirectPath()));
    }

    [Fact]
    public async Task A_part_already_on_a_draft_is_not_ticked_by_default_and_points_at_the_draft()
    {
        using var factory = new AppFactory();
        var supplier = await SeedSupplierAsync(factory, "Acme");
        var part = await SeedPartAsync(factory, "A-1", supplierId: supplier);
        var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        int draftId = 0;
        await factory.WithScopeAsync(async sp =>
        {
            var created = await sp.GetRequiredService<IPurchaseOrderService>().CreateDraftAsync(supplier, new PurchaseOrderActor(userId, true), [new PurchaseOrderLineInput(part, 4)]);
            draftId = created.Value.Id;
        });
        using var client = factory.CreateClientAs("Staff");

        var html = await client.GetStringAsync("/Reports/LowStock");

        Assert.Contains("4 already on draft", html);
        Assert.Contains($"href=\"/PurchaseOrders/{draftId}\"", html);
        Assert.DoesNotContain("value=\"" + part + "\" checked", html);
    }

    [Fact]
    public async Task The_low_stock_filters_return_a_fragment_for_htmx()
    {
        using var factory = new AppFactory();
        var supplier = await SeedSupplierAsync(factory, "Acme");
        await SeedPartAsync(factory, "A-1", supplierId: supplier);
        await SeedPartAsync(factory, "B-1", supplierId: supplier);
        using var client = factory.CreateClient();

        var html = await (await client.SendAsync(Htmx("/Reports/LowStock?search=B-1"))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<html", html);
        Assert.Contains("B-1", html);
        Assert.DoesNotContain("A-1", html);
    }

    // ---- CSV -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Reports/Valuation?handler=Csv", "stock-valuation-", "SKU,Name,Category,Unit,On hand,Unit cost,Value,Active")]
    [InlineData("/Reports/Movements?handler=Csv", "stock-movements-", "Date (UTC),SKU,Name,Category,Type,Quantity change,On hand after,Unit cost,Reason,Reference,User")]
    [InlineData("/Reports/Usage?handler=Csv", "usage-", "SKU,Name,Category,Unit,Units used,Issues,Value at current cost")]
    [InlineData("/Reports/PurchaseOrders?handler=Csv", "purchase-orders-", "Number,Supplier,Status,Order date,Expected,Lines,Total,Outstanding")]
    [InlineData("/Reports/DeadStock?handler=Csv", "dead-stock-", "SKU,Name,Category,Unit,On hand,Unit cost,Value,Last movement (UTC)")]
    public async Task Each_csv_download_is_text_csv_with_a_filename_a_byte_order_mark_and_a_header_row(string url, string filenamePrefix, string header)
    {
        using var factory = new AppFactory();
        using var viewer = factory.CreateClientAs("Viewer");

        var response = await viewer.GetAsync(url);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.StartsWith(filenamePrefix, disposition.FileName!.Trim('"'));
        Assert.EndsWith(".csv", disposition.FileName.Trim('"'));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(header, Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split("\r\n")[0]);
    }

    [Fact]
    public async Task The_csv_follows_the_filters_and_holds_every_matching_row_not_just_the_page()
    {
        using var factory = new AppFactory();
        for (var i = 1; i <= 3; i++)
        {
            await SeedPartAsync(factory, $"KEEP-{i}", onHand: i, costCents: 100);
        }

        await SeedPartAsync(factory, "OTHER", onHand: 9, costCents: 100);
        using var client = factory.CreateClient();

        var csv = await client.GetStringAsync("/Reports/Valuation?handler=Csv&search=KEEP&pageSize=1&page=2");
        var lines = csv.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);   // header + the three matching parts
        Assert.All(lines.Skip(1), l => Assert.StartsWith("KEEP-", l));
        Assert.DoesNotContain("OTHER", csv);
    }

    [Fact]
    public async Task Csv_cells_that_look_like_formulas_are_neutralised()
    {
        using var factory = new AppFactory();
        await SeedPartAsync(factory, "F-1", onHand: 1, name: "=SUM(1+1)");
        using var client = factory.CreateClient();

        var csv = await client.GetStringAsync("/Reports/Valuation?handler=Csv");

        Assert.Contains("F-1,'=SUM(1+1),", csv);
    }

    [Fact]
    public async Task The_csv_endpoint_needs_a_sign_in()
    {
        using var factory = new AppFactory();
        using var anonymous = factory.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/Reports/Valuation?handler=Csv");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
