using System.Net;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-03-3: the quick-move dialog, bulk movements, stock counts, the movements list, product history and the verify tool.</summary>
public class StockPagesTests
{
    // ---- helpers -------------------------------------------------------------------------------------------------

    private static async Task<int> ProductAsync(AppFactory factory, string sku, int onHand = 0, bool archived = false)
    {
        var id = 0;
        await factory.WithScopeAsync(async sp =>
        {
            var products = sp.GetRequiredService<IProductService>();
            var created = await products.CreateAsync(new ProductInput(sku, "Product " + sku, null, null, null, null, null, 0, 0, 0));
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Message)));
            id = created.Value.Id;
        });

        if (onHand != 0)
        {
            await PostAsync(factory, new StockMovement(id, StockTransactionType.Receive, onHand));
        }

        if (archived)
        {
            await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IProductService>().SetActiveAsync(id, false)).Succeeded));
        }

        return id;
    }

    private static async Task PostAsync(AppFactory factory, params StockMovement[] lines)
    {
        var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<IStockService>().PostAsync(lines, new StockContext(userId));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        });
    }

    private static async Task<int> OnHandAsync(AppFactory factory, int productId)
    {
        var onHand = 0;
        await factory.WithScopeAsync(async sp => onHand = (await sp.GetRequiredService<IProductService>().GetAsync(productId))!.QuantityOnHand);
        return onHand;
    }

    private static async Task<int> LedgerCountAsync(AppFactory factory)
    {
        var count = 0;
        await factory.WithScopeAsync(async sp => count = await sp.GetRequiredService<AppDbContext>().StockTransactions.CountAsync());
        return count;
    }

    private static async Task<long> FirstTransactionIdAsync(AppFactory factory)
    {
        long id = 0;
        await factory.WithScopeAsync(async sp => id = await sp.GetRequiredService<AppDbContext>().StockTransactions.OrderBy(t => t.Id).Select(t => t.Id).FirstAsync());
        return id;
    }

    private static async Task<HttpResponseMessage> SendFormAsync(
        HttpClient client, string url, IEnumerable<(string Key, string Value)> fields, string tokenPage, bool htmx = false)
    {
        var token = await WebForms.TokenAsync(client, tokenPage);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Prepend((Key: "__RequestVerificationToken", Value: token)).Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };
        if (htmx)
        {
            request.Headers.Add("HX-Request", "true");
        }

        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Htmx(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return request;
    }

    private static (string, string)[] MoveFields(int productId, string type, string quantity, string reason = "", string reference = "") =>
    [
        ("Input.ProductId", productId.ToString()), ("Input.Type", type), ("Input.Quantity", quantity), ("Input.Reason", reason),
        ("Input.Reference", reference), ("Input.OccurredAt", ""), ("Input.Tz", ""), ("Input.Note", ""),
    ];

    // ---- quick move ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_move_dialog_loads_as_a_fragment_for_htmx_and_as_a_full_page_otherwise()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var id = await ProductAsync(factory, "MV-1", onHand: 4);

        var fragment = await (await client.SendAsync(Htmx($"/Stock/Move?productId={id}&type=Issue"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", fragment);
        Assert.Matches("value=\"Issue\"\\s+checked", fragment);
        Assert.Contains("MV-1", fragment);
        Assert.Contains("On hand: 4", fragment);
        Assert.Contains("hx-post=\"/Stock/Move\"", fragment);

        var full = await client.GetStringAsync($"/Stock/Move?productId={id}");
        Assert.Contains("<html", full);
        Assert.DoesNotContain("hx-post=\"/Stock/Move\"", full);
    }

    [Fact]
    public async Task Receiving_in_the_dialog_posts_a_transaction_and_asks_the_page_to_refresh_with_a_message()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var id = await ProductAsync(factory, "MV-2");

        var response = await SendFormAsync(client, "/Stock/Move", MoveFields(id, "Receive", "7", reference: "DN-1"), $"/Stock/Move?productId={id}", htmx: true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("HX-Refresh").Single());
        Assert.Equal(7, await OnHandAsync(factory, id));

        // The refresh shows the flash from TempData.
        Assert.Contains("Received 7", await client.GetStringAsync($"/Products/{id}"));
    }

    [Fact]
    public async Task Issuing_more_than_is_on_hand_comes_back_as_an_inline_422_and_posts_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var id = await ProductAsync(factory, "MV-3", onHand: 3);
        var before = await LedgerCountAsync(factory);

        var response = await SendFormAsync(client, "/Stock/Move", MoveFields(id, "Issue", "5"), $"/Stock/Move?productId={id}", htmx: true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("only 3 on hand", html);
        Assert.Contains("name=\"Input.Quantity\" value=\"5\"", html); // what was typed is kept
        Assert.DoesNotContain("<html", html);
        Assert.Equal(before, await LedgerCountAsync(factory));
        Assert.Equal(3, await OnHandAsync(factory, id));
    }

    [Fact]
    public async Task The_full_page_move_form_redirects_to_the_product_and_shows_errors_in_place()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var id = await ProductAsync(factory, "MV-4", onHand: 1);

        var ok = await SendFormAsync(client, "/Stock/Move", MoveFields(id, "Adjust", "-1", reason: "Damaged"), $"/Stock/Move?productId={id}");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal($"/Products/{id}", ok.RedirectPath());
        Assert.Equal(0, await OnHandAsync(factory, id));

        var bad = await SendFormAsync(client, "/Stock/Move", MoveFields(id, "Adjust", "-1"), $"/Stock/Move?productId={id}");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("A reason is required", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_move_form_rejects_types_it_does_not_offer()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var id = await ProductAsync(factory, "MV-5", onHand: 10);

        var response = await SendFormAsync(client, "/Stock/Move", MoveFields(id, "Count", "0"), $"/Stock/Move?productId={id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Choose a movement type", await response.Content.ReadAsStringAsync());
        Assert.Equal(10, await OnHandAsync(factory, id));
    }

    [Fact]
    public async Task Move_buttons_show_for_active_products_to_editors_only()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        using var viewer = factory.CreateClientAs("Viewer");
        var active = await ProductAsync(factory, "BTN-1");
        var archived = await ProductAsync(factory, "BTN-2", archived: true);

        var list = await (await admin.SendAsync(Htmx("/Products?status=All"))).Content.ReadAsStringAsync();
        Assert.Contains($"/Stock/Move?productId={active}&amp;type=Receive", list);
        Assert.DoesNotContain($"/Stock/Move?productId={archived}", list);

        Assert.Contains($"/Stock/Move?productId={active}", await admin.GetStringAsync($"/Products/{active}"));
        Assert.DoesNotContain($"/Stock/Move?productId={archived}", await admin.GetStringAsync($"/Products/{archived}"));

        Assert.DoesNotContain("/Stock/Move?", await viewer.GetStringAsync("/Products"));
        Assert.DoesNotContain("/Stock/Move?", await viewer.GetStringAsync($"/Products/{active}"));
    }

    // ---- roles ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Stock/Move")]
    [InlineData("/Stock/Bulk")]
    [InlineData("/Stock/Count")]
    public async Task Viewers_cannot_open_or_post_the_editing_pages(string url)
    {
        using var factory = new AppFactory();
        using var viewer = factory.CreateClientAs("Viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(url, new FormUrlEncodedContent([]))).StatusCode);
    }

    [Fact]
    public async Task Viewers_cannot_reverse_a_movement()
    {
        using var factory = new AppFactory();
        using var viewer = factory.CreateClientAs("Viewer");
        var id = await ProductAsync(factory, "RV-0", onHand: 2);

        var response = await viewer.PostAsync($"/Stock/Reverse/{await FirstTransactionIdAsync(factory)}", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(2, await OnHandAsync(factory, id));
    }

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_the_login_page()
    {
        using var factory = new AppFactory();
        using var anonymous = factory.CreateAnonymousClient();

        foreach (var url in new[] { "/Stock", "/Stock/Movements", "/Stock/Move", "/Admin/VerifyStock" })
        {
            var response = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/Login", response.RedirectPath());
        }
    }

    // ---- bulk ----------------------------------------------------------------------------------------------------

    private static (string, string)[] BulkFields(string type, params (int ProductId, string Quantity)[] lines)
    {
        var fields = new List<(string, string)>
        {
            ("Input.Type", type), ("Input.Reason", ""), ("Input.Reference", "DN-9"), ("Input.OccurredAt", ""), ("Input.Tz", ""), ("Input.Note", ""),
        };
        for (var i = 0; i < lines.Length; i++)
        {
            fields.Add(($"Input.Lines[{i}].ProductId", lines[i].ProductId.ToString()));
            fields.Add(($"Input.Lines[{i}].Quantity", lines[i].Quantity));
        }

        return [.. fields];
    }

    [Fact]
    public async Task A_bulk_receipt_posts_every_line_and_ignores_blank_ones()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var a = await ProductAsync(factory, "BK-1");
        var b = await ProductAsync(factory, "BK-2");

        var fields = BulkFields("Receive", (a, "5"), (b, "8")).Concat([("Input.Lines[2].ProductId", ""), ("Input.Lines[2].Quantity", "")]);
        var response = await SendFormAsync(client, "/Stock/Bulk", fields, "/Stock/Bulk");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Stock/Movements", response.RedirectPath());
        Assert.Equal(5, await OnHandAsync(factory, a));
        Assert.Equal(8, await OnHandAsync(factory, b));
        Assert.Equal(2, await LedgerCountAsync(factory));
        Assert.Contains("Recorded 2 receive lines", await client.GetStringAsync("/Stock/Movements"));
    }

    [Fact]
    public async Task One_bad_bulk_line_posts_nothing_and_the_error_names_the_line()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var a = await ProductAsync(factory, "BK-3", onHand: 10);
        var b = await ProductAsync(factory, "BK-4", onHand: 1);
        var before = await LedgerCountAsync(factory);

        var response = await SendFormAsync(client, "/Stock/Bulk", BulkFields("Issue", (a, "2"), (b, "5")), "/Stock/Bulk");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Line 2:", html);
        Assert.Contains("only 1 on hand", html);
        Assert.DoesNotContain("Line 1:", html);
        Assert.Equal(10, await OnHandAsync(factory, a)); // all or nothing
        Assert.Equal(before, await LedgerCountAsync(factory));
    }

    [Fact]
    public async Task A_half_filled_bulk_line_is_an_error()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var a = await ProductAsync(factory, "BK-5");

        var response = await SendFormAsync(client, "/Stock/Bulk", BulkFields("Receive", (a, "")).Concat([("Input.Lines[1].ProductId", ""), ("Input.Lines[1].Quantity", "3")]), "/Stock/Bulk");

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Line 1: enter a quantity.", html);
        Assert.Contains("Line 2: choose a part.", html);
        Assert.Equal(0, await LedgerCountAsync(factory));
    }

    [Fact]
    public async Task Bulk_lines_can_be_added_and_removed_keeping_what_was_typed()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var a = await ProductAsync(factory, "BK-6");
        var b = await ProductAsync(factory, "BK-7");

        var added = await SendFormAsync(client, "/Stock/Bulk?handler=AddLine", BulkFields("Receive", (a, "4"), (b, "6")), "/Stock/Bulk", htmx: true);
        var addedHtml = await added.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.DoesNotContain("<html", addedHtml);
        Assert.Equal(3, addedHtml.Split("data-line-row").Length - 1);
        Assert.Contains("BK-6", addedHtml); // the picker keeps showing the chosen product

        var removed = await SendFormAsync(client, "/Stock/Bulk?handler=RemoveLine&at=0", BulkFields("Receive", (a, "4"), (b, "6")), "/Stock/Bulk", htmx: true);
        var removedHtml = await removed.Content.ReadAsStringAsync();
        Assert.Equal(1, removedHtml.Split("data-line-row").Length - 1);
        Assert.Contains("name=\"Input.Lines[0].Quantity\" value=\"6\"", removedHtml);
        Assert.Contains("BK-7", removedHtml);
    }

    // ---- count ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Counting_fifty_products_with_three_differences_posts_exactly_three_transactions()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var ids = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            ids.Add(await ProductAsync(factory, $"CNT-{i:00}", onHand: 10));
        }

        var before = await LedgerCountAsync(factory);
        var counted = ids.Select((id, i) => ($"Counted[{id}]", i switch { 3 => "8", 17 => "12", 40 => "0", _ => "10" })).ToList();
        counted.Add(("note", "Year end"));

        var preview = await SendFormAsync(client, "/Stock/Count?handler=Preview", counted, "/Stock/Count");
        var previewHtml = await preview.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("<strong>50</strong> parts counted", previewHtml);
        Assert.Contains("<strong>3</strong> differ", previewHtml);
        Assert.Equal(before, await LedgerCountAsync(factory)); // a preview writes nothing

        var post = await SendFormAsync(client, "/Stock/Count?handler=Post", counted, "/Stock/Count");
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.StartsWith("/Stock/Movements?type=Count", post.RedirectPath());
        Assert.Equal(before + 3, await LedgerCountAsync(factory));
        Assert.Equal(8, await OnHandAsync(factory, ids[3]));
        Assert.Equal(12, await OnHandAsync(factory, ids[17]));
        Assert.Equal(0, await OnHandAsync(factory, ids[40]));
        Assert.Equal(10, await OnHandAsync(factory, ids[0]));
        Assert.Contains("Count posted: 3 parts adjusted, 47 already matched.", await client.GetStringAsync("/Stock/Movements?type=Count"));
    }

    [Fact]
    public async Task A_blank_count_box_means_not_counted_and_is_different_from_zero()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var a = await ProductAsync(factory, "CB-1", onHand: 5);
        var b = await ProductAsync(factory, "CB-2", onHand: 5);

        var response = await SendFormAsync(client, "/Stock/Count?handler=Post", [($"Counted[{a}]", ""), ($"Counted[{b}]", "0")], "/Stock/Count");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(5, await OnHandAsync(factory, a));
        Assert.Equal(0, await OnHandAsync(factory, b));
    }

    [Fact]
    public async Task Previewing_an_empty_sheet_asks_for_at_least_one_count()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var a = await ProductAsync(factory, "CE-1", onHand: 5);

        var response = await SendFormAsync(client, "/Stock/Count?handler=Preview", [($"Counted[{a}]", "")], "/Stock/Count");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter at least one counted quantity", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_blind_count_sheet_hides_the_system_quantity()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        await ProductAsync(factory, "BL-1", onHand: 123);

        var open = await client.GetStringAsync("/Stock/Count");
        var blind = await client.GetStringAsync("/Stock/Count?blind=true");

        Assert.Contains(">123 <", open);
        Assert.DoesNotContain(">123 <", blind);
        Assert.Contains("BL-1", blind);
    }

    [Fact]
    public async Task The_count_sheet_filters_by_bin_and_search()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        await factory.WithScopeAsync(async sp =>
        {
            var products = sp.GetRequiredService<IProductService>();
            Assert.True((await products.CreateAsync(new ProductInput("BIN-A", "Alpha", null, null, null, null, "R1-A", 0, 0, 0))).Succeeded);
            Assert.True((await products.CreateAsync(new ProductInput("BIN-B", "Bravo", null, null, null, null, "R2-B", 0, 0, 0))).Succeeded);
        });

        var byBin = await client.GetStringAsync("/Stock/Count?bin=R1");
        Assert.Contains("BIN-A", byBin);
        Assert.DoesNotContain("BIN-B", byBin);

        var bySearch = await client.GetStringAsync("/Stock/Count?search=bravo");
        Assert.Contains("BIN-B", bySearch);
        Assert.DoesNotContain("BIN-A", bySearch);
    }

    // ---- reverse & movements -------------------------------------------------------------------------------------

    [Fact]
    public async Task Reversing_a_movement_marks_it_reversed_and_a_second_reversal_is_refused()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var id = await ProductAsync(factory, "RV-1", onHand: 6);
        var original = await FirstTransactionIdAsync(factory);
        var token = await WebForms.TokenAsync(client, "/Stock/Movements");

        async Task<HttpResponseMessage> ReverseAsync(bool htmx)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/Stock/Reverse/{original}")
            {
                Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("returnUrl", "/Stock/Movements?productId=" + id)]),
            };
            if (htmx)
            {
                request.Headers.Add("HX-Request", "true");
            }

            return await client.SendAsync(request);
        }

        var first = await ReverseAsync(htmx: false);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal($"/Stock/Movements?productId={id}", first.RedirectPath());
        Assert.Equal(0, await OnHandAsync(factory, id));

        var list = await client.GetStringAsync($"/Stock/Movements?productId={id}");
        Assert.Contains("Reversed", list);
        Assert.Contains($"reverses #{original}", list);
        Assert.Contains("row-reversed", list);

        var second = await ReverseAsync(htmx: true);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode); // htmx: refresh, the error rides TempData
        Assert.Equal("true", second.Headers.GetValues("HX-Refresh").Single());
        Assert.Equal(2, await LedgerCountAsync(factory));
        Assert.Equal(0, await OnHandAsync(factory, id));
        Assert.Contains("flash-error", await client.GetStringAsync("/Stock/Movements"));
    }

    [Fact]
    public async Task A_reversal_cannot_be_reversed_and_has_no_button()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        await ProductAsync(factory, "RV-2", onHand: 3);
        var original = await FirstTransactionIdAsync(factory);
        var token = await WebForms.TokenAsync(client, "/Stock/Movements");
        await client.PostAsync($"/Stock/Reverse/{original}", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        var list = await client.GetStringAsync("/Stock/Movements");

        Assert.DoesNotContain($"/Stock/Reverse/{original}", list);
        Assert.DoesNotContain($"/Stock/Reverse/{original + 1}", list);
    }

    [Fact]
    public async Task Reversing_an_unknown_transaction_is_a_404_and_a_get_just_redirects()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");

        var missing = await SendFormAsync(client, "/Stock/Reverse/9999", [], "/Stock/Movements");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var get = await client.GetAsync("/Stock/Reverse/1");
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
    }

    [Fact]
    public async Task The_movements_list_filters_and_shows_the_reverse_button_to_editors_only()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        using var viewer = factory.CreateClientAs("Viewer");
        var a = await ProductAsync(factory, "MVL-1", onHand: 5);
        await ProductAsync(factory, "MVL-2", onHand: 9);
        await PostAsync(factory, new StockMovement(a, StockTransactionType.Issue, 2));

        var all = await staff.GetStringAsync("/Stock/Movements");
        Assert.Contains("MVL-1", all);
        Assert.Contains("MVL-2", all);
        Assert.Contains("/Stock/Reverse/", all);

        var byProduct = await (await staff.SendAsync(Htmx($"/Stock/Movements?productId={a}"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", byProduct);
        Assert.Contains("MVL-1", byProduct);
        Assert.DoesNotContain("MVL-2", byProduct);

        var byType = await (await staff.SendAsync(Htmx("/Stock/Movements?type=Issue"))).Content.ReadAsStringAsync();
        Assert.Contains("MVL-1", byType);
        Assert.DoesNotContain("MVL-2", byType);

        var bySearch = await (await staff.SendAsync(Htmx("/Stock/Movements?search=mvl-2"))).Content.ReadAsStringAsync();
        Assert.Contains("MVL-2", bySearch);
        Assert.DoesNotContain("MVL-1", bySearch);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var future = await (await staff.SendAsync(Htmx($"/Stock/Movements?from={today.AddDays(3):yyyy-MM-dd}&tz=0"))).Content.ReadAsStringAsync();
        Assert.Contains("No movements", future);

        var current = await (await staff.SendAsync(Htmx($"/Stock/Movements?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}&tz=0"))).Content.ReadAsStringAsync();
        Assert.Contains("MVL-2", current);

        var viewerList = await viewer.GetStringAsync("/Stock/Movements");
        Assert.Contains("MVL-1", viewerList);
        Assert.DoesNotContain("/Stock/Reverse/", viewerList);
        Assert.DoesNotContain("/Stock/Bulk", viewerList);
        Assert.Contains("/Stock/Bulk", await staff.GetStringAsync("/Stock/Movements"));
    }

    [Fact]
    public async Task The_stock_menu_opens_the_movements_list()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");

        var response = await client.GetAsync("/Stock");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Stock/Movements", response.RedirectPath());
        Assert.Contains("<h1", await client.GetStringAsync("/Stock/Movements"));
    }

    // ---- product history -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_product_page_has_a_history_tab_that_pages_in_place()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var id = await ProductAsync(factory, "HIS-1");
        var extra = 5;
        await PostAsync(factory, [.. Enumerable.Range(0, ListQuery.DefaultPageSize + extra).Select(_ => new StockMovement(id, StockTransactionType.Receive, 1))]);

        var detail = await client.GetStringAsync($"/Products/{id}");
        Assert.Contains("id=\"history\"", detail);
        Assert.Equal(ListQuery.DefaultPageSize, CountRows(detail));
        Assert.Contains($"Showing 1–{ListQuery.DefaultPageSize} of {ListQuery.DefaultPageSize + extra}", detail);
        Assert.Contains($"/Stock/Movements?productId={id}", detail);

        var page2 = await (await client.SendAsync(Htmx($"/Products/{id}?handler=History&page=2"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", page2);
        Assert.Equal(extra, CountRows(page2));

        // The pager of a fragment fetches the fragment handler and does not touch the address bar.
        Assert.Contains("handler=History", detail);
        Assert.Contains("hx-target=\"#history\"", detail);
        Assert.Contains("hx-push-url=\"false\"", detail);

        static int CountRows(string html) => html.Split("<tr id=\"tx-").Length - 1;
    }

    [Fact]
    public async Task A_product_with_no_movements_shows_an_empty_history()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var id = await ProductAsync(factory, "HIS-2");

        Assert.Contains("No stock history yet", await client.GetStringAsync($"/Products/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Products/9999?handler=History")).StatusCode);
    }

    // ---- verify --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Verify_stock_reports_a_mismatch_and_the_fix_repairs_it()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        var id = await ProductAsync(factory, "VER-1", onHand: 5);

        var clean = await SendFormAsync(admin, "/Admin/VerifyStock?handler=Check", [], "/Admin/VerifyStock");
        Assert.Contains("All good.", await clean.Content.ReadAsStringAsync());

        // Corrupt the cache behind the service's back, the way a rogue script would.
        await factory.WithScopeAsync(async sp => await sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync($"UPDATE Products SET QuantityOnHand = 99 WHERE Id = {id}"));

        var bad = await (await SendFormAsync(admin, "/Admin/VerifyStock?handler=Check", [], "/Admin/VerifyStock")).Content.ReadAsStringAsync();
        Assert.Contains("Mismatch found", bad);
        Assert.Contains("VER-1", bad);
        Assert.Equal(99, await OnHandAsync(factory, id)); // checking does not repair

        var fix = await SendFormAsync(admin, "/Admin/VerifyStock?handler=Fix", [], "/Admin/VerifyStock");
        Assert.Equal(HttpStatusCode.Redirect, fix.StatusCode);
        Assert.Equal("/Admin/VerifyStock", fix.RedirectPath());
        Assert.Equal(5, await OnHandAsync(factory, id));
        Assert.Contains("Repaired 1 part from the ledger: VER-1", await admin.GetStringAsync("/Admin/VerifyStock"));
    }

    [Fact]
    public async Task Verify_stock_is_for_administrators_only_and_linked_from_the_admin_page()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        using var admin = factory.CreateClientAs("Admin");

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/Admin/VerifyStock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsync("/Admin/VerifyStock?handler=Fix", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Contains("/Admin/VerifyStock", await admin.GetStringAsync("/Admin"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin/VerifyStock")).StatusCode);
    }
}
