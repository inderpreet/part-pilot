using System.Net;
using System.Text.RegularExpressions;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-05-3: PO list, editor (htmx lines), detail actions, receiving, print view, and the product / supplier / movements links.</summary>
public partial class PurchaseOrderPagesTests
{
    [GeneratedRegex("name=\"(?:Input\\.RowVersion|rowVersion)\" value=\"(\\d+)\"")]
    private static partial Regex RowVersionPattern();

    private static HttpRequestMessage Htmx(string url, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return request;
    }

    private static async Task<HttpResponseMessage> HtmxPostAsync(HttpClient client, string url, IEnumerable<(string, string)> fields, string tokenPage)
    {
        var request = Htmx(url, HttpMethod.Post);
        request.Headers.Add("RequestVerificationToken", await WebForms.TokenAsync(client, tokenPage));
        request.Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.SendAsync(request);
    }

    private static async Task<string> RowVersionOfAsync(HttpClient client, string page)
        => RowVersionPattern().Match(await client.GetStringAsync(page)).Groups[1].Value;

    private static async Task<SupplierDetail> SeedSupplierAsync(AppFactory factory, string name, string? address = null)
    {
        SupplierDetail? created = null;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<ISupplierService>().CreateAsync(new SupplierInput(name, "Jo Bloggs", "jo@acme.test", "555-0100", address, null, null));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            created = result.Value;
        });
        return created!;
    }

    private static async Task<ProductDetail> SeedProductAsync(AppFactory factory, string sku, string name, long costCents = 500)
    {
        ProductDetail? created = null;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<IProductService>().CreateAsync(new ProductInput(sku, name, null, null, null, null, null, 0, 0, costCents));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            created = result.Value;
        });
        return created!;
    }

    private static Task LinkAsync(AppFactory factory, int productId, int supplierId, long cost) =>
        factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<ISupplierService>().LinkProductAsync(productId, supplierId, new SupplierLinkInput("S-" + productId, cost, 7, 5, false));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        });

    private static async Task<PurchaseOrderDetail> SeedOrderAsync(
        AppFactory factory, int supplierId, (int ProductId, int Quantity, long Cost)[] lines,
        bool submit = false, DateOnly? orderDate = null, DateOnly? expected = null)
    {
        PurchaseOrderDetail? order = null;
        var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        await factory.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<IPurchaseOrderService>();
            var actor = new PurchaseOrderActor(userId, IsAdmin: true);
            var created = await service.CreateDraftAsync(supplierId, actor, [.. lines.Select(l => new PurchaseOrderLineInput(l.ProductId, l.Quantity, l.Cost))], orderDate);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Message)));
            order = created.Value;

            if (expected is not null)
            {
                var updated = await service.UpdateHeaderAsync(order.Id, order.RowVersion, new PurchaseOrderHeaderInput(supplierId, order.OrderDate, expected, null));
                Assert.True(updated.Succeeded, string.Join("; ", updated.Errors.Select(e => e.Message)));
                order = updated.Value;
            }

            if (submit)
            {
                var submitted = await service.SubmitAsync(order.Id, actor);
                Assert.True(submitted.Succeeded, string.Join("; ", submitted.Errors.Select(e => e.Message)));
                order = submitted.Value;
            }
        });
        return order!;
    }

    private static async Task<PurchaseOrderDetail> GetOrderAsync(AppFactory factory, int id)
    {
        PurchaseOrderDetail? order = null;
        await factory.WithScopeAsync(async sp => order = await sp.GetRequiredService<IPurchaseOrderService>().GetAsync(id));
        return order!;
    }

    private static async Task<int> OnHandAsync(AppFactory factory, int productId)
    {
        var onHand = -1;
        await factory.WithScopeAsync(async sp => onHand = (await sp.GetRequiredService<IProductService>().GetAsync(productId))!.QuantityOnHand);
        return onHand;
    }

    private static IEnumerable<(string, string)> ReceiveFields(PurchaseOrderDetail order, params int[] quantities)
    {
        yield return ("Input.RowVersion", order.RowVersion.ToString());
        for (var i = 0; i < order.Lines.Count; i++)
        {
            yield return ($"Input.Lines[{i}].LineId", order.Lines[i].Id.ToString());
            yield return ($"Input.Lines[{i}].Quantity", quantities[i].ToString());
        }
    }

    // ---- the whole lifecycle ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_draft_is_built_in_the_editor_submitted_received_in_two_deliveries_and_closed()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget", 500);
        var gadget = await SeedProductAsync(factory, "G-1", "Gadget", 250);
        await LinkAsync(factory, widget.Id, supplier.Id, 450);

        // Create the draft from the header form.
        var create = await WebForms.PostFormAsync(client, "/PurchaseOrders/Edit",
            [("Input.SupplierId", supplier.Id.ToString()), ("Input.OrderDate", "2026-03-02"), ("Input.ExpectedDate", "2026-03-20"), ("Input.Notes", "Deliver to dock 2")]);
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var editUrl = create.RedirectPath();
        Assert.Matches("^/PurchaseOrders/Edit/\\d+$", editUrl);
        var id = int.Parse(editUrl[(editUrl.LastIndexOf('/') + 1)..]);

        var editor = await client.GetStringAsync(editUrl);
        Assert.Contains("Draft", editor);
        Assert.Contains("Deliver to dock 2", editor);
        Assert.Contains("No lines yet", editor);
        Assert.Contains("id=\"po-lines\"", editor);

        // Add two lines through htmx: the first takes the supplier link's price, the second a typed one.
        var firstAdd = await HtmxPostAsync(client, $"/PurchaseOrders/Edit/{id}?handler=AddLine", [("productId", widget.Id.ToString()), ("quantity", "10"), ("unitCost", "")], editUrl);
        Assert.Equal(HttpStatusCode.OK, firstAdd.StatusCode);
        var firstHtml = await firstAdd.Content.ReadAsStringAsync();
        Assert.Contains("W-1", firstHtml);
        Assert.Contains("45.00", firstHtml);
        Assert.DoesNotContain("<html", firstHtml);

        var secondAdd = await HtmxPostAsync(client, $"/PurchaseOrders/Edit/{id}?handler=AddLine", [("productId", gadget.Id.ToString()), ("quantity", "4"), ("unitCost", "2.50")], editUrl);
        Assert.Contains("55.00", await secondAdd.Content.ReadAsStringAsync()); // 10 x 45.00 + 4 x 2.50

        // Change the widget quantity in place; the totals follow.
        var draft = await GetOrderAsync(factory, id);
        var widgetLine = draft.Lines.Single(l => l.ProductId == widget.Id);
        var save = await HtmxPostAsync(client, $"/PurchaseOrders/Edit/{id}?handler=SaveLine", [("lineId", widgetLine.Id.ToString()), ("quantity", "20"), ("unitCost", "4.50")], editUrl);
        Assert.Contains("100.00", await save.Content.ReadAsStringAsync()); // 20 x 45.00 + 10.00

        // Remove and re-add the gadget to prove removal.
        var gadgetLine = draft.Lines.Single(l => l.ProductId == gadget.Id);
        var remove = await HtmxPostAsync(client, $"/PurchaseOrders/Edit/{id}?handler=RemoveLine", [("lineId", gadgetLine.Id.ToString())], editUrl);
        var removed = await remove.Content.ReadAsStringAsync();
        Assert.DoesNotContain("G-1", removed);
        Assert.Contains("90.00", removed);
        await HtmxPostAsync(client, $"/PurchaseOrders/Edit/{id}?handler=AddLine", [("productId", gadget.Id.ToString()), ("quantity", "4"), ("unitCost", "2.50")], editUrl);

        // The detail page offers Submit; submit it.
        var detailUrl = $"/PurchaseOrders/{id}";
        var detail = await client.GetStringAsync(detailUrl);
        Assert.Contains("Submit order", detail);
        Assert.DoesNotContain("Receive goods", detail);
        var submit = await HtmxPostAsync(client, $"{detailUrl}?handler=Submit", [("rowVersion", RowVersionPattern().Match(detail).Groups[1].Value)], detailUrl);
        Assert.Equal("/PurchaseOrders/" + id, submit.Headers.GetValues("HX-Redirect").Single());

        var submitted = await client.GetStringAsync(detailUrl);
        Assert.Contains("badge-submitted", submitted);
        Assert.Contains("submitted", submitted); // the flash
        Assert.Contains("Receive goods", submitted);
        Assert.DoesNotContain("Submit order", submitted);

        // The product page now lists it as on order.
        var product = await client.GetStringAsync($"/Products/{widget.Id}");
        Assert.Contains("Open purchase orders", product);
        Assert.Contains($"/PurchaseOrders/{id}", product);

        // First delivery: 8 widgets and all 4 gadgets.
        var order = await GetOrderAsync(factory, id);
        var receivePage = await client.GetStringAsync($"{detailUrl}/Receive");
        Assert.Contains("data-receive-all", receivePage);
        Assert.Contains("data-outstanding=\"20\"", receivePage);
        var firstReceipt = await WebForms.PostFormAsync(client, $"{detailUrl}/Receive",
            [.. ReceiveFields(order, order.Lines.Select(l => l.ProductId == widget.Id ? 8 : 4).ToArray()), ("Input.Reference", "DN-1001")]);
        Assert.Equal(HttpStatusCode.Redirect, firstReceipt.StatusCode);
        Assert.Equal(detailUrl, firstReceipt.RedirectPath());

        var partial = await client.GetStringAsync(detailUrl);
        Assert.Contains("badge-partial", partial);
        Assert.Contains("Received 12 units on 2 lines", partial);
        Assert.Contains("ref DN-1001", partial);
        Assert.Equal(8, await OnHandAsync(factory, widget.Id));
        Assert.Equal(4, await OnHandAsync(factory, gadget.Id));
        Assert.Contains("Close short", partial);

        // The stock ledger links the receipts back to the order.
        var movements = await client.GetStringAsync("/Stock/Movements");
        Assert.Contains($"/PurchaseOrders/{id}#tx-", movements);

        // Second delivery: the remaining 12 widgets.
        order = await GetOrderAsync(factory, id);
        var secondReceipt = await WebForms.PostFormAsync(client, $"{detailUrl}/Receive",
            [.. ReceiveFields(order, order.Lines.Select(l => l.ProductId == widget.Id ? 12 : 0).ToArray())]);
        Assert.Equal(HttpStatusCode.Redirect, secondReceipt.StatusCode);

        var received = await client.GetStringAsync(detailUrl);
        Assert.Contains("badge-received", received);
        Assert.DoesNotContain("Receive goods", received);
        Assert.Equal(20, await OnHandAsync(factory, widget.Id));
        Assert.Equal(PurchaseOrderStatus.Received, (await GetOrderAsync(factory, id)).Status);
        Assert.Contains("Nothing is on order", await client.GetStringAsync($"/Products/{widget.Id}")); // a received order is no longer on order
    }

    // ---- list ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_opens_on_open_orders_and_filters_by_tab_supplier_search_and_dates_with_overdue_highlighted()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var acme = await SeedSupplierAsync(factory, "Acme Tools");
        var bolt = await SeedSupplierAsync(factory, "Bolt Bros");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var line = new[] { (widget.Id, 5, 1000L) };

        var overdue = await SeedOrderAsync(factory, acme.Id, line, submit: true, orderDate: new DateOnly(2020, 1, 1), expected: new DateOnly(2020, 2, 1));
        var draft = await SeedOrderAsync(factory, bolt.Id, line, orderDate: new DateOnly(2026, 2, 1));

        var open = await client.GetStringAsync("/PurchaseOrders");
        Assert.Contains(overdue.Number, open);
        Assert.DoesNotContain(draft.Number, open);
        Assert.Contains("po-overdue", open);
        Assert.Contains("50.00", open); // total and outstanding: 5 x 10.00

        var drafts = await client.GetStringAsync("/PurchaseOrders?tab=Draft");
        Assert.Contains(draft.Number, drafts);
        Assert.DoesNotContain(overdue.Number, drafts);

        var all = await client.GetStringAsync("/PurchaseOrders?tab=All");
        Assert.Contains(overdue.Number, all);
        Assert.Contains(draft.Number, all);

        // htmx answers with just the results block.
        var bySupplier = await (await client.SendAsync(Htmx($"/PurchaseOrders?tab=All&supplierId={bolt.Id}"))).Content.ReadAsStringAsync();
        Assert.Contains(draft.Number, bySupplier);
        Assert.DoesNotContain(overdue.Number, bySupplier);
        Assert.DoesNotContain("<html", bySupplier);

        var bySearch = await (await client.SendAsync(Htmx($"/PurchaseOrders?tab=All&search={Uri.EscapeDataString(draft.Number)}"))).Content.ReadAsStringAsync();
        Assert.Contains(draft.Number, bySearch);
        Assert.DoesNotContain(overdue.Number, bySearch);

        var byDate = await (await client.SendAsync(Htmx("/PurchaseOrders?tab=All&from=2026-01-01&to=2026-12-31"))).Content.ReadAsStringAsync();
        Assert.Contains(draft.Number, byDate);
        Assert.DoesNotContain(overdue.Number, byDate);

        var sorted = await (await client.SendAsync(Htmx("/PurchaseOrders?tab=All&sort=supplier&desc=true"))).Content.ReadAsStringAsync();
        Assert.True(sorted.IndexOf("Bolt Bros", StringComparison.Ordinal) < sorted.IndexOf("Acme Tools", StringComparison.Ordinal));

        Assert.Contains("No purchase orders match", await (await client.SendAsync(Htmx("/PurchaseOrders?tab=All&search=zzz"))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_empty_list_offers_a_create_link_only_to_editors()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        using var viewer = factory.CreateClientAs("Viewer");

        var adminHtml = await admin.GetStringAsync("/PurchaseOrders?tab=All");
        Assert.Contains("No purchase orders yet", adminHtml);
        Assert.Contains("/PurchaseOrders/Edit", adminHtml);

        var viewerHtml = await viewer.GetStringAsync("/PurchaseOrders?tab=All");
        Assert.Contains("No purchase orders yet", viewerHtml);
        Assert.DoesNotContain("New purchase order", viewerHtml);

        Assert.Contains("No open purchase orders", await admin.GetStringAsync("/PurchaseOrders"));
    }

    // ---- editor ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_editor_reports_line_problems_as_422_and_keeps_what_was_typed()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var order = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 5, 1000L)]);
        var url = $"/PurchaseOrders/Edit/{order.Id}";
        var add = $"{url}?handler=AddLine";

        var noProduct = await HtmxPostAsync(client, add, [("quantity", "3"), ("unitCost", "")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noProduct.StatusCode);
        Assert.Contains("Choose a product", await noProduct.Content.ReadAsStringAsync());

        var badQuantity = await HtmxPostAsync(client, add, [("productId", widget.Id.ToString()), ("q", "W-1 — Widget"), ("quantity", "abc"), ("unitCost", "")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badQuantity.StatusCode);
        var badHtml = await badQuantity.Content.ReadAsStringAsync();
        Assert.Contains("whole number", badHtml);
        Assert.Contains("W-1 — Widget", WebUtility.HtmlDecode(badHtml));

        var badCost = await HtmxPostAsync(client, add, [("productId", widget.Id.ToString()), ("quantity", "3"), ("unitCost", "twelve")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCost.StatusCode);
        Assert.Contains("12.50", await badCost.Content.ReadAsStringAsync());

        var duplicate = await HtmxPostAsync(client, add, [("productId", widget.Id.ToString()), ("quantity", "3"), ("unitCost", "")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);

        var zero = await HtmxPostAsync(client, $"{url}?handler=SaveLine", [("lineId", order.Lines[0].Id.ToString()), ("quantity", "0"), ("unitCost", "10.00")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);
        Assert.Contains("greater than zero", await zero.Content.ReadAsStringAsync());
        Assert.Equal(5, (await GetOrderAsync(factory, order.Id)).Lines[0].QuantityOrdered);
    }

    [Fact]
    public async Task The_product_picker_lists_the_suppliers_products_first_and_marks_ones_already_on_the_order()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var linked = await SeedProductAsync(factory, "Z-LINKED", "Zebra part");
        var other = await SeedProductAsync(factory, "A-OTHER", "Aardvark part");
        var onOrder = await SeedProductAsync(factory, "M-ORDERED", "Mouse part");
        await LinkAsync(factory, linked.Id, supplier.Id, 700);
        var order = await SeedOrderAsync(factory, supplier.Id, [(onOrder.Id, 1, 100L)]);

        var html = await (await client.SendAsync(Htmx($"/PurchaseOrders/Edit/{order.Id}?handler=Products&q=part"))).Content.ReadAsStringAsync();

        Assert.True(html.IndexOf("Z-LINKED", StringComparison.Ordinal) < html.IndexOf("A-OTHER", StringComparison.Ordinal), "linked products come first");
        Assert.Contains("data-picker-select", html);
        Assert.Contains("already on this order", html);
        Assert.Contains($"data-id=\"{other.Id}\"", html);
        Assert.DoesNotContain($"data-id=\"{onOrder.Id}\"", html);
    }

    [Fact]
    public async Task The_header_form_saves_with_a_version_check_and_the_supplier_locks_once_there_are_lines()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var acme = await SeedSupplierAsync(factory, "Acme Tools");
        var bolt = await SeedSupplierAsync(factory, "Bolt Bros");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var empty = await SeedOrderAsync(factory, acme.Id, []);
        var withLines = await SeedOrderAsync(factory, acme.Id, [(widget.Id, 5, 1000L)]);

        // No lines: the supplier can be changed.
        var emptyUrl = $"/PurchaseOrders/Edit/{empty.Id}";
        var change = await WebForms.PostFormAsync(client, emptyUrl,
            [("Input.SupplierId", bolt.Id.ToString()), ("Input.OrderDate", "2026-03-02"), ("Input.Notes", "moved"), ("Input.RowVersion", await RowVersionOfAsync(client, emptyUrl))]);
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);
        Assert.Equal("Bolt Bros", (await GetOrderAsync(factory, empty.Id)).SupplierName);

        // With lines: the select is disabled and a posted supplier is ignored.
        var linesUrl = $"/PurchaseOrders/Edit/{withLines.Id}";
        Assert.Contains("disabled", Regex.Match(await client.GetStringAsync(linesUrl), "<select[^>]*>").Value);
        var ignored = await WebForms.PostFormAsync(client, linesUrl,
            [("Input.SupplierId", bolt.Id.ToString()), ("Input.OrderDate", "2026-03-02"), ("Input.RowVersion", await RowVersionOfAsync(client, linesUrl))]);
        Assert.Equal(HttpStatusCode.Redirect, ignored.StatusCode);
        Assert.Equal("Acme Tools", (await GetOrderAsync(factory, withLines.Id)).SupplierName);

        // A stale version is a conflict, not a silent overwrite.
        var stale = await WebForms.PostFormAsync(client, linesUrl,
            [("Input.OrderDate", "2026-03-02"), ("Input.Notes", "stale"), ("Input.RowVersion", "1")], linesUrl);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var staleHtml = await stale.Content.ReadAsStringAsync();
        Assert.Contains("Someone else changed this order", staleHtml);

        // The expected date may not precede the order date.
        var early = await WebForms.PostFormAsync(client, linesUrl,
            [("Input.OrderDate", "2026-03-02"), ("Input.ExpectedDate", "2026-03-01"), ("Input.RowVersion", await RowVersionOfAsync(client, linesUrl))], linesUrl);
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Contains("cannot be before the order date", await early.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_submitted_order_only_allows_its_expected_date_and_notes_to_change_and_a_closed_one_is_not_editable()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var order = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 5, 1000L)], submit: true);
        var url = $"/PurchaseOrders/Edit/{order.Id}";

        var page = await client.GetStringAsync(url);
        Assert.Contains("Lines can only be changed while the order is a draft", page);
        Assert.DoesNotContain("id=\"po-lines\"", page);

        var save = await WebForms.PostFormAsync(client, url, [("Input.ExpectedDate", "2030-01-15"), ("Input.Notes", "call before delivery"), ("Input.RowVersion", await RowVersionOfAsync(client, url))]);
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Equal($"/PurchaseOrders/{order.Id}", save.RedirectPath());
        var saved = await GetOrderAsync(factory, order.Id);
        Assert.Equal(new DateOnly(2030, 1, 15), saved.ExpectedDate);
        Assert.Equal("call before delivery", saved.Notes);

        // Line handlers refuse once submitted.
        var add = await HtmxPostAsync(client, $"{url}?handler=AddLine", [("productId", widget.Id.ToString()), ("quantity", "1")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);

        // A cancelled order has nothing to edit: the editor sends you back to it.
        await factory.WithScopeAsync(async sp =>
        {
            var userId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
            Assert.True((await sp.GetRequiredService<IPurchaseOrderService>().CancelAsync(order.Id, new PurchaseOrderActor(userId, true))).Succeeded);
        });
        var closed = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        Assert.Equal($"/PurchaseOrders/{order.Id}", closed.RedirectPath());
    }

    // ---- detail actions --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_close_short_and_delete_draft_work_with_confirmation_buttons_and_flash()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var line = new[] { (widget.Id, 10, 1000L) };

        // Draft: shows Delete draft and Cancel; deleting returns to the list.
        var draft = await SeedOrderAsync(factory, supplier.Id, line);
        var draftUrl = $"/PurchaseOrders/{draft.Id}";
        var draftHtml = await client.GetStringAsync(draftUrl);
        Assert.Contains("Delete draft", draftHtml);
        Assert.Contains("hx-confirm=", draftHtml);
        var delete = await HtmxPostAsync(client, $"{draftUrl}?handler=Delete", [("rowVersion", RowVersionPattern().Match(draftHtml).Groups[1].Value)], draftUrl);
        Assert.Equal("/PurchaseOrders", delete.Headers.GetValues("HX-Redirect").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(draftUrl)).StatusCode);

        // Submitted: cancel without htmx (plain form post redirects back).
        var submitted = await SeedOrderAsync(factory, supplier.Id, line, submit: true);
        var submittedUrl = $"/PurchaseOrders/{submitted.Id}";
        var cancel = await WebForms.PostFormAsync(client, $"{submittedUrl}?handler=Cancel", [("rowVersion", await RowVersionOfAsync(client, submittedUrl))], submittedUrl);
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Assert.Equal(submittedUrl, cancel.RedirectPath());
        var cancelled = await client.GetStringAsync(submittedUrl);
        Assert.Contains("badge-cancelled", cancelled);
        Assert.Contains("cancelled", cancelled);
        Assert.DoesNotContain("Receive goods", cancelled);
        Assert.DoesNotContain("Edit order", cancelled);

        // Partially received: close short.
        var partial = await SeedOrderAsync(factory, supplier.Id, line, submit: true);
        var partialUrl = $"/PurchaseOrders/{partial.Id}";
        var receive = await WebForms.PostFormAsync(client, $"{partialUrl}/Receive", [.. ReceiveFields(partial, 4)]);
        Assert.Equal(HttpStatusCode.Redirect, receive.StatusCode);
        var partialHtml = await client.GetStringAsync(partialUrl);
        Assert.Contains("Close short", partialHtml);
        var close = await HtmxPostAsync(client, $"{partialUrl}?handler=CloseShort", [("rowVersion", RowVersionPattern().Match(partialHtml).Groups[1].Value)], partialUrl);
        Assert.Equal(partialUrl, close.Headers.GetValues("HX-Redirect").Single());
        Assert.Equal(PurchaseOrderStatus.Received, (await GetOrderAsync(factory, partial.Id)).Status);
        Assert.Equal(4, await OnHandAsync(factory, widget.Id));
    }

    [Fact]
    public async Task A_receipt_can_be_reversed_from_the_order_which_reopens_it()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var order = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 10, 1000L)], submit: true);
        var url = $"/PurchaseOrders/{order.Id}";

        await WebForms.PostFormAsync(client, $"{url}/Receive", [.. ReceiveFields(order, 10)]);
        Assert.Equal(PurchaseOrderStatus.Received, (await GetOrderAsync(factory, order.Id)).Status);
        Assert.Equal(10, await OnHandAsync(factory, widget.Id));

        var detail = await client.GetStringAsync(url);
        var transactionId = Regex.Match(detail, "name=\"transactionId\" value=\"(\\d+)\"").Groups[1].Value;
        Assert.NotEmpty(transactionId);

        var reverse = await HtmxPostAsync(client, $"{url}?handler=Reverse", [("transactionId", transactionId)], url);
        Assert.Equal(url, reverse.Headers.GetValues("HX-Redirect").Single());

        var after = await client.GetStringAsync(url);
        Assert.Contains("reversed", after);
        Assert.Contains("Receive goods", after); // reopened
        Assert.Equal(0, await OnHandAsync(factory, widget.Id));
        Assert.Equal(PurchaseOrderStatus.Submitted, (await GetOrderAsync(factory, order.Id)).Status);

        // The generic reverse on the movements page points at the order instead of offering the button.
        var movements = await client.GetStringAsync("/Stock/Movements");
        Assert.DoesNotContain("/Stock/Reverse/", movements);
    }

    // ---- receiving -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Receiving_more_than_ordered_is_refused_for_staff_and_allowed_for_an_admin_who_ticks_the_box()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        using var admin = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var order = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 10, 1000L)], submit: true);
        var url = $"/PurchaseOrders/{order.Id}/Receive";

        Assert.DoesNotContain("AllowOverReceipt", await staff.GetStringAsync(url));
        Assert.Contains("AllowOverReceipt", await admin.GetStringAsync(url));

        // Staff: 12 of 10 is refused with a message on the line; nothing is posted.
        var refused = await WebForms.PostFormAsync(staff, url, [.. ReceiveFields(order, 12), ("Input.AllowOverReceipt", "true")], url);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("validation-summary", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, await OnHandAsync(factory, widget.Id));

        // Admin without the tick: refused, and told how to proceed.
        var unticked = await WebForms.PostFormAsync(admin, url, [.. ReceiveFields(order, 12)], url);
        Assert.Equal(HttpStatusCode.OK, unticked.StatusCode);
        var untickedHtml = await unticked.Content.ReadAsStringAsync();
        Assert.Contains("validation-summary", untickedHtml);
        Assert.Contains("Allow over-receipt", untickedHtml);
        Assert.Equal(0, await OnHandAsync(factory, widget.Id));

        // Admin with the tick: 12 are received and the line is complete.
        var ticked = await WebForms.PostFormAsync(admin, url, [.. ReceiveFields(order, 12), ("Input.AllowOverReceipt", "true")], url);
        Assert.Equal(HttpStatusCode.Redirect, ticked.StatusCode);
        Assert.Equal(12, await OnHandAsync(factory, widget.Id));
        Assert.Equal(PurchaseOrderStatus.Received, (await GetOrderAsync(factory, order.Id)).Status);
    }

    [Fact]
    public async Task The_receive_form_reports_typos_and_empty_receipts_without_posting()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var order = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 10, 1000L)], submit: true);
        var url = $"/PurchaseOrders/{order.Id}/Receive";

        var typo = await WebForms.PostFormAsync(client, url, [("Input.Lines[0].LineId", order.Lines[0].Id.ToString()), ("Input.Lines[0].Quantity", "ten")], url);
        Assert.Equal(HttpStatusCode.OK, typo.StatusCode);
        Assert.Contains("whole number", await typo.Content.ReadAsStringAsync());

        var nothing = await WebForms.PostFormAsync(client, url, [.. ReceiveFields(order, 0)], url);
        Assert.Equal(HttpStatusCode.OK, nothing.StatusCode);
        Assert.Contains("validation-summary", await nothing.Content.ReadAsStringAsync());

        Assert.Equal(0, await OnHandAsync(factory, widget.Id));
    }

    [Fact]
    public async Task A_draft_or_cancelled_order_cannot_be_received_against()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var draft = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 10, 1000L)]);

        var response = await client.GetAsync($"/PurchaseOrders/{draft.Id}/Receive");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/PurchaseOrders/{draft.Id}", response.RedirectPath());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/PurchaseOrders/99999/Receive")).StatusCode);
    }

    // ---- print -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_print_view_has_no_site_chrome_and_prints_the_supplier_lines_total_and_notes()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools", address: "1 Anvil Way\nSpringfield");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var order = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 3, 1250L)], submit: true);

        var html = await client.GetStringAsync($"/PurchaseOrders/{order.Id}/Print");

        Assert.Contains("data-auto-print", html);
        Assert.Contains("print-sheet", html);
        Assert.DoesNotContain("main-nav", html);
        Assert.DoesNotContain("app-header", html);
        Assert.Contains(order.Number, html);
        Assert.Contains("Acme Tools", html);
        Assert.Contains("1 Anvil Way", html);
        Assert.Contains("Widget", html);
        Assert.Contains("37.50", html); // 3 x 12.50
        Assert.DoesNotContain("DRAFT", html);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/PurchaseOrders/99999/Print")).StatusCode);
    }

    [Fact]
    public async Task A_draft_prints_with_a_draft_stamp()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var order = await SeedOrderAsync(factory, supplier.Id, []);

        Assert.Contains("DRAFT", await client.GetStringAsync($"/PurchaseOrders/{order.Id}/Print"));
    }

    // ---- other pages -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_supplier_page_lists_recent_orders_and_offers_a_new_one_pre_filled_with_the_supplier()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var order = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 3, 1250L)], submit: true);

        var html = await client.GetStringAsync($"/Suppliers/{supplier.Id}");
        Assert.Contains("Recent purchase orders", html);
        Assert.Contains($"/PurchaseOrders/{order.Id}", html);
        Assert.Contains($"/PurchaseOrders/Edit?supplierId={supplier.Id}", html);

        var form = await client.GetStringAsync($"/PurchaseOrders/Edit?supplierId={supplier.Id}");
        Assert.Matches($"<option value=\"{supplier.Id}\" selected", form);
    }

    [Fact]
    public async Task The_product_page_shows_only_open_orders_that_still_have_something_outstanding()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");

        Assert.Contains("Nothing is on order", await client.GetStringAsync($"/Products/{widget.Id}"));

        var draft = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 5, 1000L)]);
        Assert.Contains("Nothing is on order", await client.GetStringAsync($"/Products/{widget.Id}")); // drafts are not "on order"

        var open = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 7, 1000L)], submit: true);
        var html = await client.GetStringAsync($"/Products/{widget.Id}");
        Assert.Contains(open.Number, html);
        Assert.DoesNotContain(draft.Number, html);
        Assert.Contains("<strong>7</strong>", html);
    }

    // ---- authorization ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Viewers_can_read_orders_but_have_no_action_buttons_and_cannot_edit_receive_or_post()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        using var viewer = factory.CreateClientAs("Viewer");
        var supplier = await SeedSupplierAsync(factory, "Acme Tools");
        var widget = await SeedProductAsync(factory, "W-1", "Widget");
        var order = await SeedOrderAsync(factory, supplier.Id, [(widget.Id, 10, 1000L)], submit: true);
        var url = $"/PurchaseOrders/{order.Id}";

        // Reading works, and the list and print links are there.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/PurchaseOrders")).StatusCode);
        var detail = await viewer.GetStringAsync(url);
        Assert.Contains(order.Number, detail);
        Assert.Contains("Print", detail);
        Assert.DoesNotContain("New purchase order", await viewer.GetStringAsync("/PurchaseOrders"));

        // ... but none of the actions.
        foreach (var label in new[] { "Receive goods", "Submit order", "Cancel order", "Close short", "Delete draft", "Edit order", "Edit dates", "Reverse" })
        {
            Assert.DoesNotContain(label, detail);
        }

        // Pages and handlers are forbidden.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/PurchaseOrders/Edit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/PurchaseOrders/Edit/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"{url}/Receive")).StatusCode);
        foreach (var handler in new[] { "Submit", "Cancel", "CloseShort", "Delete", "Reverse" })
        {
            var post = await WebForms.PostFormAsync(viewer, $"{url}?handler={handler}", [("rowVersion", order.RowVersion.ToString()), ("transactionId", "1")], url);
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        }

        var receive = await WebForms.PostFormAsync(viewer, $"{url}/Receive", [.. ReceiveFields(order, 5)], url);
        Assert.Equal(HttpStatusCode.Forbidden, receive.StatusCode);
        Assert.Equal(0, await OnHandAsync(factory, widget.Id));
        Assert.Equal(PurchaseOrderStatus.Submitted, (await GetOrderAsync(factory, order.Id)).Status);

        // An editor does see them.
        var adminDetail = await admin.GetStringAsync(url);
        Assert.Contains("Receive goods", adminDetail);
        Assert.Contains("Cancel order", adminDetail);
    }

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_sign_in()
    {
        using var factory = new AppFactory();
        using var anonymous = factory.CreateAnonymousClient();

        foreach (var url in new[] { "/PurchaseOrders", "/PurchaseOrders/1", "/PurchaseOrders/Edit", "/PurchaseOrders/1/Receive", "/PurchaseOrders/1/Print" })
        {
            var response = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/Login", response.RedirectPath());
        }
    }

    [Fact]
    public async Task The_main_nav_links_to_purchase_orders()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");

        var html = await client.GetStringAsync("/PurchaseOrders");

        Assert.Contains("href=\"/PurchaseOrders\" aria-current=\"page\">Purchase Orders</a>", html);
    }

    [Fact]
    public async Task Unknown_orders_are_404()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/PurchaseOrders/99999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/PurchaseOrders/Edit/99999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Htmx("/PurchaseOrders/Edit/99999?handler=Products&q=x"))).StatusCode);
    }
}
