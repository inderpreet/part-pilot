using System.Security.Claims;
using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using InventoryTracker.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Auth;

public class ClaimsAndCurrentUserTests : IAsyncLifetime
{
    private UserServiceTestHost _host = null!;

    public async Task InitializeAsync() => _host = await UserServiceTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<ClaimsPrincipal> PrincipalOfAsync(string userId)
        => _host.InScopeAsync(async sp =>
        {
            var user = (await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId))!;
            return await sp.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
        });

    private static CurrentUser CurrentUserFor(ClaimsPrincipal? principal)
        => new(new HttpContextAccessor { HttpContext = principal is null ? null : new DefaultHttpContext { User = principal } });

    [Fact]
    public async Task Principal_carries_id_name_role_display_name_and_the_active_marker()
    {
        var user = await _host.NewUserAsync("alice", Roles.Staff);

        var principal = await PrincipalOfAsync(user.Id);

        Assert.Equal(user.Id, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("alice", principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("User alice", principal.FindFirstValue(AppClaimTypes.DisplayName));
        Assert.Equal("true", principal.FindFirstValue(AppClaimTypes.Active));
        Assert.True(principal.IsInRole(Roles.Staff));
        Assert.False(principal.IsInRole(Roles.Admin));
    }

    [Fact]
    public async Task Principal_of_an_inactive_user_has_no_active_marker()
    {
        var user = await _host.NewUserAsync("gone", Roles.Admin, isActive: false);

        var principal = await PrincipalOfAsync(user.Id);

        Assert.Null(principal.FindFirst(AppClaimTypes.Active));
    }

    [Fact]
    public async Task Current_user_reads_everything_from_claims()
    {
        var user = await _host.NewUserAsync("alice", Roles.Staff);
        var current = CurrentUserFor(await PrincipalOfAsync(user.Id));

        Assert.True(current.IsAuthenticated);
        Assert.Equal(user.Id, current.Id);
        Assert.Equal("alice", current.UserName);
        Assert.Equal("User alice", current.DisplayName);
        Assert.Equal(Roles.Staff, current.Role);
        Assert.True(current.CanEdit);
        Assert.False(current.IsAdmin);
    }

    [Theory]
    [InlineData(Roles.Admin, true, true)]
    [InlineData(Roles.Staff, false, true)]
    [InlineData(Roles.Viewer, false, false)]
    public async Task Current_user_flags_follow_the_role(string role, bool isAdmin, bool canEdit)
    {
        var user = await _host.NewUserAsync("u-" + role.ToLowerInvariant(), role);
        var current = CurrentUserFor(await PrincipalOfAsync(user.Id));

        Assert.Equal(role, current.Role);
        Assert.Equal(isAdmin, current.IsAdmin);
        Assert.Equal(canEdit, current.CanEdit);
    }

    [Fact]
    public void Current_user_is_anonymous_without_a_principal_or_http_context()
    {
        foreach (var current in new[] { CurrentUserFor(null), CurrentUserFor(new ClaimsPrincipal(new ClaimsIdentity())) })
        {
            Assert.False(current.IsAuthenticated);
            Assert.Null(current.Id);
            Assert.Null(current.UserName);
            Assert.Null(current.DisplayName);
            Assert.Null(current.Role);
            Assert.False(current.IsAdmin);
            Assert.False(current.CanEdit);
        }
    }

    [Fact]
    public void Display_name_falls_back_to_the_user_name()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "bare")], "test");

        Assert.Equal("bare", CurrentUserFor(new ClaimsPrincipal(identity)).DisplayName);
    }
}
