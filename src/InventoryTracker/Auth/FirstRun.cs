using InventoryTracker.Data;
using InventoryTracker.Hosting;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Auth;

/// <summary>Whether the install has any user yet. Until it does, every request is sent to first-run setup.</summary>
public interface IFirstRunState
{
    /// <summary>True once at least one user exists. Cached permanently after the first <c>true</c>.</summary>
    Task<bool> HasUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Called by <c>UserService</c> after the first admin is created so the cache flips immediately.</summary>
    void MarkUsersExist();
}

public sealed class FirstRunState(IServiceScopeFactory scopeFactory) : IFirstRunState
{
    private volatile bool _hasUsers;

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        if (_hasUsers)
        {
            return true;
        }

        // While there are no users this runs per request - a trivial indexed query on a tiny table.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _hasUsers = await db.Users.AnyAsync(cancellationToken);
        return _hasUsers;
    }

    public void MarkUsersExist() => _hasUsers = true;
}

/// <summary>
/// On a fresh install (no users) redirects every request to <c>/Account/Setup</c> so the app is never usable, or
/// reachable, without an admin account. Static files are served before this middleware; <c>/health</c> and the
/// error pages are exempt.
/// </summary>
public sealed class FirstRunMiddleware(RequestDelegate next)
{
    public const string SetupPath = "/Account/Setup";

    public async Task InvokeAsync(HttpContext context, IFirstRunState state)
    {
        if (IsExempt(context.Request.Path) || await state.HasUsersAsync(context.RequestAborted))
        {
            await next(context);
            return;
        }

        if (context.Request.IsHtmx())
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.HxRedirect(SetupPath);
            return;
        }

        context.Response.Redirect(SetupPath);
    }

    private static bool IsExempt(PathString path)
        => path.StartsWithSegments(SetupPath, StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/Error", StringComparison.OrdinalIgnoreCase);
}
