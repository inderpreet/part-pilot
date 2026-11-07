using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace InventoryTracker.Services;

public enum LoginOutcome
{
    Succeeded,

    /// <summary>Unknown user or wrong password - deliberately indistinguishable to the caller.</summary>
    InvalidCredentials,

    LockedOut,

    /// <summary>Correct password but the account has been deactivated.</summary>
    Inactive,
}

public sealed record LoginResult(LoginOutcome Outcome, DateTimeOffset? LockoutEnd = null)
{
    public bool Succeeded => Outcome == LoginOutcome.Succeeded;
}

/// <summary>Sign in / out of the cookie session. Page handlers call this; they never touch SignInManager directly.</summary>
public interface ISignInService
{
    Task<LoginResult> SignInAsync(string? userName, string? password, bool rememberMe, CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-issues the current user's cookie (e.g. after they change their own password, which rotates the security stamp).</summary>
    Task RefreshSignInAsync(string userId);
}

public sealed class SignInService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IUserService users,
    ICurrentUser currentUser,
    UserEventPublisher events,
    IClock clock)
    : ISignInService
{
    /// <summary>How long a "remember me" session lasts (sliding). Without it the session is browser-session only, 8 h sliding at most.</summary>
    public static readonly TimeSpan RememberMeDuration = TimeSpan.FromDays(14);

    // Verified against when the user name is unknown so a missing user costs the same time as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "not-a-real-password"));

    public async Task<LoginResult> SignInAsync(string? userName, string? password, bool rememberMe, CancellationToken cancellationToken = default)
    {
        userName = userName?.Trim();
        password ??= string.Empty;

        var user = string.IsNullOrEmpty(userName) ? null : await userManager.FindByNameAsync(userName);
        if (user is null)
        {
            userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash.Value, password);
            await events.PublishAsync(UserEventType.SignInFailed, null, userName, detail: "unknown user", cancellationToken: cancellationToken);
            return new LoginResult(LoginOutcome.InvalidCredentials);
        }

        // lockoutOnFailure: 5 wrong passwords lock the account for 15 minutes (configured in AddInventoryAuth).
        var check = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (check.IsLockedOut)
        {
            await events.PublishAsync(UserEventType.LockedOut, user.Id, user.UserName, cancellationToken: cancellationToken);
            return new LoginResult(LoginOutcome.LockedOut, user.LockoutEnd);
        }

        if (!check.Succeeded)
        {
            await events.PublishAsync(UserEventType.SignInFailed, user.Id, user.UserName, detail: "wrong password", cancellationToken: cancellationToken);
            return new LoginResult(LoginOutcome.InvalidCredentials);
        }

        if (!user.IsActive)
        {
            await events.PublishAsync(UserEventType.SignInFailed, user.Id, user.UserName, detail: "account inactive", cancellationToken: cancellationToken);
            return new LoginResult(LoginOutcome.Inactive);
        }

        var properties = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            AllowRefresh = true,
            ExpiresUtc = rememberMe ? clock.UtcNow + RememberMeDuration : null,
        };
        await signInManager.SignInAsync(user, properties);
        await users.RecordSignInAsync(user.Id, cancellationToken);
        await events.PublishAsync(UserEventType.SignedIn, user.Id, user.UserName, cancellationToken: cancellationToken);
        return new LoginResult(LoginOutcome.Succeeded);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var (id, name) = (currentUser.Id, currentUser.UserName);
        await signInManager.SignOutAsync();
        if (id is not null)
        {
            await events.PublishAsync(UserEventType.SignedOut, id, name, cancellationToken: cancellationToken);
        }
    }

    public async Task RefreshSignInAsync(string userId)
    {
        if (await userManager.FindByIdAsync(userId) is { } user)
        {
            await signInManager.RefreshSignInAsync(user);
        }
    }
}
