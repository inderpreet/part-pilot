using System.Net;
using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-01-3: first-run setup, login, logout, change password and ReturnUrl handling, through the real cookie auth.</summary>
public class AccountPagesTests
{
    private static AppFactory Realistic(bool seedUsers = true) => new() { DefaultTestRole = null, SeedUsers = seedUsers };

    private static string SetCookie(HttpResponseMessage response) =>
        string.Join("\n", response.Headers.TryGetValues("Set-Cookie", out var values) ? values : []);

    // ---- first run ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Setup_creates_the_admin_signs_in_and_shows_the_company_name()
    {
        using var factory = Realistic(seedUsers: false);
        using var client = factory.CreateAnonymousClient();

        var response = await WebForms.PostFormAsync(client, "/Account/Setup",
        [
            ("Input.CompanyName", "Acme Co"), ("Input.UserName", "boss"), ("Input.DisplayName", "The Boss"),
            ("Input.Email", ""), ("Input.Password", "correct horse"), ("Input.ConfirmPassword", "correct horse"),
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.RedirectPath());

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("Acme Co", html);
        Assert.Contains("The Boss", html);
        Assert.Contains("Admin", html);
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData("correct horse", "something else")]
    public async Task Setup_rejects_a_weak_or_mismatched_password_and_creates_nothing(string password, string confirm)
    {
        using var factory = Realistic(seedUsers: false);
        using var client = factory.CreateAnonymousClient();

        var response = await WebForms.PostFormAsync(client, "/Account/Setup",
        [
            ("Input.CompanyName", "Acme Co"), ("Input.UserName", "boss"), ("Input.DisplayName", "The Boss"),
            ("Input.Email", ""), ("Input.Password", password), ("Input.ConfirmPassword", confirm),
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // form re-rendered with errors
        await factory.WithScopeAsync(async sp => Assert.False(await sp.GetRequiredService<AppDbContext>().Users.AnyAsync()));
    }

    [Fact]
    public async Task Setup_is_gone_once_an_admin_exists()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Account/Setup")).StatusCode);
    }

    // ---- login -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_succeeds_case_insensitively_with_a_session_cookie_that_is_http_only()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();

        var response = await WebForms.LoginAsync(client, TestAuth.Users.Staff.ToUpperInvariant(), TestAuth.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.RedirectPath());
        var cookie = SetCookie(response);
        Assert.Contains(".Inventory.Auth=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase); // session cookie

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Remember_me_makes_the_cookie_persistent()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();

        var response = await WebForms.LoginAsync(client, TestAuth.Users.Staff, TestAuth.Password, rememberMe: true);

        Assert.Contains("expires=", SetCookie(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_user_get_the_same_generic_message()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();

        var wrong = await WebForms.LoginAsync(client, TestAuth.Users.Staff, "not-the-password");
        var unknown = await WebForms.LoginAsync(client, "nobody-here", "not-the-password");

        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Contains("Invalid user name or password.", await wrong.Content.ReadAsStringAsync());
        Assert.Contains("Invalid user name or password.", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Five_failures_lock_the_account_and_even_the_right_password_is_refused()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();

        for (var i = 0; i < 5; i++)
        {
            await WebForms.LoginAsync(client, TestAuth.Users.Viewer, "wrong-" + i);
        }

        var response = await WebForms.LoginAsync(client, TestAuth.Users.Viewer, TestAuth.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("locked", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_deactivated_user_cannot_sign_in_and_is_told_why()
    {
        using var factory = Realistic();
        await WebForms.SetActiveAsync(factory, TestAuth.Users.Staff, false);
        using var client = factory.CreateAnonymousClient();

        var response = await WebForms.LoginAsync(client, TestAuth.Users.Staff, TestAuth.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("deactivated", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Regression: Identity's stamp validator also signs out the two-factor schemes when it rejects a session; with
    /// only the application cookie registered that threw, turning "session rejected" into a 500.
    /// </summary>
    [Fact]
    public async Task An_existing_session_is_rejected_cleanly_once_the_user_is_deactivated()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();
        await WebForms.LoginAsync(client, TestAuth.Users.Staff, TestAuth.Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

        await WebForms.SetActiveAsync(factory, TestAuth.Users.Staff, false);

        var after = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", after.RedirectPath());
    }

    [Fact]
    public async Task An_admin_password_reset_ends_the_users_existing_sessions()
    {
        using var factory = Realistic();
        using var viewer = factory.CreateAnonymousClient();
        await WebForms.LoginAsync(viewer, TestAuth.Users.Viewer, TestAuth.Password);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/")).StatusCode);

        await factory.WithScopeAsync(async sp =>
        {
            var id = await WebForms.UserIdAsync(factory, TestAuth.Users.Viewer);
            Assert.True((await sp.GetRequiredService<IUserService>().ResetPasswordAsync(id, "a brand new password")).Succeeded);
        });

        var after = await viewer.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", after.RedirectPath());
    }

    // ---- return URL --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Admin/Users?search=x", "/Admin/Users?search=x")] // local: honoured
    [InlineData("https://evil.test/", "/")]
    [InlineData("//evil.test/", "/")]
    [InlineData("/\\evil.test", "/")]
    [InlineData("/Account/Login", "/")] // never bounce back to the login form
    public async Task Return_url_is_followed_only_when_it_is_local(string returnUrl, string expected)
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();

        var response = await WebForms.LoginAsync(client, TestAuth.Users.Admin, TestAuth.Password, returnUrl: returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.RedirectPath());
    }

    // ---- logout ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Logout_is_post_only_and_needs_an_antiforgery_token()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();
        await WebForms.LoginAsync(client, TestAuth.Users.Staff, TestAuth.Password);

        var get = await client.GetAsync("/Account/Logout");
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode); // still signed in

        var noToken = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Logout_signs_out_and_shows_a_confirmation_on_the_login_page()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();
        await WebForms.LoginAsync(client, TestAuth.Users.Staff, TestAuth.Password);

        var response = await WebForms.PostFormAsync(client, "/Account/Logout", [], tokenPage: "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.RedirectPath());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);

        var login = await client.GetStringAsync("/Account/Login");
        Assert.Contains("You have been signed out.", login);
    }

    // ---- change password ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Changing_your_own_password_keeps_you_signed_in_and_replaces_the_old_password()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();
        await WebForms.LoginAsync(client, TestAuth.Users.Staff, TestAuth.Password);

        var wrongCurrent = await WebForms.PostFormAsync(client, "/Account/ChangePassword",
            [("Input.CurrentPassword", "nope"), ("Input.NewPassword", "a brand new password"), ("Input.ConfirmPassword", "a brand new password")]);
        Assert.Equal(HttpStatusCode.OK, wrongCurrent.StatusCode); // re-rendered with an error

        var changed = await WebForms.PostFormAsync(client, "/Account/ChangePassword",
            [("Input.CurrentPassword", TestAuth.Password), ("Input.NewPassword", "a brand new password"), ("Input.ConfirmPassword", "a brand new password")]);
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode); // session refreshed, not ended

        using var other = factory.CreateAnonymousClient();
        var oldPassword = await WebForms.LoginAsync(other, TestAuth.Users.Staff, TestAuth.Password);
        Assert.Equal(HttpStatusCode.OK, oldPassword.StatusCode);
        Assert.Contains("Invalid user name or password.", await oldPassword.Content.ReadAsStringAsync());

        var newPassword = await WebForms.LoginAsync(other, TestAuth.Users.Staff, "a brand new password");
        Assert.Equal(HttpStatusCode.Redirect, newPassword.StatusCode);
    }

    [Fact]
    public async Task Change_password_requires_sign_in()
    {
        using var factory = Realistic();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/Account/ChangePassword");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login?ReturnUrl=", response.RedirectPath());
    }
}
