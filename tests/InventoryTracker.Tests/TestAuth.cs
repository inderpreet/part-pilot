using System.Security.Claims;
using System.Text.Encodings.Web;
using InventoryTracker.Auth;
using InventoryTracker.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Tests;

/// <summary>Constants shared by the test auth handler, <see cref="AppFactory"/> and the seeded users.</summary>
public static class TestAuth
{
    public const string Scheme = "Test";

    /// <summary>
    /// A request carrying this header is authenticated by <see cref="TestAuthHandler"/> instead of the cookie.
    /// Values: Admin, Staff, Viewer, Inactive (valid cookie-less identity whose account is deactivated), NoRole.
    /// Requests without the header go through the real cookie authentication.
    /// </summary>
    public const string RoleHeader = "X-Test-Role";

    public const string Password = "Correct-Horse-9";

    public static class Users
    {
        public const string Admin = "admin";
        public const string Staff = "staff";
        public const string Viewer = "viewer";
    }
}

/// <summary>Fake authentication driven by the <c>X-Test-Role</c> header; claims mirror those of the real principal factory.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers[TestAuth.RoleHeader].ToString();
        if (string.IsNullOrEmpty(role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var name = "test-" + role.ToLowerInvariant();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, name),
            new(ClaimTypes.Name, name),
            new(AppClaimTypes.DisplayName, "Test " + role),
        };

        switch (role)
        {
            case "Admin" or "Staff" or "Viewer":
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim(AppClaimTypes.Active, "true"));
                break;
            case "Inactive":
                claims.Add(new Claim(ClaimTypes.Role, Roles.Admin)); // would be allowed, were the account active
                break;
            case "NoRole":
                claims.Add(new Claim(AppClaimTypes.Active, "true"));
                break;
            default:
                throw new InvalidOperationException($"Unknown test role '{role}'.");
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestAuth.Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestAuth.Scheme)));
    }
}
