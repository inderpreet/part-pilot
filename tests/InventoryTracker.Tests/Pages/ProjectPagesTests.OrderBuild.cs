using System.Net;
using System.Text.RegularExpressions;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-10-4: the order-shortages review, the Build action and the build history.</summary>
public partial class ProjectPagesTests
{
    private record Decision(long LineId, string Action, string? Quantity = null, int? DraftId = null);

    private static List<(string, string)> OrderFields(string? kind, int? purchaseOrderId, int? supplierId, params Decision[] decisions)
    {
        var fields = new List<(string, string)>();
        if (kind is not null)
        {
            fields.Add(("Input.Kind", kind));
        }

        if (purchaseOrderId is { } order)
        {
            fields.Add(("Input.PurchaseOrderId", order.ToString()));
        }

        if (supplierId is { } supplier)
        {
            fields.Add(("Input.SupplierId", supplier.ToString()));
        }

        for (var i = 0; i < decisions.Length; i++)
        {
            fields.Add(($"Input.Lines[{i}].LineId", decisions[i].LineId.ToString()));
            fields.Add(($"Input.Lines[{i}].Action", decisions[i].Action));
            if (decisions[i].Quantity is { } quantity)
            {
                fields.Add(($"Input.Lines[{i}].Quantity", quantity));
            }

            if (decisions[i].DraftId is { } draft)
            {
                fields.Add(($"Input.Lines[{i}].DraftId", draft.ToString()));
            }
        }

        return fields;
    }

    private static long LineOf(ProjectDetail project, int productId) => project.Lines.Single(l => l.ProductId == productId).Id;

    private static async Task<PurchaseOrderDetail> GetOrderAsync(AppFactory factory, int id)
    {
        PurchaseOrderDetail? order = null;
        await factory.WithScopeAsync(async sp => order = await sp.GetRequiredService<IPurchaseOrderService>().GetAsync(id));
        return order!;
    }

    private static async Task<int> DraftCountAsync(AppFactory factory)
    {
        var count = 0;
        await factory.WithScopeAsync(async sp => count = (await sp.GetRequiredService<IPurchaseOrderService>().SearchAsync(new PurchaseOrderListQuery { Tab = PurchaseOrderTab.Draft })).TotalCount);
        return count;
    }

    private static async Task<int> OnHandAsync(AppFactory factory, int productId)
    {
        var onHand = 0;
        await factory.WithScopeAsync(async sp => onHand = (await sp.GetRequiredService<IProductService>().GetAsync(productId))!.QuantityOnHand);
        return onHand;
    }

    private static async Task LinkAsync(AppFactory factory, int productId, int supplierId, int minOrder)
        => await factory.WithScopeAsync(async sp =>
        {
            var linked = await sp.GetRequiredService<ISupplierService>().LinkProductAsync(productId, supplierId, new SupplierLinkInput(null, 100, null, minOrder, true));
            Assert.True(linked.Succeeded, string.Join("; ", linked.Errors.Select(e => e.Message)));
        });

    /// <summary>The row of the order table for <paramref name="sku"/>.</summary>
    private static string OrderRow(string html, string sku)
    {
        var rows = Regex.Matches(html, "<tr class=\"order-line[^\"]*\" data-line-row>.*?</tr>", RegexOptions.Singleline);
        var row = rows.Select(m => m.Value).FirstOrDefault(r => r.Contains($"<strong>{sku}</strong>"));
        Assert.NotNull(row);
        return row;
    }

    private static string SelectedAction(string row)
        => Regex.Match(row, "<option value=\"(Add|Update|Skip)\" selected").Groups[1].Value;

    private static string QuantityIn(string row)
        => Regex.Match(row, "name=\"Input\\.Lines\\[\\d+\\]\\.Quantity\" value=\"(\\d+)\"").Groups[1].Value;

    // ---- order shortages -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_order_page_shows_each_short_part_with_stock_on_order_and_in_draft_numbers_and_defaults_to_add()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory);
        var bolt = await SeedProductAsync(factory, "BOLT", onHand: 3);
        var nut = await SeedProductAsync(factory, "NUT");
        var project = await SeedProjectAsync(factory, "Robot", 2, (bolt.Id, 5), (nut.Id, 1));
        await SeedOrderAsync(factory, supplier.Id, bolt.Id, 4, submit: true); // on order

        var html = await client.GetStringAsync($"/Projects/{project.Id}/Order");

