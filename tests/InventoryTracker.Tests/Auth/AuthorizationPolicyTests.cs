using System.Net;
using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Auth;

public class AuthorizationPolicyTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private const HttpStatusCode Ok = HttpStatusCode.OK;
    private const HttpStatusCode Forbidden = HttpStatusCode.Forbidden;
    private const HttpStatusCode ToLogin = HttpStatusCode.Redirect;

    public static TheoryData<string, string, HttpStatusCode> Matrix()
    {
        // role (null = anonymous), endpoint, expected status
        var data = new TheoryData<string, string, HttpStatusCode>();
        void Row(string role, string endpoint, HttpStatusCode status) => data.Add(role, endpoint, status);

        foreach (var endpoint in new[] { "admin", "approve", "edit", "view", "fallback" })
        {
            Row("Anonymous", endpoint, ToLogin);
        }

        Row("Admin", "admin", Ok);
        Row("Admin", "approve", Ok);
        Row("Admin", "edit", Ok);
        Row("Admin", "view", Ok);
        Row("Admin", "fallback", Ok);

        Row("Manager", "admin", Forbidden);
        Row("Manager", "approve", Ok);
        Row("Manager", "edit", Ok);
        Row("Manager", "view", Ok);
        Row("Manager", "fallback", Ok);

        Row("Staff", "admin", Forbidden);
        Row("Staff", "approve", Forbidden);
        Row("Staff", "edit", Ok);
        Row("Staff", "view", Ok);
        Row("Staff", "fallback", Ok);

        // The acceptance criterion: a Viewer gets 403 on a CanEdit endpoint.
        Row("Viewer", "admin", Forbidden);
        Row("Viewer", "approve", Forbidden);
        Row("Viewer", "edit", Forbidden);
        Row("Viewer", "view", Ok);
        Row("Viewer", "fallback", Ok);

        // Signed in and active but with no role: only the fallback policy (authenticated + active) lets them in.
        Row("NoRole", "admin", Forbidden);
        Row("NoRole", "approve", Forbidden);
        Row("NoRole", "edit", Forbidden);
        Row("NoRole", "view", Forbidden);
        Row("NoRole", "fallback", Ok);

        // A deactivated account is refused by every policy, the fallback included, whatever role it had.
        foreach (var endpoint in new[] { "admin", "approve", "edit", "view", "fallback" })
        {
            Row("Inactive", endpoint, Forbidden);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Policy_matrix(string role, string endpoint, HttpStatusCode expected)
    {
        using var client = role == "Anonymous" ? factory.CreateAnonymousClient() : factory.CreateClientAs(role);

        var response = await client.GetAsync("/__policy/" + endpoint);

        Assert.Equal(expected, response.StatusCode);
        if (expected == ToLogin)
        {
            Assert.StartsWith("/Account/Login?ReturnUrl=", response.RedirectPath());
            Assert.Contains(Uri.EscapeDataString("/__policy/" + endpoint), response.RedirectPath());
        }
    }

    [Fact]
    public async Task Anonymous_users_can_still_reach_health_and_static_assets()
    {
        using var client = factory.CreateAnonymousClient();

        Assert.Equal(Ok, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(Ok, (await client.GetAsync("/css/site.css")).StatusCode);
        Assert.Equal(Ok, (await client.GetAsync("/lib/htmx.min.js")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_users_can_see_error_pages()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/Error/404");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_home_page_redirects_to_login_with_a_return_url()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/Dev/Components?page=2");

        Assert.Equal(ToLogin, response.StatusCode);
        Assert.Equal("/Account/Login?ReturnUrl=" + Uri.EscapeDataString("/Dev/Components?page=2"), response.RedirectPath());
    }

    [Fact]
    public async Task Htmx_request_without_a_session_gets_401_and_a_client_side_redirect_instead_of_a_302()
    {
        using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__policy/view");
        request.Headers.Add("HX-Request", "true");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("/Account/Login?ReturnUrl=", response.Headers.GetValues("HX-Redirect").Single());
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Htmx_request_without_permission_gets_403_and_no_redirect()
    {
        using var client = factory.CreateClientAs("Viewer");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__policy/edit");
        request.Headers.Add("HX-Request", "true");

        var response = await client.SendAsync(request);

        Assert.Equal(Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("HX-Redirect"));
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Test_endpoints_are_not_mapped_outside_the_Testing_environment()
    {
        using var production = new AppFactory { EnvironmentName = "Production" };
        using var client = production.CreateClientAs("Admin");

        var response = await client.GetAsync("/__policy/view");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Every_page_model_declares_authorization_explicitly()
    {
        var pageModels = typeof(Program).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(PageModel).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(pageModels);

        var missing = pageModels
            .Where(t => t.GetCustomAttributes(inherit: true).All(a => a is not AuthorizeAttribute and not AllowAnonymousAttribute))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(missing.Count == 0, "Page models without [Authorize(Policy = ...)] or [AllowAnonymous]: " + string.Join(", ", missing));
    }

    [Fact]
    public void Authorized_page_models_name_a_policy()
    {
        var unnamed = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(PageModel).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Select(a => (Type: t, Attribute: a)))
            .Where(x => string.IsNullOrEmpty(x.Attribute.Policy))
            .Select(x => x.Type.FullName)
            .ToList();

        Assert.True(unnamed.Count == 0, "Use [Authorize(Policy = Policies.X)] rather than a bare [Authorize]: " + string.Join(", ", unnamed));
    }

    [Fact]
    public async Task All_three_policies_and_the_fallback_policy_are_registered()
    {
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var name in new[]
                 {
                     Policies.RequireAdmin, Policies.CanEdit, Policies.CanView, Policies.CanRequest, Policies.CanApprove,
                     Policies.CanImportData, Policies.CanImportBoms, Policies.CanExportData, Policies.CanExportBoms,
                     Policies.CanImport, Policies.CanExport,
                 })
        {
            Assert.NotNull(await provider.GetPolicyAsync(name));
        }

        Assert.NotNull(await provider.GetFallbackPolicyAsync());
    }

    public static TheoryData<string, DataPermission, bool, HttpStatusCode> PermissionMatrix()
    {
        var data = new TheoryData<string, DataPermission, bool, HttpStatusCode>();
        foreach (var permission in Enum.GetValues<DataPermission>())
        {
            foreach (var allowed in new[] { true, false })
            {
                data.Add("Admin", permission, allowed, Ok);
                data.Add("Manager", permission, allowed, allowed ? Ok : Forbidden);
                data.Add("Staff", permission, allowed, allowed ? Ok : Forbidden);
                data.Add("Viewer", permission, allowed, allowed ? Ok : Forbidden);
                data.Add("Inactive", permission, allowed, Forbidden);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PermissionMatrix))]
    public async Task Data_permission_matrix(string role, DataPermission permission, bool allowed, HttpStatusCode expected)
    {
        var endpoint = "/__policy/" + permission switch
        {
            DataPermission.ImportData => "import-data",
            DataPermission.ImportBoms => "import-boms",
            DataPermission.ExportData => "export-data",
            _ => "export-boms",
        };

        async Task SetAllAsync(Func<string, bool> value) => await factory.WithScopeAsync(async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            foreach (var configurable in DataPermissions.ConfigurableRoles)
            {
                Assert.True((await settings.SetAsync(SettingKeys.Permission(permission, configurable), value(configurable))).Succeeded);
            }
        });

        await SetAllAsync(_ => allowed);
        try
        {
            using var client = factory.CreateClientAs(role);
            Assert.Equal(expected, (await client.GetAsync(endpoint)).StatusCode);
        }
        finally
        {
            await SetAllAsync(r => DataPermissions.DefaultAllowed(permission, r));
        }
    }

    [Fact]
    public async Task Data_permissions_follow_the_settings_and_always_let_an_Admin_through()
    {
        using var admin = factory.CreateClientAs("Admin");
        using var staff = factory.CreateClientAs("Staff");
        using var viewer = factory.CreateClientAs("Viewer");

        Assert.Equal(Ok, (await admin.GetAsync("/__policy/import-data")).StatusCode);
        Assert.Equal(Forbidden, (await staff.GetAsync("/__policy/import-data")).StatusCode);
        Assert.Equal(Ok, (await staff.GetAsync("/__policy/export-data")).StatusCode);
        Assert.Equal(Forbidden, (await viewer.GetAsync("/__policy/export-boms")).StatusCode);
        Assert.Equal(Ok, (await staff.GetAsync("/__policy/export")).StatusCode);
        Assert.Equal(Forbidden, (await staff.GetAsync("/__policy/import")).StatusCode);

        await factory.WithScopeAsync(async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            Assert.True((await settings.SetAsync(SettingKeys.Permission(DataPermission.ImportData, Roles.Staff), true)).Succeeded);
        });

        Assert.Equal(Ok, (await staff.GetAsync("/__policy/import-data")).StatusCode);
        Assert.Equal(Ok, (await staff.GetAsync("/__policy/import")).StatusCode);

        await factory.WithScopeAsync(async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            Assert.True((await settings.SetAsync(SettingKeys.Permission(DataPermission.ImportData, Roles.Staff), false)).Succeeded);
        });

        Assert.Equal(Forbidden, (await staff.GetAsync("/__policy/import-data")).StatusCode);
    }
}
