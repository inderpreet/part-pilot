using System.Security.Claims;
using InventoryTracker.Data;

namespace InventoryTracker.Auth;

/// <summary>The signed-in user of the current request, read from the auth cookie's claims (no database access).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>User id (GUID string); <c>null</c> when anonymous. Use for <c>UserId</c> foreign keys.</summary>
    string? Id { get; }

    string? UserName { get; }

    /// <summary>Display name, falling back to the user name.</summary>
    string? DisplayName { get; }

    /// <summary>The user's single role (see <see cref="Roles"/>), or <c>null</c> when anonymous / role-less.</summary>
    string? Role { get; }

    bool IsAdmin { get; }

    /// <summary>Admin or Staff.</summary>
    bool CanEdit { get; }
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? Id => Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name);

    public string? DisplayName => Principal?.FindFirstValue(AppClaimTypes.DisplayName) is { Length: > 0 } name ? name : UserName;

    public string? Role => Roles.All.FirstOrDefault(r => Principal?.IsInRole(r) == true);

    public bool IsAdmin => Principal?.IsInRole(Roles.Admin) == true;

    public bool CanEdit => IsAdmin || Principal?.IsInRole(Roles.Staff) == true;
}
