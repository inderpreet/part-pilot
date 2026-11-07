using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Data.Interceptors;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Tests.Services;

/// <summary>Controllable <see cref="IClock"/>.</summary>
public sealed class TestClock(DateTime start) : IClock
{
    public DateTime UtcNow { get; set; } = start;
}

/// <summary>Collects every event; optionally throws, to prove a failing handler can't break the operation.</summary>
public sealed class RecordingUserEvents : IUserEvents
{
    public List<UserEvent> Events { get; } = [];

    public bool Throw { get; set; }

    public Task HandleAsync(UserEvent userEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(userEvent);
        return Throw ? throw new InvalidOperationException("handler failed") : Task.CompletedTask;
    }
}

/// <summary>
/// Real Identity + EF + the real services over an in-memory SQLite database - everything except the web host.
/// Each test gets its own database.
/// </summary>
public sealed class UserServiceTestHost : IAsyncDisposable
{
    private readonly SqliteTestDb _db;
    private readonly ServiceProvider _provider;
    private readonly string _keysDir = Directory.CreateTempSubdirectory("inventory-keys-").FullName;

    private UserServiceTestHost(SqliteTestDb db, RecordingUserEvents events, TestClock clock)
    {
        _db = db;
        Events = events;
        Clock = clock;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new HostSettings("http://localhost", "Data Source=:memory:", Path.Combine(_keysDir, "test.db"), _keysDir, false));
        services.AddDbContext<AppDbContext>(o => o
            .UseSqlite(db.Connection)
            .AddInterceptors(new TimestampInterceptor(), new RowVersionInterceptor()));
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IUserEvents>(events);
        services.AddInventoryAuth();
        _provider = services.BuildServiceProvider();
    }

    public RecordingUserEvents Events { get; }

    public TestClock Clock { get; }

    public IServiceProvider Services => _provider;

    public static async Task<UserServiceTestHost> CreateAsync()
        => new(await SqliteTestDb.CreateAsync(), new RecordingUserEvents(), new TestClock(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)));

    /// <summary>Runs <paramref name="action"/> in a fresh scope (fresh DbContext, so nothing is served from a tracking cache).</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task InScopeAsync(Func<IServiceProvider, Task> action) => InScopeAsync(async sp =>
    {
        await action(sp);
        return 0;
    });

    public Task<T> UsersAsync<T>(Func<IUserService, Task<T>> action) => InScopeAsync(sp => action(sp.GetRequiredService<IUserService>()));

    public Task<UserDto> NewUserAsync(string userName, string role = Roles.Staff, bool isActive = true, string password = "password-1234")
        => UsersAsync(async users =>
        {
            var result = await users.CreateAsync(new CreateUserInput(userName, "User " + userName, $"{userName}@example.test", role, password, isActive));
            return result.Succeeded ? result.Value : throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Message)));
        });

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _db.DisposeAsync();
        try
        {
            Directory.Delete(_keysDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