        var boltRow = OrderRow(html, "BOLT");
        Assert.Contains(">3<", boltRow); // in stock
        Assert.Contains(">4<", boltRow); // on order
        Assert.Equal("Add", SelectedAction(boltRow));
        Assert.Equal("3", QuantityIn(boltRow)); // 10 needed - 3 in stock - 4 on order
        Assert.Equal("Add", SelectedAction(OrderRow(html, "NUT")));
        Assert.Equal("2", QuantityIn(OrderRow(html, "NUT")));
        Assert.Contains("Where to order", html);
    }

    [Fact]
    public async Task Adding_to_a_new_draft_for_a_supplier_creates_it_with_the_chosen_quantities_and_submits_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory);
        var bolt = await SeedProductAsync(factory, "BOLT");
        var nut = await SeedProductAsync(factory, "NUT");
        var project = await SeedProjectAsync(factory, "Robot", 1, (bolt.Id, 5), (nut.Id, 2));
        var url = $"/Projects/{project.Id}/Order";

        var response = await WebForms.PostFormAsync(client, url, OrderFields("New", null, supplier.Id,
            new Decision(LineOf(project, bolt.Id), "Add", "8"), new Decision(LineOf(project, nut.Id), "Add", "2")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith($"{url}?handler=Result", response.RedirectPath());
        var result = await client.GetStringAsync(response.RedirectPath());
        Assert.Contains("Nothing was submitted", result);
        Assert.Contains("Added", result);

        Assert.Equal(1, await DraftCountAsync(factory));
        var link = Regex.Match(result, "href=\"/PurchaseOrders/(\\d+)\"").Groups[1].Value;
        var order = await GetOrderAsync(factory, int.Parse(link));
        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
        Assert.Equal(supplier.Id, order.SupplierId);
        Assert.Equal(8, order.Lines.Single(l => l.ProductId == bolt.Id).QuantityOrdered);
        Assert.Equal(2, order.Lines.Single(l => l.ProductId == nut.Id).QuantityOrdered);

        // The result is shown once: a refresh goes back to the project.
        var again = await client.GetAsync(response.RedirectPath());
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Equal($"/Projects/{project.Id}", again.RedirectPath());
    }

    [Fact]
    public async Task Add_update_and_skip_can_be_mixed_and_a_draft_is_changed_in_place()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory);
        var added = await SeedProductAsync(factory, "ADD-ME");
        var updated = await SeedProductAsync(factory, "UPD-ME");
        var skipped = await SeedProductAsync(factory, "SKIP-ME");
        var project = await SeedProjectAsync(factory, "Robot", 1, (added.Id, 4), (updated.Id, 10), (skipped.Id, 6));
        var draft = await SeedOrderAsync(factory, supplier.Id, updated.Id, 4, submit: false);
        var other = await SeedSupplierAsync(factory, "Other Co");
        var coveringDraft = await SeedOrderAsync(factory, other.Id, skipped.Id, 6, submit: false);
        var url = $"/Projects/{project.Id}/Order";

        // Defaults first: nothing in a draft adds, a draft that covers it skips, a partial one updates to current + uncovered.
        var page = await client.GetStringAsync(url);
        Assert.Equal("Add", SelectedAction(OrderRow(page, "ADD-ME")));
        Assert.Equal("Update", SelectedAction(OrderRow(page, "UPD-ME")));
        Assert.Equal("10", QuantityIn(OrderRow(page, "UPD-ME"))); // 4 on the draft + 6 still uncovered
        Assert.Equal("Skip", SelectedAction(OrderRow(page, "SKIP-ME")));
        Assert.Contains("Already in draft", OrderRow(page, "UPD-ME"));
        Assert.Contains(draft.Number, OrderRow(page, "UPD-ME"));

        var response = await WebForms.PostFormAsync(client, url, OrderFields("Existing", draft.Id, null,
            new Decision(LineOf(project, added.Id), "Add", "4"),
            new Decision(LineOf(project, updated.Id), "Update", "10", draft.Id),
            new Decision(LineOf(project, skipped.Id), "Skip")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var after = await GetOrderAsync(factory, draft.Id);
        Assert.Equal(2, after.Lines.Count);
        Assert.Equal(4, after.Lines.Single(l => l.ProductId == added.Id).QuantityOrdered);
        Assert.Equal(10, after.Lines.Single(l => l.ProductId == updated.Id).QuantityOrdered);
        Assert.Equal(6, (await GetOrderAsync(factory, coveringDraft.Id)).Lines.Single().QuantityOrdered);
        Assert.Equal(2, await DraftCountAsync(factory)); // nothing new was created
        var result = await client.GetStringAsync(response.RedirectPath());
        Assert.Contains("Quantity 4 → 10", result);
    }

    [Fact]
    public async Task A_part_held_by_two_drafts_is_updated_on_the_one_picked()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var first = await SeedSupplierAsync(factory, "First Co");
        var second = await SeedSupplierAsync(factory, "Second Co");
        var bolt = await SeedProductAsync(factory, "BOLT");
        var project = await SeedProjectAsync(factory, "Robot", 1, (bolt.Id, 10));
        var one = await SeedOrderAsync(factory, first.Id, bolt.Id, 2, submit: false);
        var two = await SeedOrderAsync(factory, second.Id, bolt.Id, 3, submit: false);
        var url = $"/Projects/{project.Id}/Order";

        var page = await client.GetStringAsync(url);
        var row = OrderRow(page, "BOLT");
        Assert.Contains("class=\"draft-select\"", row); // a select, because two drafts hold the part
        Assert.Contains(one.Number, row);
        Assert.Contains(two.Number, row);

        var response = await WebForms.PostFormAsync(client, url, OrderFields(null, null, null, new Decision(LineOf(project, bolt.Id), "Update", "9", two.Id)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(2, (await GetOrderAsync(factory, one.Id)).Lines.Single().QuantityOrdered);
        Assert.Equal(9, (await GetOrderAsync(factory, two.Id)).Lines.Single().QuantityOrdered);
    }

    [Fact]
    public async Task Skipping_everything_or_updating_only_creates_no_draft_and_needs_no_target()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory);
        var bolt = await SeedProductAsync(factory, "BOLT");
        var project = await SeedProjectAsync(factory, "Robot", 1, (bolt.Id, 5));
        var url = $"/Projects/{project.Id}/Order";

        var skip = await WebForms.PostFormAsync(client, url, OrderFields("New", null, null, new Decision(LineOf(project, bolt.Id), "Skip")));
        Assert.Equal(HttpStatusCode.Redirect, skip.StatusCode);
        Assert.Equal(0, await DraftCountAsync(factory));

        var draft = await SeedOrderAsync(factory, supplier.Id, bolt.Id, 2, submit: false);
        var update = await WebForms.PostFormAsync(client, url, OrderFields(null, null, null, new Decision(LineOf(project, bolt.Id), "Update", "5", draft.Id)), url);
        Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
        Assert.Equal(5, (await GetOrderAsync(factory, draft.Id)).Lines.Single().QuantityOrdered);
        Assert.Equal(1, await DraftCountAsync(factory));
    }

    [Fact]
    public async Task An_add_without_a_target_or_with_a_bad_quantity_is_refused_and_changes_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory);
        var bolt = await SeedProductAsync(factory, "BOLT");
        var project = await SeedProjectAsync(factory, "Robot", 1, (bolt.Id, 5));
        var url = $"/Projects/{project.Id}/Order";

        var noTarget = await HtmxPostAsync(client, url, OrderFields("New", null, null, new Decision(LineOf(project, bolt.Id), "Add", "5")), url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTarget.StatusCode);
        Assert.Contains("Choose the supplier for the new draft order", await noTarget.Content.ReadAsStringAsync());

        var badQuantity = await HtmxPostAsync(client, url, OrderFields("New", null, supplier.Id, new Decision(LineOf(project, bolt.Id), "Add", "0")), url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badQuantity.StatusCode);
        Assert.Contains("enter a quantity of 1 or more", await badQuantity.Content.ReadAsStringAsync());

        Assert.Equal(0, await DraftCountAsync(factory));
    }

    [Fact]
    public async Task A_submitted_order_is_never_a_target_and_a_draft_submitted_meanwhile_is_refused_with_the_current_drafts_shown()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory);
        var bolt = await SeedProductAsync(factory, "BOLT");
        var nut = await SeedProductAsync(factory, "NUT");
        var project = await SeedProjectAsync(factory, "Robot", 1, (bolt.Id, 5), (nut.Id, 3));
        var submitted = await SeedOrderAsync(factory, supplier.Id, nut.Id, 1, submit: true);
        var url = $"/Projects/{project.Id}/Order";

        // Posting a submitted order as the target: it is not a draft, so it is not accepted as one.
        var notDraft = await HtmxPostAsync(client, url, OrderFields("Existing", submitted.Id, null, new Decision(LineOf(project, bolt.Id), "Add", "5")), url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notDraft.StatusCode);
        Assert.Single((await GetOrderAsync(factory, submitted.Id)).Lines);

        // A draft that is submitted after the page was opened.
        var draft = await SeedOrderAsync(factory, supplier.Id, bolt.Id, 2, submit: false);
        await client.GetStringAsync(url);
        var userId = await AdminIdAsync(factory);
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IPurchaseOrderService>().SubmitAsync(draft.Id, new PurchaseOrderActor(userId, true))).Succeeded));

        var late = await HtmxPostAsync(client, url, OrderFields("Existing", draft.Id, null, new Decision(LineOf(project, bolt.Id), "Update", "5", draft.Id)), url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, late.StatusCode);
        var body = await late.Content.ReadAsStringAsync();
        Assert.Contains("changed while this page was open", body);
        Assert.Equal(2, (await GetOrderAsync(factory, draft.Id)).Lines.Single().QuantityOrdered);
    }

    [Fact]
    public async Task Update_is_only_offered_for_parts_held_by_a_draft_and_add_not_where_the_target_already_holds_the_part()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory);
        var onSubmitted = await SeedProductAsync(factory, "SUBMITTED");
        var onDraft = await SeedProductAsync(factory, "ONDRAFT");
        var project = await SeedProjectAsync(factory, "Robot", 1, (onSubmitted.Id, 10), (onDraft.Id, 10));
        await SeedOrderAsync(factory, supplier.Id, onSubmitted.Id, 4, submit: true);
        var draft = await SeedOrderAsync(factory, supplier.Id, onDraft.Id, 3, submit: false);

        var html = await client.GetStringAsync($"/Projects/{project.Id}/Order?Input.Kind=Existing&Input.PurchaseOrderId={draft.Id}");

        var submittedRow = OrderRow(html, "SUBMITTED");
        Assert.DoesNotContain("value=\"Update\"", submittedRow);
        Assert.Equal("Add", SelectedAction(submittedRow));
        Assert.Equal("6", QuantityIn(submittedRow));

        var draftRow = OrderRow(html, "ONDRAFT");
        Assert.DoesNotContain("value=\"Add\"", draftRow); // this draft already holds it
        Assert.Contains("value=\"Update\"", draftRow);
        Assert.Equal("10", QuantityIn(draftRow));
    }

    [Fact]
    public async Task Suggested_quantities_follow_the_minimum_order_quantity_of_the_chosen_supplier_and_refresh_with_the_target()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var small = await SeedSupplierAsync(factory, "Small Co");
        var bulk = await SeedSupplierAsync(factory, "Bulk Co");
        var bolt = await SeedProductAsync(factory, "BOLT");
        await LinkAsync(factory, bolt.Id, small.Id, 1);
        await LinkAsync(factory, bolt.Id, bulk.Id, 25);
        var project = await SeedProjectAsync(factory, "Robot", 1, (bolt.Id, 10));
        var url = $"/Projects/{project.Id}/Order";

        var smallLines = await (await client.SendAsync(Htmx($"{url}?handler=Lines&Input.Kind=New&Input.SupplierId={small.Id}"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", smallLines);
        Assert.Equal("10", QuantityIn(OrderRow(smallLines, "BOLT")));

        var bulkLines = await (await client.SendAsync(Htmx($"{url}?handler=Lines&Input.Kind=New&Input.SupplierId={bulk.Id}"))).Content.ReadAsStringAsync();
        Assert.Equal("25", QuantityIn(OrderRow(bulkLines, "BOLT")));
    }

    [Fact]
    public async Task An_archived_part_is_shown_disabled_and_is_left_out_of_the_order()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var supplier = await SeedSupplierAsync(factory);
        var live = await SeedProductAsync(factory, "LIVE");
        var old = await SeedProductAsync(factory, "OLD");
        var project = await SeedProjectAsync(factory, "Robot", 1, (old.Id, 2), (live.Id, 2));
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IProductService>().SetActiveAsync(old.Id, false)).Succeeded));
        var url = $"/Projects/{project.Id}/Order";

        var html = await client.GetStringAsync(url);
        var oldRow = OrderRow(html, "OLD");
        Assert.Contains("Archived", oldRow);
        Assert.Contains("Not offered", oldRow);
        Assert.DoesNotContain("<select", oldRow);

        // The archived row is posted as a skip, so the part after it is still read.
        var fields = Regex.Matches(html, "name=\"(Input\\.Lines\\[\\d+\\]\\.(?:LineId|Action))\"[^>]*value=\"([^\"]*)\"")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.Contains(fields, f => f.Item1.EndsWith(".Action") && f.Item2 == "Skip");

        var response = await WebForms.PostFormAsync(client, url, OrderFields("New", null, supplier.Id,
            new Decision(LineOf(project, old.Id), "Skip"), new Decision(LineOf(project, live.Id), "Add", "2")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var link = Regex.Match(await client.GetStringAsync(response.RedirectPath()), "href=\"/PurchaseOrders/(\\d+)\"").Groups[1].Value;
        Assert.Equal(live.Id, (await GetOrderAsync(factory, int.Parse(link))).Lines.Single().ProductId);
    }

    [Fact]
    public async Task The_order_button_shows_only_when_something_is_short_and_only_to_people_who_can_edit()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        using var viewer = factory.CreateClientAs("Viewer");
        var bolt = await SeedProductAsync(factory, "BOLT", onHand: 1);
        var project = await SeedProjectAsync(factory, "Robot", 1, (bolt.Id, 5));
        var url = $"/Projects/{project.Id}";

        Assert.Contains($"href=\"{url}/Order\"", await staff.GetStringAsync(url));
        Assert.DoesNotContain("/Order\"", await viewer.GetStringAsync(url));

        // Stock arrives: nothing short, no button, and the page itself says so.
        var userId = await AdminIdAsync(factory);
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IStockService>()
            .PostAsync([new StockMovement(bolt.Id, StockTransactionType.Receive, 10)], new StockContext(userId))).Succeeded));

        Assert.DoesNotContain("Order shortages", await staff.GetStringAsync(url));
        Assert.Contains("Nothing to order", await staff.GetStringAsync($"{url}/Order"));
    }

    [Fact]
    public async Task The_order_button_updates_with_the_bom_over_htmx()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var bolt = await SeedProductAsync(factory, "BOLT");
        var project = await SeedProjectAsync(factory, "Robot", 1);
        var url = $"/Projects/{project.Id}";
        Assert.DoesNotContain("Order shortages", await client.GetStringAsync(url));

        var response = await HtmxPostAsync(client, $"{url}?handler=AddLine", [("productId", bolt.Id.ToString()), ("quantity", "2")], url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Matches("id=\"project-actions\"[^>]*hx-swap-oob=\"true\"", html);
        Assert.Contains("Order shortages", html);
    }

    // ---- build -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Building_issues_every_part_from_stock_records_the_build_and_updates_the_page()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var bolt = await SeedProductAsync(factory, "BOLT", onHand: 20);
        var nut = await SeedProductAsync(factory, "NUT", onHand: 9);
        var project = await SeedProjectAsync(factory, "Robot", 3, (bolt.Id, 4), (nut.Id, 2));
        var url = $"/Projects/{project.Id}";
        Assert.Equal("Ready to build", StateChip(await client.GetStringAsync(url)));

        var response = await WebForms.PostFormAsync(client, $"{url}?handler=Build", [("units", "2"), ("note", "First batch")], url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(url, response.RedirectPath());
        Assert.Equal(12, await OnHandAsync(factory, bolt.Id));
        Assert.Equal(5, await OnHandAsync(factory, nut.Id));

        var detail = await client.GetStringAsync(url);
        Assert.Contains("Built 2 units. Issued 12 parts from stock.", detail);
        Assert.Contains("First batch", detail);

        var reference = ProjectService.ReferenceFor("Robot", (await GetProjectAsync(factory, project.Id)).BuildCount);
        var history = await client.GetStringAsync($"/Stock/Movements?search={Uri.EscapeDataString(reference)}");
        Assert.Contains("BOLT", history);
        Assert.Contains("NUT", history);
        Assert.Contains("Issue", history);
        Assert.Contains("/Stock/Movements?search=", detail);

        var product = await client.GetStringAsync($"/Products/{bolt.Id}");
        Assert.Contains(reference, product);
    }

    [Fact]
    public async Task A_build_with_a_short_part_posts_nothing_and_names_the_part()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var bolt = await SeedProductAsync(factory, "BOLT", onHand: 20);
        var nut = await SeedProductAsync(factory, "NUT", onHand: 1);
        var project = await SeedProjectAsync(factory, "Robot", 3, (bolt.Id, 4), (nut.Id, 2));
        var url = $"/Projects/{project.Id}";

        var response = await HtmxPostAsync(client, $"{url}?handler=Build", [("units", "2")], url);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("NUT", body);
        Assert.Equal(20, await OnHandAsync(factory, bolt.Id));
        Assert.Equal(1, await OnHandAsync(factory, nut.Id));
        Assert.Equal(0, (await GetProjectAsync(factory, project.Id)).BuildCount);
    }

    [Fact]
    public async Task Build_works_as_a_plain_page_and_a_dialog_and_previews_what_it_would_issue()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var bolt = await SeedProductAsync(factory, "BOLT", onHand: 4);
        var project = await SeedProjectAsync(factory, "Robot", 5, (bolt.Id, 3));
        var url = $"/Projects/{project.Id}";

        var inline = await client.GetStringAsync($"{url}?handler=Build");
        Assert.Contains("<html", inline);
        Assert.Contains("id=\"inline-dialog\"", inline);
        Assert.Contains("name=\"units\"", inline);

        var dialog = await (await client.SendAsync(Htmx($"{url}?handler=Build"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", dialog);
        Assert.Contains("Build and issue parts", dialog);

        var preview = await (await client.SendAsync(Htmx($"{url}?handler=BuildPreview&units=1"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", preview);
        Assert.Contains("Issue 3 parts from stock for 1 unit?", preview);
        Assert.DoesNotContain("Short 1", preview);

        var tooMany = await (await client.SendAsync(Htmx($"{url}?handler=BuildPreview&units=2"))).Content.ReadAsStringAsync();
        Assert.Contains("Short 2", tooMany);
        Assert.Contains("A build issues nothing until every part is covered", tooMany);

        // A plain post that fails shows the form again with the error.
        var failed = await WebForms.PostFormAsync(client, $"{url}?handler=Build", [("units", "2")], url);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Contains("BOLT", await failed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Build_is_the_primary_action_when_ready_and_a_menu_item_otherwise_and_gone_once_built()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var bolt = await SeedProductAsync(factory, "BOLT", onHand: 5);
        var ready = await SeedProjectAsync(factory, "Ready one", 1, (bolt.Id, 2));
        var short1 = await SeedProjectAsync(factory, "Short one", 1, (bolt.Id, 50));

        var readyHtml = await client.GetStringAsync($"/Projects/{ready.Id}");
        Assert.Matches("<a role=\"button\" href=\"[^\"]*handler=Build\"", readyHtml);

        var shortHtml = await client.GetStringAsync($"/Projects/{short1.Id}");
        Assert.DoesNotContain("<a role=\"button\" href=\"/Projects/" + short1.Id + "?handler=Build\"", shortHtml);
        Assert.Contains("class=\"menu-item\" href=\"/Projects/" + short1.Id + "?handler=Build\"", shortHtml);

        var built = await WebForms.PostFormAsync(client, $"/Projects/{ready.Id}?handler=Build", [("units", "1")], $"/Projects/{ready.Id}");
        Assert.Equal(HttpStatusCode.Redirect, built.StatusCode);
        var after = await client.GetStringAsync($"/Projects/{ready.Id}");
        Assert.Equal("Built", StateChip(after));
        Assert.DoesNotContain("handler=Build\"", after);
    }

    // ---- roles -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Viewers_cannot_open_or_post_order_or_build_and_anonymous_visitors_are_sent_to_sign_in()
    {
        using var factory = new AppFactory();
        using var viewer = factory.CreateClientAs("Viewer");
        using var anonymous = factory.CreateAnonymousClient();
        var bolt = await SeedProductAsync(factory, "BOLT", onHand: 10);
        var project = await SeedProjectAsync(factory, "Robot", 1, (bolt.Id, 2));
        var url = $"/Projects/{project.Id}";

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"{url}/Order")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"{url}?handler=Build")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"{url}?handler=BuildPreview&units=1")).StatusCode);

        var post = await WebForms.PostFormAsync(viewer, $"{url}?handler=Build", [("units", "1")], "/Projects");
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(10, await OnHandAsync(factory, bolt.Id));

        var order = await anonymous.GetAsync($"{url}/Order");
        Assert.Equal(HttpStatusCode.Redirect, order.StatusCode);
        Assert.Contains("/Account/Login", order.RedirectPath());
        var build = await anonymous.GetAsync($"{url}?handler=Build");
        Assert.Equal(HttpStatusCode.Redirect, build.StatusCode);
    }
}
