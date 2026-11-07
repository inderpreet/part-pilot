using System.Net;
using InventoryTracker.Auth;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Auth;

public class FirstRunTests : IDisposable
{
    private readonly AppFactory _factory = new() { SeedUsers = false, DefaultTestRole = null };

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/")]
    [InlineData("/Dev/Components")]
    [InlineData("/Admin/Users")]
    [InlineData("/no/such/page")]
    public async Task On_a_fresh_install_every_page_redirects_to_setup(string path)
    {
        using var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Setup", response.RedirectPath());
    }

    [Fact]
    public async Task On_a_fresh_install_posts_are_redirected_too()
    {
        using var client = _factory.CreateAnonymousClient();

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Setup", response.RedirectPath());
    }

    [Fact]
    public async Task Htmx_requests_on_a_fresh_install_are_redirected_client_side()
    {
        using var client = _factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("HX-Request", "true");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/Account/Setup", response.Headers.GetValues("HX-Redirect").Single());
    }

    [Fact]
    public async Task Health_static_files_and_error_pages_stay_reachable_before_setup()
    {
        using var client = _factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/css/site.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/js/site.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Error/404")).StatusCode);
    }

    [Fact]
    public async Task The_setup_path_itself_is_not_redirected_in_a_loop()
    {
        using var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/Account/Setup");

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task After_the_admin_exists_the_redirect_goes_to_login_instead()
    {
        using var client = _factory.CreateAnonymousClient();
        Assert.Equal("/Account/Setup", (await client.GetAsync("/")).RedirectPath());

        await _factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<IUserService>()
                .CreateFirstAdminAsync("boss", "The Boss", null, "long-enough-pw");
            Assert.True(result.Succeeded);
        });

        var after = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", after.RedirectPath());
    }

    [Fact]
    public async Task A_user_created_behind_the_apps_back_is_noticed_on_the_next_request()
    {
        using var client = _factory.CreateAnonymousClient();
        Assert.Equal("/Account/Setup", (await client.GetAsync("/")).RedirectPath());

        // Not via CreateFirstAdminAsync, so IFirstRunState is not told: it must re-check the database.
        await _factory.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<IUserService>();
            Assert.True((await users.CreateAsync(new CreateUserInput("manual", "Manual", null, "Admin", "long-enough-pw"))).Succeeded);
        });

        var after = await client.GetAsync("/");

        Assert.StartsWith("/Account/Login", after.RedirectPath());
        Assert.True(await _factory.Services.GetRequiredService<IFirstRunState>().HasUsersAsync());
    }
}
