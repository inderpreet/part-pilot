using System.Net;
using System.Text.RegularExpressions;
using InventoryTracker.Data.Entities;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-11-3: requisition list, editor and detail, per role.</summary>
public sealed partial class RequisitionPagesTests
{
    [GeneratedRegex("name=\"(?:Input\\.RowVersion|rowVersion)\" value=\"(\\d+)\"")]
    private static partial Regex RowVersionPattern();

    private static async Task<int> SeedPartAsync(AppFactory factory, string sku, int onHand = 0)
    {
        var id = 0;
        var adminId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        await factory.WithScopeAsync(async sp =>
        {
            var created = await sp.GetRequiredService<IProductService>().CreateAsync(new ProductInput(sku, "Part " + sku, null, null, null, null, null, 0, 0, 0));
            Assert.True(created.Succeeded);
            id = created.Value.Id;
            if (onHand > 0)
            {
                Assert.True((await sp.GetRequiredService<IStockService>().PostAsync([new StockMovement(id, StockTransactionType.Receive, onHand)], new StockContext(adminId))).Succeeded);
            }
        });
        return id;
    }

    /// <summary>A requisition owned by <paramref name="userName"/>.</summary>
    private static async Task<RequisitionDetail> SeedRequisitionAsync(AppFactory factory, string userName, int? productId = null, bool submit = false, string? reason = "Line stopped")
    {
        RequisitionDetail? detail = null;
        var actor = new RequisitionActor(await WebForms.UserIdAsync(factory, userName));
        await factory.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<IRequisitionService>();
            var created = await service.CreateDraftAsync(new RequisitionHeaderInput(null, reason), actor);
            Assert.True(created.Succeeded);
            if (productId is { } id)
            {
                Assert.True((await service.AddLineAsync(created.Value.Id, new RequisitionLineInput(id, 3), actor)).Succeeded);
            }

            detail = (await service.GetAsync(created.Value.Id, actor))!;
            if (submit)
            {
                detail = (await service.SubmitAsync(detail.Id, detail.RowVersion, actor)).Value;
            }
        });
        return detail!;
    }

    private static async Task<RequisitionStatus?> StatusOfAsync(AppFactory factory, int id)
    {
        RequisitionStatus? status = null;
        var admin = new RequisitionActor(await WebForms.UserIdAsync(factory, TestAuth.Users.Admin), CanSeeAll: true, IsAdmin: true);
        await factory.WithScopeAsync(async sp => status = (await sp.GetRequiredService<IRequisitionService>().GetAsync(id, admin))?.Status);
        return status;
    }

    private static async Task<string> RowVersionOfAsync(HttpClient client, string page)
        => RowVersionPattern().Match(await client.GetStringAsync(page)).Groups[1].Value;

    private static async Task<HttpResponseMessage> HtmxPostAsync(HttpClient client, string url, IEnumerable<(string, string)> fields, string tokenPage)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("RequestVerificationToken", await WebForms.TokenAsync(client, tokenPage));
        request.Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.SendAsync(request);
    }

    // ---- access ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Requisitions")]
    [InlineData("/Requisitions/Edit")]
    public async Task Anonymous_users_are_redirected_to_sign_in(string url)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Staff")]
    [InlineData("Viewer")]
    public async Task Every_role_can_open_the_list_and_the_new_form(string role)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs(role);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Requisitions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Requisitions/Edit")).StatusCode);
    }

    [Theory]
    [InlineData("NoRole")]
    [InlineData("Inactive")]
    public async Task Users_without_an_active_role_are_refused(string role)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs(role);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Requisitions")).StatusCode);
    }

    [Fact]
    public async Task Viewers_get_the_nav_entry()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");

        Assert.Contains("href=\"/Requisitions\"", await client.GetStringAsync("/"));
    }

    // ---- end to end --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_can_create_fill_submit_and_track_a_requisition()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "BOLT-1", onHand: 12);
        using var client = factory.CreateClientAs("Viewer");

        var created = await WebForms.PostFormAsync(client, "/Requisitions/Edit", [("Input.Reason", "Line 3 is down"), ("Input.NeededBy", "2026-12-01")]);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var editUrl = created.Headers.Location!.ToString();
        var id = int.Parse(editUrl.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);

        var added = await HtmxPostAsync(client, $"/Requisitions/Edit/{id}?handler=AddLine", [("productId", part.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("quantity", "4")], editUrl);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var fragment = await added.Content.ReadAsStringAsync();
        Assert.Contains("BOLT-1", fragment);
        Assert.Contains("hx-swap-oob", fragment);

        var detailUrl = $"/Requisitions/{id}";
        var detail = await client.GetStringAsync(detailUrl);
        Assert.Contains("REQ-", detail);
        Assert.Contains("Line 3 is down", detail);
        Assert.Contains(">Submit<", detail);

        var submitted = await WebForms.PostFormAsync(client, detailUrl + "?handler=Submit", [("rowVersion", await RowVersionOfAsync(client, detailUrl))], detailUrl);
        Assert.Equal(HttpStatusCode.Redirect, submitted.StatusCode);
        Assert.Equal(RequisitionStatus.Submitted, await StatusOfAsync(factory, id));

        var after = await client.GetStringAsync(detailUrl);
        Assert.Contains(">Withdraw<", after);
        Assert.DoesNotContain(">Submit<", after);
        Assert.Contains("Submitted by Test Viewer", after);

        var list = await client.GetStringAsync("/Requisitions");
        Assert.Contains($"/Requisitions/{id}", list);
    }

    [Fact]
    public async Task Withdrawing_makes_it_editable_again()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "A");
        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer, part, submit: true);
        using var client = factory.CreateClientAs("Viewer");
        var url = $"/Requisitions/{requisition.Id}";

        var response = await WebForms.PostFormAsync(client, url + "?handler=Withdraw", [("rowVersion", requisition.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))], url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(RequisitionStatus.Draft, await StatusOfAsync(factory, requisition.Id));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Requisitions/Edit/{requisition.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_draft_can_be_deleted()
    {
        using var factory = new AppFactory();
        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        using var client = factory.CreateClientAs("Viewer");
        var url = $"/Requisitions/{requisition.Id}";

        var response = await WebForms.PostFormAsync(client, url + "?handler=Delete", [("rowVersion", requisition.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))], url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(await StatusOfAsync(factory, requisition.Id));
    }

    [Fact]
    public async Task Submitting_without_a_reason_comes_back_with_the_message()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "A");
        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer, part, reason: null);
        using var client = factory.CreateClientAs("Viewer");
        var url = $"/Requisitions/{requisition.Id}";

        await WebForms.PostFormAsync(client, url + "?handler=Submit", [("rowVersion", requisition.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))], url);

        Assert.Equal(RequisitionStatus.Draft, await StatusOfAsync(factory, requisition.Id));
        Assert.Contains("Say why these parts are needed", await client.GetStringAsync(url));
    }

    // ---- ownership ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Requisitions/{0}")]
    [InlineData("/Requisitions/Edit/{0}")]
    [InlineData("/Requisitions/Edit/{0}?handler=Products&q=A")]
    public async Task A_viewer_gets_404_for_someone_elses_requisition(string template)
    {
        using var factory = new AppFactory();
        var theirs = await SeedRequisitionAsync(factory, TestAuth.Users.Staff);
        using var client = factory.CreateClientAs("Viewer");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, theirs.Id))).StatusCode);
    }

    [Theory]
    [InlineData("Submit")]
    [InlineData("Withdraw")]
    [InlineData("Delete")]
    public async Task A_viewer_cannot_post_to_someone_elses_requisition(string handler)
    {
        using var factory = new AppFactory();
        var theirs = await SeedRequisitionAsync(factory, TestAuth.Users.Staff);
        var own = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        using var client = factory.CreateClientAs("Viewer");

        var response = await WebForms.PostFormAsync(client, $"/Requisitions/{theirs.Id}?handler={handler}", [("rowVersion", theirs.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))], $"/Requisitions/{own.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RequisitionStatus.Draft, await StatusOfAsync(factory, theirs.Id));
    }

    [Fact]
    public async Task A_viewer_cannot_add_a_line_to_someone_elses_requisition()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "A");
        var theirs = await SeedRequisitionAsync(factory, TestAuth.Users.Staff);
        using var client = factory.CreateClientAs("Viewer");

        var response = await HtmxPostAsync(client, $"/Requisitions/Edit/{theirs.Id}?handler=AddLine", [("productId", part.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("quantity", "1")], "/Requisitions/Edit");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Staff_can_read_someone_elses_requisition_but_get_no_actions_and_no_editor()
    {
        using var factory = new AppFactory();
        var theirs = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        using var client = factory.CreateClientAs("Staff");

        var detail = await client.GetStringAsync($"/Requisitions/{theirs.Id}");
        Assert.DoesNotContain(">Submit<", detail);
        Assert.DoesNotContain("handler=Delete", detail);

        var edit = await client.GetAsync($"/Requisitions/Edit/{theirs.Id}");
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        Assert.Contains($"/Requisitions/{theirs.Id}", edit.Headers.Location!.ToString());
    }

    // ---- list --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_has_no_all_tab_and_only_sees_their_own_even_when_asking()
    {
        using var factory = new AppFactory();
        var own = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        var theirs = await SeedRequisitionAsync(factory, TestAuth.Users.Staff);
        using var client = factory.CreateClientAs("Viewer");

        var html = await client.GetStringAsync("/Requisitions?tab=All");

        Assert.DoesNotContain("Whose requisitions", html);
        Assert.Contains(own.Number, html);
        Assert.DoesNotContain(theirs.Number, html);
    }

    [Fact]
    public async Task Staff_see_mine_by_default_and_everyone_on_all()
    {
        using var factory = new AppFactory();
        var own = await SeedRequisitionAsync(factory, TestAuth.Users.Staff);
        var theirs = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        using var client = factory.CreateClientAs("Staff");

        var mine = await client.GetStringAsync("/Requisitions");
        Assert.Contains(own.Number, mine);
        Assert.DoesNotContain(theirs.Number, mine);

        var all = await client.GetStringAsync("/Requisitions?tab=All");
        Assert.Contains(theirs.Number, all);
        Assert.Contains("Test Viewer", all);
    }

    [Fact]
    public async Task An_htmx_list_request_gets_only_the_results_block()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");
        var request = new HttpRequestMessage(HttpMethod.Get, "/Requisitions");
        request.Headers.Add("HX-Request", "true");

        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<html", html);
        Assert.Contains("No requisitions yet", html);
    }

    // ---- editor ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_picker_shows_on_hand_and_on_order()
    {
        using var factory = new AppFactory();
        await SeedPartAsync(factory, "BOLT-1", onHand: 12);
        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        using var client = factory.CreateClientAs("Viewer");

        var html = await client.GetStringAsync($"/Requisitions/Edit/{requisition.Id}?handler=Products&q=BOLT");

        Assert.Contains("12 each on hand", html);
        Assert.Contains("0 on order", html);
    }

    [Fact]
    public async Task A_bad_quantity_answers_422_with_the_message()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "A");
        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        using var client = factory.CreateClientAs("Viewer");
        var url = $"/Requisitions/Edit/{requisition.Id}";

        var response = await HtmxPostAsync(client, url + "?handler=AddLine", [("productId", part.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("quantity", "0")], url);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("1 or more", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Adding_a_line_after_loading_the_form_does_not_make_the_header_save_conflict()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "A");
        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        using var client = factory.CreateClientAs("Viewer");
        var url = $"/Requisitions/Edit/{requisition.Id}";

        var added = await HtmxPostAsync(client, url + "?handler=AddLine", [("productId", part.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("quantity", "1")], url);
        var refreshed = RowVersionPattern().Match(await added.Content.ReadAsStringAsync()).Groups[1].Value;

        var saved = await WebForms.PostFormAsync(client, url, [("Input.Reason", "Changed"), ("Input.RowVersion", refreshed)]);

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
    }

    [Fact]
    public async Task A_stale_header_save_shows_the_conflict()
    {
        using var factory = new AppFactory();
        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);
        using var client = factory.CreateClientAs("Viewer");
        var url = $"/Requisitions/Edit/{requisition.Id}";

        var response = await WebForms.PostFormAsync(client, url, [("Input.Reason", "Changed"), ("Input.RowVersion", "999")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("changed while you were editing", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_submitted_requisition_sends_the_editor_to_its_detail_page()
    {
        using var factory = new AppFactory();
        var part = await SeedPartAsync(factory, "A");
        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer, part, submit: true);
        using var client = factory.CreateClientAs("Viewer");

        var response = await client.GetAsync($"/Requisitions/Edit/{requisition.Id}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains($"/Requisitions/{requisition.Id}", response.Headers.Location!.ToString());
    }

    // ---- settings ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_prefix_can_be_changed_in_settings_and_is_used_for_new_numbers()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        Assert.Contains("Input.RequisitionNumberPrefix", await client.GetStringAsync("/Admin/Settings"));

        await factory.WithScopeAsync(async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            var current = await settings.GetBusinessSettingsAsync();
            Assert.Equal("REQ-", current.RequisitionNumberPrefix);
            Assert.True((await settings.UpdateBusinessSettingsAsync(current with { RequisitionNumberPrefix = "RQ-" })).Succeeded);
        });

        var requisition = await SeedRequisitionAsync(factory, TestAuth.Users.Viewer);

        Assert.StartsWith("RQ-", requisition.Number);
    }

    [Fact]
    public async Task A_prefix_with_a_space_is_refused()
    {
        using var factory = new AppFactory();
        await factory.WithScopeAsync(async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            var current = await settings.GetBusinessSettingsAsync();

            var result = await settings.UpdateBusinessSettingsAsync(current with { RequisitionNumberPrefix = "R Q" });

            Assert.Contains(result.Errors, e => e.Code == "settings.requisition_prefix_invalid");
        });
    }

    [Fact]
    public void Status_labels_and_styles_cover_every_status()
    {
        foreach (var status in Enum.GetValues<RequisitionStatus>())
        {
            Assert.Equal(status.ToString(), RequisitionDisplay.StatusLabel(status));
            Assert.StartsWith("badge-", RequisitionDisplay.StatusCss(status));
        }
    }
}
