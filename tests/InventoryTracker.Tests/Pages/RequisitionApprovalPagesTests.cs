using System.Net;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-12-3: the approval queue, the decision form, cancelling, and the role and permission screens.</summary>
public sealed partial class RequisitionPagesTests
{
    private static async Task<RequisitionDetail> SeedSubmittedAsync(AppFactory factory, string userName, params int[] parts)
    {
        RequisitionDetail? detail = null;
        var actor = new RequisitionActor(await WebForms.UserIdAsync(factory, userName));
        await factory.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<IRequisitionService>();
            var created = await service.CreateDraftAsync(new RequisitionHeaderInput(null, "Line stopped"), actor);
            Assert.True(created.Succeeded);
            foreach (var part in parts)
            {
                Assert.True((await service.AddLineAsync(created.Value.Id, new RequisitionLineInput(part, 5), actor)).Succeeded);
            }

            var draft = (await service.GetAsync(created.Value.Id, actor))!;
            detail = (await service.SubmitAsync(draft.Id, draft.RowVersion, actor)).Value;
        });
        return detail!;
    }

    private static IEnumerable<(string, string)> DecisionFields(RequisitionDetail requisition, params (string Decision, string Quantity, string Reason)[] rows)
    {
        yield return ("RowVersion", requisition.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 0; i < rows.Length; i++)
        {
            yield return ($"Lines[{i}].LineId", requisition.Lines[i].Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return ($"Lines[{i}].Decision", rows[i].Decision);
            yield return ($"Lines[{i}].QuantityApproved", rows[i].Quantity);
            yield return ($"Lines[{i}].Reason", rows[i].Reason);
        }
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Admin")]
    public async Task Approvers_see_the_queue_and_the_decision_form(string role)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs(role);
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-1"));

        var queue = await client.GetStringAsync("/Requisitions/Approvals");
        var form = await client.GetStringAsync($"/Requisitions/{requisition.Id}/Decide");

        Assert.Contains(requisition.Number, queue);
        Assert.Contains($"/Requisitions/{requisition.Id}/Decide", queue);
        Assert.Contains("Save decision", form);
        Assert.Contains("name=\"Lines[0].Decision\"", form);
    }

    [Fact]
    public async Task The_queue_marks_your_own_and_the_form_says_so()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Manager, await SeedPartAsync(factory, "APP-2"));

        Assert.Contains("Yours", await client.GetStringAsync("/Requisitions/Approvals"));
        Assert.Contains("You raised this requisition", await client.GetStringAsync($"/Requisitions/{requisition.Id}/Decide"));
    }

    [Fact]
    public async Task An_empty_queue_says_so()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");

        Assert.Contains("Nothing waiting", await client.GetStringAsync("/Requisitions/Approvals"));
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Viewer")]
    public async Task Others_are_forbidden_from_approval_pages_and_cancelling(string role)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs(role);
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-3"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Requisitions/Approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Requisitions/{requisition.Id}/Decide")).StatusCode);

        var cancel = await WebForms.PostFormAsync(
            client, $"/Requisitions/{requisition.Id}?handler=Cancel",
            [("rowVersion", requisition.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("reason", "No")],
            "/Requisitions");

        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.Equal(RequisitionStatus.Submitted, await StatusOfAsync(factory, requisition.Id));
    }

    [Fact]
    public async Task Staff_do_not_get_the_nav_entry_and_a_Manager_gets_it_with_a_count()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        using var manager = factory.CreateClientAs("Manager");
        await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-4"));

        Assert.DoesNotContain("href=\"/Requisitions/Approvals\"", await staff.GetStringAsync("/Requisitions"));

        var page = await manager.GetStringAsync("/Requisitions");
        Assert.Contains("href=\"/Requisitions/Approvals\"", page);
        Assert.Contains("1 awaiting a decision", page);
    }

    [Fact]
    public async Task Approve_all_fills_the_form_but_decides_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-5"));

        var form = await client.GetStringAsync($"/Requisitions/{requisition.Id}/Decide?approveAll=true");

        Assert.Matches("value=\"Approved\"\\s+checked", form);
        Assert.Equal(RequisitionStatus.Submitted, await StatusOfAsync(factory, requisition.Id));
    }

    [Fact]
    public async Task A_full_approval_goes_end_to_end()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-6"));
        var url = $"/Requisitions/{requisition.Id}/Decide";

        var response = await WebForms.PostFormAsync(client, url, DecisionFields(requisition, ("Approved", "5", "")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(RequisitionStatus.Approved, await StatusOfAsync(factory, requisition.Id));
        Assert.Contains("Approved by Test Manager", await client.GetStringAsync($"/Requisitions/{requisition.Id}"));
    }

    [Fact]
    public async Task One_line_approved_one_reduced_one_rejected_in_a_single_submit()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        using var viewer = factory.CreateClientAs("Viewer");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-7"), await SeedPartAsync(factory, "APP-8"), await SeedPartAsync(factory, "APP-9"));

        var response = await WebForms.PostFormAsync(
            client, $"/Requisitions/{requisition.Id}/Decide",
            DecisionFields(requisition, ("Approved", "5", ""), ("Approved", "2", "Only two left in budget"), ("Rejected", "not a number", "Wrong part")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(RequisitionStatus.Approved, await StatusOfAsync(factory, requisition.Id));

        // The requester sees what happened to each line, and why.
        var detail = await viewer.GetStringAsync($"/Requisitions/{requisition.Id}");
        Assert.Contains("Reduced", detail);
        Assert.Contains("Only two left in budget", detail);
        Assert.Contains("Wrong part", detail);
        Assert.DoesNotContain("The requester decided this themselves", detail);
        Assert.DoesNotContain($"/Requisitions/{requisition.Id}/Decide", detail);
    }

    [Fact]
    public async Task A_row_mistake_is_shown_on_the_form_and_saves_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-10"));

        var response = await WebForms.PostFormAsync(client, $"/Requisitions/{requisition.Id}/Decide", DecisionFields(requisition, ("Rejected", "5", "")));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("field-error", html);
        Assert.Contains("was rejected", html);
        Assert.Equal(RequisitionStatus.Submitted, await StatusOfAsync(factory, requisition.Id));
    }

    [Fact]
    public async Task A_decision_on_a_stale_page_sends_you_to_the_requisition()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-11"));
        var url = $"/Requisitions/{requisition.Id}/Decide";
        var token = await WebForms.TokenAsync(client, url);
        Assert.Equal(HttpStatusCode.Redirect, (await WebForms.PostFormAsync(client, url, DecisionFields(requisition, ("Approved", "5", "")))).StatusCode);

        var second = await client.PostAsync(url, new FormUrlEncodedContent(DecisionFields(requisition, ("Rejected", "0", "Late")).Prepend(("__RequestVerificationToken", token)).Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal($"/Requisitions/{requisition.Id}", second.Headers.Location!.ToString());
        Assert.Equal(RequisitionStatus.Approved, await StatusOfAsync(factory, requisition.Id));
    }

    [Fact]
    public async Task Deciding_your_own_shows_on_the_detail_page()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Manager, await SeedPartAsync(factory, "APP-12"));

        await WebForms.PostFormAsync(client, $"/Requisitions/{requisition.Id}/Decide", DecisionFields(requisition, ("Approved", "5", "")));

        Assert.Contains("The requester decided this themselves", await client.GetStringAsync($"/Requisitions/{requisition.Id}"));
    }

    [Fact]
    public async Task Cancelling_needs_a_reason_and_shows_it()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-13"));
        var page = $"/Requisitions/{requisition.Id}";
        var version = requisition.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await WebForms.PostFormAsync(client, page + "?handler=Cancel", [("rowVersion", version), ("reason", " ")], page);
        Assert.Equal(RequisitionStatus.Submitted, await StatusOfAsync(factory, requisition.Id));

        var response = await WebForms.PostFormAsync(client, page + "?handler=Cancel", [("rowVersion", version), ("reason", "Project dropped")], page);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(RequisitionStatus.Cancelled, await StatusOfAsync(factory, requisition.Id));
        var html = await client.GetStringAsync(page);
        Assert.Contains("Cancelled by Test Manager", html);
        Assert.Contains("Project dropped", html);
    }

    [Fact]
    public async Task Opening_the_form_for_a_decided_requisition_goes_to_its_detail()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");
        var requisition = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, await SeedPartAsync(factory, "APP-14"));
        await WebForms.PostFormAsync(client, $"/Requisitions/{requisition.Id}/Decide", DecisionFields(requisition, ("Approved", "5", "")));

        var response = await client.GetAsync($"/Requisitions/{requisition.Id}/Decide");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Requisitions/{requisition.Id}", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/Products")]
    [InlineData("/Stock/Movements")]
    [InlineData("/Suppliers")]
    [InlineData("/PurchaseOrders")]
    [InlineData("/Projects")]
    [InlineData("/Requisitions?tab=All")]
    public async Task A_Manager_can_open_the_working_pages(string url)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Manager");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task The_role_choice_offers_Manager()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        Assert.Contains("<option value=\"Manager\"", await client.GetStringAsync("/Admin/Users/Edit"));
    }

    [Fact]
    public async Task The_Manager_permission_column_saves()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var page = await client.GetStringAsync("/Admin/Settings");
        Assert.Contains("name=\"Permissions.ManagerImportData\"", page);

        var response = await WebForms.PostFormAsync(
            client, "/Admin/Settings?handler=Permissions",
            [("Permissions.ManagerImportData", "true"), ("Permissions.ManagerExportData", "false")],
            "/Admin/Settings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await factory.WithScopeAsync(async sp =>
        {
            var saved = await sp.GetRequiredService<ISettingsService>().GetDataPermissionsAsync();
            Assert.True(saved.ManagerImportData);
            Assert.False(saved.ManagerExportData);
            Assert.False(await sp.GetRequiredService<ISettingsService>().GetBoolAsync(SettingKeys.Permission(DataPermission.ImportData, Roles.Staff)));
        });
    }
}
