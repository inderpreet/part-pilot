using System.Net;
using InventoryTracker.Auth;
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

        foreach (var endpoint in new[] { "admin", "edit", "view", "fallback" })
        {
            Row("Anonymous", endpoint, ToLogin);
        }

        Row("Admin", "admin", Ok);
        Row("Admin", "edit", Ok);
        Row("Admin", "view", Ok);
        Row("Admin", "fallback", Ok);

        Row("Staff", "admin", Forbidden);
        Row("Staff", "edit", Ok);
        Row("Staff", "view", Ok);
        Row("Staff", "fallback", Ok);

        // The acceptance criterion: a Viewer gets 403 on a CanEdit endpoint.
        Row("Viewer", "admin", Forbidden);
        Row("Viewer", "edit", Forbidden);
        Row("Viewer", "view", Ok);
        Row("Viewer", "fallback", Ok);

        // Signed in and active but with no role: only the fallback policy (authenticated + active) lets them in.
        Row("NoRole", "admin", Forbidden);
        Row("NoRole", "edit", Forbidden);
        Row("NoRole", "view", Forbidden);
        Row("NoRole", "fallback", Ok);

        // A deactivated account is refused by every policy, the fallback included, whatever role it had.
        foreach (var endpoint in new[] { "admin", "edit", "view", "fallback" })
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

        foreach (var name in new[] { Policies.RequireAdmin, Policies.CanEdit, Policies.CanView })
        {
            Assert.NotNull(await provider.GetPolicyAsync(name));
        }

        Assert.NotNull(await provider.GetFallbackPolicyAsync());
    }
}
