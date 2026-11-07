using System.Net;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-01-3: the Admin area (user management, settings), role gating and the htmx fragments.</summary>
public class AdminPagesTests
{
    private static readonly string[] AdminUrls = ["/Admin", "/Admin/Users", "/Admin/Users/Edit", "/Admin/Settings"];

    private static HttpRequestMessage Htmx(string url, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return request;
    }

    // ---- role gating -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TestAuth.Users.Staff)]
    [InlineData(TestAuth.Users.Viewer)]
    public async Task Staff_and_viewers_signed_in_for_real_are_sent_to_access_denied_on_every_admin_page(string userName)
    {
        using var factory = new AppFactory { DefaultTestRole = null };
        using var client = factory.CreateAnonymousClient();
        await WebForms.LoginAsync(client, userName, TestAuth.Password);

        foreach (var url in AdminUrls)
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{userName} {url} -> {(int)response.StatusCode}");
            Assert.StartsWith("/AccessDenied", response.RedirectPath());
        }
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Viewer")]
    public async Task Staff_and_viewers_are_forbidden_on_every_admin_page(string role)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs(role);

        foreach (var url in AdminUrls)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Access_denied_renders_a_403_page()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");

        var response = await client.GetAsync("/AccessDenied");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("permission", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_htmx_request_to_an_admin_page_by_a_viewer_gets_403_and_no_redirect()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Viewer");

        var response = await client.SendAsync(Htmx("/Admin/Users"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("HX-Redirect"));
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Staff", false)]
    [InlineData("Viewer", false)]
    public async Task The_admin_menu_item_is_only_shown_to_admins(string role, bool visible)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs(role);

        var html = await client.GetStringAsync("/");

        Assert.Equal(visible, html.Contains("href=\"/Admin\"", StringComparison.Ordinal));
        Assert.Contains("Test " + role, html); // user menu shows who is signed in
    }

    // ---- users -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_user_list_shows_the_seeded_users_and_htmx_requests_get_only_the_table()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var full = await client.GetStringAsync("/Admin/Users");
        Assert.Contains("<html", full);
        Assert.Contains(TestAuth.Users.Staff, full);
        Assert.Contains(TestAuth.Users.Viewer, full);

        var fragment = await (await client.SendAsync(Htmx("/Admin/Users?search=" + TestAuth.Users.Viewer))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", fragment);
        Assert.Contains("<table", fragment);
        Assert.Contains(TestAuth.Users.Viewer, fragment);
        Assert.DoesNotContain(TestAuth.Users.Staff, fragment);
    }

    [Fact]
    public async Task Creating_a_user_through_the_form_adds_them_and_rejects_duplicates_case_insensitively()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        (string, string)[] Fields(string userName) =>
        [
            ("Input.UserName", userName), ("Input.DisplayName", "New Person"), ("Input.Email", ""), ("Input.Role", "Staff"),
            ("Input.IsActive", "true"), ("Input.IsActive", "false"), ("Input.Password", "a long enough password"), ("Input.ConfirmPassword", "a long enough password"),
        ];

        var created = await WebForms.PostFormAsync(client, "/Admin/Users/Edit", Fields("newbie"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Contains("newbie", await client.GetStringAsync("/Admin/Users"));

        var duplicate = await WebForms.PostFormAsync(client, "/Admin/Users/Edit", Fields("NEWBIE"));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode); // form re-rendered with the error
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted_or_deactivated_from_the_form()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var adminId = await WebForms.UserIdAsync(factory, TestAuth.Users.Admin);
        var url = "/Admin/Users/Edit/" + adminId;

        var demote = await WebForms.PostFormAsync(client, url,
            [("Input.DisplayName", "Test Admin"), ("Input.Email", ""), ("Input.Role", "Staff"), ("Input.IsActive", "true"), ("Input.IsActive", "false")]);
        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.Contains("last active Admin", await demote.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var deactivate = await WebForms.PostFormAsync(client, url,
            [("Input.DisplayName", "Test Admin"), ("Input.Email", ""), ("Input.Role", "Admin"), ("Input.IsActive", "false")]);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.Contains("last active Admin", await deactivate.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await factory.WithScopeAsync(async sp =>
        {
            var user = await sp.GetRequiredService<IUserService>().GetAsync(adminId);
            Assert.Equal("Admin", user!.Role);
            Assert.True(user.IsActive);
        });
    }

    // ---- reset password modal ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_reset_password_modal_loads_as_a_fragment()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var id = await WebForms.UserIdAsync(factory, TestAuth.Users.Staff);

        var response = await client.SendAsync(Htmx($"/Admin/Users/Edit/{id}?handler=ResetPassword"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", html);
        Assert.Contains("newPassword", html);
    }

    [Fact]
    public async Task Resetting_a_password_returns_422_for_bad_input_and_204_with_triggers_on_success()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var id = await WebForms.UserIdAsync(factory, TestAuth.Users.Staff);
        var url = $"/Admin/Users/Edit/{id}?handler=ResetPassword";
        var token = await WebForms.TokenAsync(client, "/Admin/Users");

        async Task<HttpResponseMessage> Post(string password, string confirm, bool withToken = true)
        {
            var request = Htmx(url, HttpMethod.Post);
            if (withToken)
            {
                request.Headers.Add("RequestVerificationToken", token);
            }

            request.Content = new FormUrlEncodedContent([new("newPassword", password), new("confirmPassword", confirm)]);
            return await client.SendAsync(request);
        }

        Assert.Equal((HttpStatusCode)422, (await Post("abc", "abc")).StatusCode); // too short
        Assert.Equal((HttpStatusCode)422, (await Post("a long enough password", "different")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("a long enough password", "a long enough password", withToken: false)).StatusCode);

        var ok = await Post("a long enough password", "a long enough password");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var triggers = string.Join(" ", ok.Headers.GetValues("HX-Trigger"));
        Assert.Contains("flash", triggers);
        Assert.Contains("closeModal", triggers);

        using var anonymous = factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Redirect,
            (await WebForms.LoginAsync(anonymous, TestAuth.Users.Staff, "a long enough password")).StatusCode);
    }

    // ---- settings ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_settings_normalises_them_and_the_new_company_name_shows_in_the_header_immediately()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var bad = await WebForms.PostFormAsync(client, "/Admin/Settings",
        [
            ("Input.CompanyName", "Zeta Ltd"), ("Input.CurrencyCode", "US"), ("Input.CurrencySymbol", "$"),
            ("Input.PurchaseOrderNumberPrefix", "PO-"), ("Input.AllowNegativeStock", "false"),
        ]);
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode); // 2-letter currency code rejected

        var saved = await WebForms.PostFormAsync(client, "/Admin/Settings",
        [
            ("Input.CompanyName", "Zeta Ltd"), ("Input.CurrencyCode", "eur"), ("Input.CurrencySymbol", "\u20AC"),
            ("Input.PurchaseOrderNumberPrefix", "ORD-"), ("Input.AllowNegativeStock", "true"), ("Input.AllowNegativeStock", "false"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        Assert.Contains("Zeta Ltd", await client.GetStringAsync("/"));

        await factory.WithScopeAsync(async sp =>
        {
            var settings = await sp.GetRequiredService<ISettingsService>().GetBusinessSettingsAsync();
            Assert.Equal("EUR", settings.CurrencyCode);
            Assert.Equal("ORD-", settings.PurchaseOrderNumberPrefix);
            Assert.True(settings.AllowNegativeStock);
        });
    }
}
