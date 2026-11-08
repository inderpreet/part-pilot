using System.Net;
using System.Text.RegularExpressions;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-10-3: the projects list, project page with its BOM status table, BOM editing, New part, project number, product-page links.</summary>
public partial class ProjectPagesTests
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

    /// <summary>The text of the header state chip, e.g. "Short".</summary>
    private static string StateChip(string html)
    {
        var match = Regex.Match(html, "id=\"project-state\"[^>]*>\\s*<span class=\"badge [^\"]*\">([^<]+)</span>");
        Assert.True(match.Success, "No state chip on the page.");
        return match.Groups[1].Value;
    }

    /// <summary>The row of the BOM table for <paramref name="sku"/>.</summary>
    private static string Row(string html, string sku)
    {
        var rows = Regex.Matches(html, "<tr data-line-row>.*?</tr>", RegexOptions.Singleline);
        var row = rows.Select(m => m.Value).FirstOrDefault(r => r.Contains($"<strong>{sku}</strong>"));
        Assert.NotNull(row);
        return row;
    }

    private static async Task<string> AdminIdAsync(AppFactory factory) => await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);

    private static async Task<ProductDetail> SeedProductAsync(AppFactory factory, string sku, string name = "", int onHand = 0, long costCents = 0)
    {
        ProductDetail? created = null;
        var userId = await AdminIdAsync(factory);
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<IProductService>().CreateAsync(new ProductInput(sku, string.IsNullOrEmpty(name) ? "Part " + sku : name, null, null, null, null, null, 0, 0, costCents));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            created = result.Value;
            if (onHand > 0)
            {
                var posted = await sp.GetRequiredService<IStockService>().PostAsync([new StockMovement(created.Id, StockTransactionType.Receive, onHand)], new StockContext(userId));
                Assert.True(posted.Succeeded, string.Join("; ", posted.Errors.Select(e => e.Message)));
            }
        });
        return created!;
    }

    private static async Task<SupplierDetail> SeedSupplierAsync(AppFactory factory, string name = "Acme Tools")
    {
        SupplierDetail? created = null;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<ISupplierService>().CreateAsync(new SupplierInput(name, null, null, null, null, null, null));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            created = result.Value;
        });
        return created!;
    }

    private static async Task<ProjectDetail> SeedProjectAsync(AppFactory factory, string name = "Robot", int target = 1, params (int ProductId, int Quantity)[] parts)
    {
        ProjectDetail? created = null;
        var userId = await AdminIdAsync(factory);
        await factory.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<IProjectService>();
            var actor = new ProjectActor(userId, IsAdmin: true);
            var result = await service.CreateAsync(new ProjectInput(name, null, null, target, null), actor);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            foreach (var (productId, quantity) in parts)
            {
                var added = await service.AddLineAsync(result.Value.Id, new BomLineInput(productId, quantity, null), actor);
                Assert.True(added.Succeeded, string.Join("; ", added.Errors.Select(e => e.Message)));
            }

            created = (await service.GetAsync(result.Value.Id))!;
        });
        return created!;
    }

    private static async Task<PurchaseOrderDetail> SeedOrderAsync(AppFactory factory, int supplierId, int productId, int quantity, bool submit)
    {
        PurchaseOrderDetail? order = null;
        var userId = await AdminIdAsync(factory);
        await factory.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<IPurchaseOrderService>();
            var actor = new PurchaseOrderActor(userId, IsAdmin: true);
            var created = await service.CreateDraftAsync(supplierId, actor, [new PurchaseOrderLineInput(productId, quantity)]);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Message)));
            order = created.Value;
            if (submit)
            {
                var submitted = await service.SubmitAsync(order.Id, actor);
                Assert.True(submitted.Succeeded, string.Join("; ", submitted.Errors.Select(e => e.Message)));
                order = submitted.Value;
            }
        });
        return order!;
    }

    private static async Task<ProjectDetail> GetProjectAsync(AppFactory factory, int id)
    {
        ProjectDetail? project = null;
        await factory.WithScopeAsync(async sp => project = await sp.GetRequiredService<IProjectService>().GetAsync(id));
        return project!;
    }

    private static async Task<int> ProductCountAsync(AppFactory factory)
    {
        var count = 0;
        await factory.WithScopeAsync(async sp => count = (await sp.GetRequiredService<IProductService>().SearchAsync(new ProductListQuery { Status = ProductStatus.All })).TotalCount);
        return count;
    }

    // ---- list ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_is_a_full_page_normally_and_just_the_results_for_htmx_with_search_and_tabs()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var robot = await SeedProjectAsync(factory, "Robot arm");
        var drone = await SeedProjectAsync(factory, "Drone");
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IProjectService>().SetActiveAsync(drone.Id, false)).Succeeded));

        var page = await client.GetStringAsync("/Projects");
        Assert.Contains("<html", page);
        Assert.Contains("Robot arm", page);
        Assert.Contains(robot.Number, page);
        Assert.DoesNotContain("Drone", page);
        Assert.Contains("aria-label=\"Project status\"", page);

        var partial = await (await client.SendAsync(Htmx("/Projects?search=robot"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", partial);
        Assert.Contains("Robot arm", partial);

        var byNumber = await client.GetStringAsync($"/Projects?search={Uri.EscapeDataString(robot.Number)}");
        Assert.Contains("Robot arm", byNumber);

        var none = await client.GetStringAsync("/Projects?search=zzz-nothing");
        Assert.Contains("No projects match", none);

        var archived = await client.GetStringAsync("/Projects?tab=Archived");
        Assert.Contains("Drone", archived);
        Assert.DoesNotContain("Robot arm", archived);

        var all = await client.GetStringAsync("/Projects?tab=All");
        Assert.Contains("Drone", all);
        Assert.Contains("Robot arm", all);
    }

    [Fact]
    public async Task The_list_shows_number_name_parts_target_state_and_cost_and_an_empty_state_invites_creating()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");

        var empty = await staff.GetStringAsync("/Projects");
        Assert.Contains("No projects yet", empty);
        Assert.Contains("href=\"/Projects/Edit\"", empty);

        var part = await SeedProductAsync(factory, "R-1", costCents: 250);
        var project = await SeedProjectAsync(factory, "Robot", 4, (part.Id, 2));

        var list = await staff.GetStringAsync("/Projects");
        Assert.Contains(project.Number, list);
        Assert.Contains("Robot", list);
        Assert.Contains("badge-out", list); // nothing in stock: Short
        Assert.Contains("5.00", list); // 2 x 2.50 per unit
    }

    // ---- create and edit -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_blank_number_generates_the_next_one_and_a_typed_number_is_kept()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");

        var generated = await WebForms.PostFormAsync(client, "/Projects/Edit", [("Input.Name", "Robot"), ("Input.TargetUnits", "2"), ("Input.Number", "")]);
        Assert.Equal(HttpStatusCode.Redirect, generated.StatusCode);
        var detail = await client.GetStringAsync(generated.RedirectPath());
        Assert.Matches("PRJ-\\d{4}-0001", detail);
        Assert.Contains("Target 2 units", detail);

        var typed = await WebForms.PostFormAsync(client, "/Projects/Edit", [("Input.Name", "Drone"), ("Input.TargetUnits", "1"), ("Input.Number", "CUST-77")]);
        Assert.Equal(HttpStatusCode.Redirect, typed.StatusCode);
        Assert.Contains("CUST-77", await client.GetStringAsync(typed.RedirectPath()));

        // A typed number does not use the sequence: the next blank one is 0002.
        var next = await WebForms.PostFormAsync(client, "/Projects/Edit", [("Input.Name", "Rover"), ("Input.TargetUnits", "1")]);
        Assert.Matches("PRJ-\\d{4}-0002", await client.GetStringAsync(next.RedirectPath()));
    }

    [Fact]
    public async Task A_duplicate_number_or_name_and_a_missing_target_show_field_errors_and_create_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        await SeedProjectAsync(factory, "Robot");
        var rover = await SeedProjectAsync(factory, "Rover");

        var duplicateName = await WebForms.PostFormAsync(client, "/Projects/Edit", [("Input.Name", "robot"), ("Input.TargetUnits", "1")]);
        Assert.Equal(HttpStatusCode.OK, duplicateName.StatusCode);
        var html = await duplicateName.Content.ReadAsStringAsync();
        Assert.Contains("aria-invalid=\"true\"", html);
        Assert.Contains("name=\"Input.Name\"", html);

        var noTarget = await WebForms.PostFormAsync(client, "/Projects/Edit", [("Input.Name", "Drone"), ("Input.TargetUnits", "")]);
        Assert.Equal(HttpStatusCode.OK, noTarget.StatusCode);
        Assert.Contains("Enter how many units", await noTarget.Content.ReadAsStringAsync());

        var zeroTarget = await WebForms.PostFormAsync(client, "/Projects/Edit", [("Input.Name", "Drone"), ("Input.TargetUnits", "0")]);
        Assert.Equal(HttpStatusCode.OK, zeroTarget.StatusCode);
        Assert.Contains("aria-invalid=\"true\"", await zeroTarget.Content.ReadAsStringAsync());

        var duplicateNumber = await WebForms.PostFormAsync(client, "/Projects/Edit", [("Input.Name", "Drone"), ("Input.TargetUnits", "1"), ("Input.Number", rover.Number)]);
        Assert.Equal(HttpStatusCode.OK, duplicateNumber.StatusCode);
        Assert.Contains("name=\"Input.Number\"", await duplicateNumber.Content.ReadAsStringAsync());

        var list = await client.GetStringAsync("/Projects?tab=All");
        Assert.DoesNotContain("Drone", list);
    }

    [Fact]
    public async Task The_edit_form_shows_the_number_read_only_and_a_posted_change_to_it_is_ignored()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var project = await SeedProjectAsync(factory, "Robot", 3);
        var url = $"/Projects/Edit/{project.Id}";

        var form = await client.GetStringAsync(url);
        Assert.Contains(project.Number, form);
        Assert.DoesNotContain("name=\"Input.Number\"", form);
        Assert.Contains("Only an Admin can change the project number", form);

        var save = await WebForms.PostFormAsync(client, url,
            [("Input.Name", "Robot v2"), ("Input.TargetUnits", "5"), ("Input.Number", "HACKED"), ("Input.RowVersion", await RowVersionOfAsync(client, url))]);
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        var saved = await GetProjectAsync(factory, project.Id);
        Assert.Equal("Robot v2", saved.Name);
        Assert.Equal(5, saved.TargetUnits);
        Assert.Equal(project.Number, saved.Number);
    }

    [Fact]
    public async Task A_stale_edit_reports_the_conflict()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/Edit/{project.Id}";
        var stale = await RowVersionOfAsync(client, url);

        Assert.Equal(HttpStatusCode.Redirect, (await WebForms.PostFormAsync(client, url, [("Input.Name", "First"), ("Input.TargetUnits", "1"), ("Input.RowVersion", stale)])).StatusCode);

        var second = await WebForms.PostFormAsync(client, url, [("Input.Name", "Second"), ("Input.TargetUnits", "1"), ("Input.RowVersion", stale)], url);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("Someone else saved this project", await second.Content.ReadAsStringAsync());
        Assert.Equal("First", (await GetProjectAsync(factory, project.Id)).Name);
    }

    // ---- BOM editing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_catalog_part_is_added_edited_and_removed_through_htmx_partials()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var part = await SeedProductAsync(factory, "W-1", "Widget", onHand: 10);
        var project = await SeedProjectAsync(factory, "Robot", 2);
        var url = $"/Projects/{project.Id}";

        var added = await HtmxPostAsync(client, $"{url}?handler=AddLine", [("productId", part.Id.ToString()), ("q", "W-1"), ("quantity", "3"), ("note", "red one")], url);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var partial = await added.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", partial);
        Assert.Contains("<strong>W-1</strong>", partial);
        Assert.Contains("red one", partial);
        Assert.Contains("hx-swap-oob=\"true\"", partial); // header chip, sub line and totals ride along
        Assert.Contains("id=\"project-state\"", partial);

        var line = (await GetProjectAsync(factory, project.Id)).Lines.Single();
        Assert.Equal(3, line.QuantityPerUnit);

        // Edit the quantity: 7 x 2 = 14 needed with 10 on hand, so the project is short.
        var saved = await HtmxPostAsync(client, $"{url}?handler=SaveLine", [("lineId", line.Id.ToString()), ("quantity", "7"), ("note", "red one")], url);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(7, (await GetProjectAsync(factory, project.Id)).Lines.Single().QuantityPerUnit);
        Assert.Equal("Short", StateChip(await saved.Content.ReadAsStringAsync()));

        var removed = await HtmxPostAsync(client, $"{url}?handler=RemoveLine", [("lineId", line.Id.ToString())], url);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty((await GetProjectAsync(factory, project.Id)).Lines);
        Assert.Contains("No parts yet", await removed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Adding_the_same_part_twice_is_refused_with_a_message_naming_it()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var part = await SeedProductAsync(factory, "W-1", "Widget");
        var project = await SeedProjectAsync(factory, "Robot", 1, (part.Id, 1));
        var url = $"/Projects/{project.Id}";

        var again = await HtmxPostAsync(client, $"{url}?handler=AddLine", [("productId", part.Id.ToString()), ("q", "W-1"), ("quantity", "2")], url);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        var html = await again.Content.ReadAsStringAsync();
        Assert.Contains("W-1", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Single((await GetProjectAsync(factory, project.Id)).Lines);
    }

    [Fact]
    public async Task Bad_input_on_the_add_row_answers_422_without_changing_the_bom()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var part = await SeedProductAsync(factory, "W-1");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/{project.Id}";

        var noPart = await HtmxPostAsync(client, $"{url}?handler=AddLine", [("quantity", "1")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noPart.StatusCode);
        Assert.Contains("Choose a part", await noPart.Content.ReadAsStringAsync());

        var badQuantity = await HtmxPostAsync(client, $"{url}?handler=AddLine", [("productId", part.Id.ToString()), ("quantity", "0")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badQuantity.StatusCode);

        var notNumber = await HtmxPostAsync(client, $"{url}?handler=AddLine", [("productId", part.Id.ToString()), ("quantity", "abc")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notNumber.StatusCode);

        Assert.Empty((await GetProjectAsync(factory, project.Id)).Lines);
    }

    [Fact]
    public async Task Without_htmx_the_same_forms_post_and_redirect_back_to_the_project()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var part = await SeedProductAsync(factory, "W-1");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/{project.Id}";

        var add = await WebForms.PostFormAsync(client, $"{url}?handler=AddLine", [("productId", part.Id.ToString()), ("quantity", "2")], url);
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        Assert.Equal(url, add.RedirectPath());
        var line = (await GetProjectAsync(factory, project.Id)).Lines.Single();
        Assert.Equal(2, line.QuantityPerUnit);

        var save = await WebForms.PostFormAsync(client, $"{url}?handler=SaveLine", [("lineId", line.Id.ToString()), ("quantity", "4")], url);
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Equal(4, (await GetProjectAsync(factory, project.Id)).Lines.Single().QuantityPerUnit);

        var remove = await WebForms.PostFormAsync(client, $"{url}?handler=RemoveLine", [("lineId", line.Id.ToString())], url);
        Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);
        Assert.Empty((await GetProjectAsync(factory, project.Id)).Lines);

        // A failure rides TempData to the page.
        var failed = await WebForms.PostFormAsync(client, $"{url}?handler=AddLine", [("quantity", "2")], url);
        Assert.Equal(HttpStatusCode.Redirect, failed.StatusCode);
        Assert.Contains("Choose a part", await client.GetStringAsync(url));
    }

    // ---- New part --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_part_creates_a_catalog_product_and_a_line_and_closes_the_dialog()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/{project.Id}";

        var dialog = await (await client.SendAsync(Htmx($"{url}?handler=NewPart"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", dialog);
        Assert.Contains("New part for Robot", dialog);
        Assert.Contains("name=\"NewPart.Sku\"", dialog);
        Assert.Contains("value=\"each\"", dialog);

        var before = await ProductCountAsync(factory);
        var response = await HtmxPostAsync(client, $"{url}?handler=NewPart",
            [("NewPart.Sku", "NEW-1"), ("NewPart.Name", "Mystery bolt"), ("NewPart.Unit", "each"), ("NewPart.UnitCost", "1.25"), ("NewPart.QuantityPerUnit", "4")], url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("closeModal", string.Join(";", response.Headers.GetValues("HX-Trigger")));
        Assert.Equal("#bom", response.Headers.GetValues("HX-Retarget").Single());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<strong>NEW-1</strong>", html);
        Assert.Equal(before + 1, await ProductCountAsync(factory));

        var line = (await GetProjectAsync(factory, project.Id)).Lines.Single();
        Assert.Equal("NEW-1", line.Sku);
        Assert.Equal(4, line.QuantityPerUnit);
        Assert.Equal(125, line.UnitCostCents);
    }

    [Fact]
    public async Task A_new_part_with_a_taken_sku_shows_the_field_error_in_place_and_creates_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        await SeedProductAsync(factory, "NEW-1");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/{project.Id}";
        var before = await ProductCountAsync(factory);

        var response = await HtmxPostAsync(client, $"{url}?handler=NewPart",
            [("NewPart.Sku", "new-1"), ("NewPart.Name", "Another"), ("NewPart.Unit", "each"), ("NewPart.UnitCost", ""), ("NewPart.QuantityPerUnit", "1")], url);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("aria-invalid=\"true\"", html);
        Assert.Contains("name=\"NewPart.Sku\"", html);
        Assert.False(response.Headers.Contains("HX-Retarget"));
        Assert.Equal(before, await ProductCountAsync(factory));
        Assert.Empty((await GetProjectAsync(factory, project.Id)).Lines);
    }

    [Fact]
    public async Task A_new_part_with_a_bad_cost_or_quantity_is_refused_and_works_as_a_plain_page()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/{project.Id}";

        var badCost = await HtmxPostAsync(client, $"{url}?handler=NewPart",
            [("NewPart.Sku", "NEW-2"), ("NewPart.Name", "Thing"), ("NewPart.UnitCost", "1.234"), ("NewPart.QuantityPerUnit", "1")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCost.StatusCode);

        var badQuantity = await HtmxPostAsync(client, $"{url}?handler=NewPart",
            [("NewPart.Sku", "NEW-2"), ("NewPart.Name", "Thing"), ("NewPart.QuantityPerUnit", "zero")], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badQuantity.StatusCode);

        // Without htmx the form is a card on the project page, and a good post redirects back to it.
        var page = await client.GetStringAsync($"{url}?handler=NewPart");
        Assert.Contains("<html", page);
        Assert.Contains("id=\"inline-dialog\"", page);

        var created = await WebForms.PostFormAsync(client, $"{url}?handler=NewPart",
            [("NewPart.Sku", "NEW-2"), ("NewPart.Name", "Thing"), ("NewPart.Unit", ""), ("NewPart.QuantityPerUnit", "2")], url);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal(url, created.RedirectPath());
        Assert.Equal("NEW-2", (await GetProjectAsync(factory, project.Id)).Lines.Single().Sku);
    }

    // ---- the status table ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_status_table_shows_Ready_Covered_and_Short_and_the_chip_is_the_worst_line()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var supplier = await SeedSupplierAsync(factory);
        var ready = await SeedProductAsync(factory, "A-READY", onHand: 5);
        var covered = await SeedProductAsync(factory, "B-COVERED");
        var shortPart = await SeedProductAsync(factory, "C-SHORT");
        await SeedOrderAsync(factory, supplier.Id, covered.Id, 10, submit: true);
        var project = await SeedProjectAsync(factory, "Robot", 5, (ready.Id, 1), (covered.Id, 2), (shortPart.Id, 1));

        var html = await client.GetStringAsync($"/Projects/{project.Id}");

        Assert.Contains("badge-ok\">Ready", Row(html, "A-READY"));
        Assert.Contains("badge-partial\">Covered", Row(html, "B-COVERED"));
        Assert.Contains("badge-out\">Short", Row(html, "C-SHORT"));
        Assert.Equal("Short", StateChip(html));
        Assert.Contains("1 short", html);
        Assert.Contains("1 on order", html);

        // Numbers side by side: needed 10, in stock 0, on order 10 for the covered part.
        var row = Regex.Replace(Row(html, "B-COVERED"), "<[^>]+>", " ");
        Assert.Matches("\\b10\\b[\\s\\S]*\\b0\\b[\\s\\S]*\\b10\\b", row);
    }

    [Fact]
    public async Task The_table_shows_in_stock_on_order_and_in_draft_side_by_side_with_draft_numbers_as_links()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var other = await SeedSupplierAsync(factory, "Other Supplies");
        var inDraft = await SeedProductAsync(factory, "D-1", onHand: 8);
        var none = await SeedProductAsync(factory, "N-1", onHand: 8);
        var draft = await SeedOrderAsync(factory, other.Id, inDraft.Id, 25, submit: false);
        var project = await SeedProjectAsync(factory, "Robot", 1, (inDraft.Id, 1), (none.Id, 1));

        var html = await client.GetStringAsync($"/Projects/{project.Id}");

        // In a draft made for another supplier, and the line is Ready: the draft is still shown.
        var draftRow = Row(html, "D-1");
        Assert.Contains($"href=\"/PurchaseOrders/{draft.Id}\"", draftRow);
        Assert.Contains(draft.Number, draftRow);
        Assert.Contains("<strong>25</strong>", draftRow);
        Assert.Contains("badge-ok\">Ready", draftRow);

        var noneRow = Row(html, "N-1");
        Assert.DoesNotContain("/PurchaseOrders/", noneRow);
        Assert.Contains("In PO draft", html);
    }

    [Fact]
    public async Task A_line_covered_only_by_a_draft_is_In_draft_and_the_chip_follows_Short_then_In_draft_then_Covered_then_Ready()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var supplier = await SeedSupplierAsync(factory);
        var ready = await SeedProductAsync(factory, "A-READY", onHand: 5);
        var covered = await SeedProductAsync(factory, "B-COVERED");
        var drafted = await SeedProductAsync(factory, "C-DRAFT");
        var missing = await SeedProductAsync(factory, "D-MISSING");
        await SeedOrderAsync(factory, supplier.Id, covered.Id, 3, submit: true);
        await SeedOrderAsync(factory, supplier.Id, drafted.Id, 3, submit: false);

        var onlyReady = await SeedProjectAsync(factory, "P1", 1, (ready.Id, 1));
        var withCovered = await SeedProjectAsync(factory, "P2", 1, (ready.Id, 1), (covered.Id, 3));
        var withDraft = await SeedProjectAsync(factory, "P3", 1, (ready.Id, 1), (covered.Id, 3), (drafted.Id, 3));
        var withShort = await SeedProjectAsync(factory, "P4", 1, (ready.Id, 1), (covered.Id, 3), (drafted.Id, 3), (missing.Id, 1));

        Assert.Equal("Ready to build", StateChip(await client.GetStringAsync($"/Projects/{onlyReady.Id}")));
        Assert.Equal("Covered by orders", StateChip(await client.GetStringAsync($"/Projects/{withCovered.Id}")));

        var draftPage = await client.GetStringAsync($"/Projects/{withDraft.Id}");
        Assert.Equal("In draft PO", StateChip(draftPage));
        Assert.Contains("badge-draft\">In draft", Row(draftPage, "C-DRAFT"));

        Assert.Equal("Short", StateChip(await client.GetStringAsync($"/Projects/{withShort.Id}")));
    }

    [Fact]
    public async Task An_empty_project_says_so_and_an_archived_part_is_flagged()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        var part = await SeedProductAsync(factory, "OLD-1", onHand: 1);
        var empty = await SeedProjectAsync(factory, "Empty");
        var withPart = await SeedProjectAsync(factory, "Robot", 1, (part.Id, 1));
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IProductService>().SetActiveAsync(part.Id, false)).Succeeded));

        var emptyHtml = await staff.GetStringAsync($"/Projects/{empty.Id}");
        Assert.Contains("No parts yet", emptyHtml);
        Assert.Equal("No parts", StateChip(emptyHtml));

        var html = await staff.GetStringAsync($"/Projects/{withPart.Id}");
        Assert.Contains("Archived", Row(html, "OLD-1"));
    }

    // ---- roles -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Viewer_reads_everything_including_costs_but_sees_no_controls_and_cannot_post()
    {
        using var factory = new AppFactory();
        using var viewer = factory.CreateClientAs("Viewer");
        var part = await SeedProductAsync(factory, "W-1", "Widget", onHand: 3, costCents: 250);
        var project = await SeedProjectAsync(factory, "Robot", 3, (part.Id, 2));
        var url = $"/Projects/{project.Id}";

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/Projects")).StatusCode);
        var list = await viewer.GetStringAsync("/Projects");
        Assert.DoesNotContain("New project", list);

        var detail = await viewer.GetStringAsync(url);
        Assert.Contains("5.00", detail); // parts cost per unit: 2 x 2.50
        Assert.Contains("15.00", detail); // for the 3 remaining units
        foreach (var control in new[] { "Add part", "Remove", "Create a new part", "Archive", "Delete project", "Change number", ">Edit<" })
        {
            Assert.DoesNotContain(control, detail);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Projects/Edit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/Projects/Edit/{project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"{url}?handler=NewPart")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"{url}?handler=ChangeNumber")).StatusCode);

        var line = (await GetProjectAsync(factory, project.Id)).Lines.Single();
        var posts = new (string Handler, (string, string)[] Fields)[]
        {
            ("AddLine", [("productId", part.Id.ToString()), ("quantity", "1")]),
            ("SaveLine", [("lineId", line.Id.ToString()), ("quantity", "9")]),
            ("RemoveLine", [("lineId", line.Id.ToString())]),
            ("NewPart", [("NewPart.Sku", "X-1"), ("NewPart.Name", "X"), ("NewPart.QuantityPerUnit", "1")]),
            ("ChangeNumber", [("rowVersion", project.RowVersion.ToString()), ("newNumber", "HACK-1")]),
            ("Archive", [("active", "false"), ("rowVersion", project.RowVersion.ToString())]),
            ("Delete", [("rowVersion", project.RowVersion.ToString())]),
        };
        foreach (var (handler, fields) in posts)
        {
            var response = await WebForms.PostFormAsync(viewer, $"{url}?handler={handler}", fields, url);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var save = await WebForms.PostFormAsync(viewer, "/Projects/Edit", [("Input.Name", "Nope"), ("Input.TargetUnits", "1")], url);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);

        var unchanged = await GetProjectAsync(factory, project.Id);
        Assert.Equal(project.Number, unchanged.Number);
        Assert.True(unchanged.IsActive);
        Assert.Equal(2, unchanged.Lines.Single().QuantityPerUnit);
    }

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_sign_in()
    {
        using var factory = new AppFactory();
        using var anonymous = factory.CreateAnonymousClient();

        foreach (var url in new[] { "/Projects", "/Projects/1", "/Projects/Edit", "/Projects/Edit/1", "/Projects/1?handler=NewPart", "/Projects/1?handler=ChangeNumber" })
        {
            var response = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/Login", response.RedirectPath());
        }
    }

    [Fact]
    public async Task The_main_nav_links_to_projects()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");

        var html = await client.GetStringAsync("/Projects");

        Assert.Matches("href=\"/Projects\" aria-current=\"page\"><svg[^>]*>.*?</svg><span>Projects</span></a>", html);
        Assert.DoesNotContain("aria-disabled=\"true\" title=\"Coming soon\">Projects</span>", html);
    }

    [Fact]
    public async Task Unknown_projects_are_404()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Projects/99999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Projects/Edit/99999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Projects/99999?handler=NewPart")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Projects/99999?handler=ChangeNumber")).StatusCode);
    }

    // ---- project number --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_Admin_changes_the_number_through_the_dialog_and_the_old_number_no_longer_finds_the_project()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/{project.Id}";

        Assert.Contains("Change number", await admin.GetStringAsync(url));

        var dialog = await (await admin.SendAsync(Htmx($"{url}?handler=ChangeNumber"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", dialog);
        Assert.Contains(project.Number, dialog);
        Assert.Contains("Other systems may refer to the current project number. Purchase orders are not affected.", dialog);

        var changed = await HtmxPostAsync(admin, $"{url}?handler=ChangeNumber", [("rowVersion", project.RowVersion.ToString()), ("newNumber", "ACME-2026-9")], url);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(url, changed.Headers.GetValues("HX-Redirect").Single());

        var page = await admin.GetStringAsync(url);
        Assert.Contains("ACME-2026-9", page);
        Assert.Contains($"Project number changed from {project.Number} to ACME-2026-9.", page);

        Assert.Contains("Robot", await admin.GetStringAsync("/Projects?search=ACME-2026-9"));
        Assert.Contains("No projects match", await admin.GetStringAsync($"/Projects?search={Uri.EscapeDataString(project.Number)}"));
    }

    [Fact]
    public async Task A_duplicate_or_too_long_new_number_shows_the_field_error_in_the_dialog()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        var first = await SeedProjectAsync(factory, "Robot");
        var second = await SeedProjectAsync(factory, "Drone");
        var url = $"/Projects/{second.Id}";

        var duplicate = await HtmxPostAsync(admin, $"{url}?handler=ChangeNumber", [("rowVersion", second.RowVersion.ToString()), ("newNumber", first.Number)], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        var html = await duplicate.Content.ReadAsStringAsync();
        Assert.Contains("aria-invalid=\"true\"", html);
        Assert.Contains("name=\"newNumber\"", html);

        var tooLong = await HtmxPostAsync(admin, $"{url}?handler=ChangeNumber", [("rowVersion", second.RowVersion.ToString()), ("newNumber", new string('X', 31))], url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);

        Assert.Equal(second.Number, (await GetProjectAsync(factory, second.Id)).Number);
    }

    [Fact]
    public async Task Staff_get_no_menu_item_and_a_403_for_the_number_change()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/{project.Id}";

        Assert.DoesNotContain("Change number", await staff.GetStringAsync(url));
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"{url}?handler=ChangeNumber")).StatusCode);
        var post = await WebForms.PostFormAsync(staff, $"{url}?handler=ChangeNumber", [("rowVersion", project.RowVersion.ToString()), ("newNumber", "STAFF-1")], url);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(project.Number, (await GetProjectAsync(factory, project.Id)).Number);
    }

    // ---- archive and delete ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_project_is_archived_restored_and_deleted_with_a_flash_each_time()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var project = await SeedProjectAsync(factory, "Robot");
        var url = $"/Projects/{project.Id}";

        var archive = await WebForms.PostFormAsync(client, $"{url}?handler=Archive", [("active", "false"), ("rowVersion", project.RowVersion.ToString())], url);
        Assert.Equal(HttpStatusCode.Redirect, archive.StatusCode);
        var archivedPage = await client.GetStringAsync(url);
        Assert.Contains("archived.", archivedPage);
        Assert.Contains("Restore", archivedPage);
        Assert.Contains("Robot", await client.GetStringAsync("/Projects?tab=Archived"));

        var restore = await WebForms.PostFormAsync(client, $"{url}?handler=Archive", [("active", "true"), ("rowVersion", (await GetProjectAsync(factory, project.Id)).RowVersion.ToString())], url);
        Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
        Assert.True((await GetProjectAsync(factory, project.Id)).IsActive);

        var delete = await WebForms.PostFormAsync(client, $"{url}?handler=Delete", [("rowVersion", (await GetProjectAsync(factory, project.Id)).RowVersion.ToString())], url);
        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.Equal("/Projects", delete.RedirectPath());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_project_that_has_been_built_cannot_be_deleted_only_archived()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var part = await SeedProductAsync(factory, "W-1", onHand: 5);
        var project = await SeedProjectAsync(factory, "Robot", 2, (part.Id, 1));
        var userId = await AdminIdAsync(factory);
        await factory.WithScopeAsync(async sp =>
        {
            var built = await sp.GetRequiredService<IProjectService>().BuildAsync(project.Id, 1, null, new StockContext(userId));
            Assert.True(built.Succeeded, string.Join("; ", built.Errors.Select(e => e.Message)));
        });
        var url = $"/Projects/{project.Id}";

        var detail = await client.GetStringAsync(url);
        Assert.DoesNotContain("Delete project", detail);
        Assert.Contains("1 built so far", detail);

        var response = await WebForms.PostFormAsync(client, $"{url}?handler=Delete", [("rowVersion", project.RowVersion.ToString())], url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(url, response.RedirectPath());
        Assert.NotNull(await GetProjectAsync(factory, project.Id));
    }

    // ---- product page ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_product_page_lists_the_projects_that_use_it_and_hides_the_card_when_none_do()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var used = await SeedProductAsync(factory, "USED-1");
        var unused = await SeedProductAsync(factory, "FREE-1");
        var project = await SeedProjectAsync(factory, "Robot", 1, (used.Id, 3));

        var usedPage = await client.GetStringAsync($"/Products/{used.Id}");
        Assert.Contains("Used in projects", usedPage);
        Assert.Contains($"href=\"/Projects/{project.Id}\"", usedPage);
        Assert.Contains(project.Number, usedPage);
        Assert.Contains("3 each", usedPage);

        var freePage = await client.GetStringAsync($"/Products/{unused.Id}");
        Assert.DoesNotContain("Used in projects", freePage);
    }

    [Fact]
    public async Task The_product_page_shows_In_draft_POs_with_links_only_when_the_product_is_on_a_draft()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var supplier = await SeedSupplierAsync(factory);
        var drafted = await SeedProductAsync(factory, "D-1");
        var submitted = await SeedProductAsync(factory, "S-1");
        var draft = await SeedOrderAsync(factory, supplier.Id, drafted.Id, 12, submit: false);
        await SeedOrderAsync(factory, supplier.Id, submitted.Id, 4, submit: true);

        var draftPage = await client.GetStringAsync($"/Products/{drafted.Id}");
        Assert.Contains("In draft POs: 12", draftPage);
        Assert.Contains($"href=\"/PurchaseOrders/{draft.Id}\"", draftPage);
        Assert.Contains("Nothing is on order for this product.", draftPage);

        var submittedPage = await client.GetStringAsync($"/Products/{submitted.Id}");
        Assert.DoesNotContain("In draft POs", submittedPage);
    }
}
