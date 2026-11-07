namespace InventoryTracker.Services;

public enum UserEventType
{
    FirstAdminCreated,
    UserCreated,
    UserUpdated,
    PasswordChanged,
    PasswordReset,
    SignedIn,
    SignInFailed,
    LockedOut,
    SignedOut,
}

/// <summary>Something that happened to an account. Never contains passwords.</summary>
/// <param name="UserId">The affected user's id; null for a failed sign-in with an unknown user name.</param>
/// <param name="UserName">The affected (or attempted) user name.</param>
/// <param name="ActorUserId">Who did it, when different from the affected user (an admin editing someone).</param>
/// <param name="Detail">Short human-readable summary, e.g. "role Staff → Viewer".</param>
public sealed record UserEvent(
    UserEventType Type,
    string? UserId,
    string? UserName,
    string? ActorUserId,
    string? Detail,
    DateTime OccurredAtUtc);

/// <summary>
/// Hook for reacting to account events. F08 (audit log) registers an implementation; F01 deliberately writes no
/// audit rows itself. Register with <c>services.AddScoped&lt;IUserEvents, MyHandler&gt;()</c>.
/// </summary>
public interface IUserEvents
{
    Task HandleAsync(UserEvent userEvent, CancellationToken cancellationToken = default);
}

/// <summary>Fans an event out to every registered <see cref="IUserEvents"/>. A failing handler is logged, never thrown.</summary>
public sealed class UserEventPublisher(IEnumerable<IUserEvents> handlers, IClock clock, ILogger<UserEventPublisher> logger)
{
    public async Task PublishAsync(
        UserEventType type,
        string? userId,
        string? userName,
        string? actorUserId = null,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        var userEvent = new UserEvent(type, userId, userName, actorUserId, detail, clock.UtcNow);
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(userEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An audit/notification problem must not block signing in or managing users.
                logger.LogError(ex, "User event handler {Handler} failed for {EventType}", handler.GetType().Name, type);
            }
        }
    }
}
