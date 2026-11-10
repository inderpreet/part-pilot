using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InventoryTracker.Tests;

/// <summary>
/// Hosts the real app in-process against a throwaway SQLite file, in the non-Development "Testing" environment.
/// <para>
/// By default three users are seeded (<see cref="TestAuth.Users"/>) and <see cref="CreateClient()"/>-style clients are
/// sent as an Admin via the <c>X-Test-Role</c> header, so pages can be tested without signing in. Use
/// <see cref="CreateAnonymousClient"/> or <see cref="CreateClientAs"/> for the auth-specific cases.
/// </para>
/// </summary>
public class AppFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inventory-web-").FullName;

    /// <summary>When set, becomes the content root (e.g. a folder with no wwwroot).</summary>
    public string? ContentRootOverride { get; init; }

    /// <summary>Hosting environment; defaults to "Testing" (non-Development, enables test-only endpoints).</summary>
    public string EnvironmentName { get; init; } = "Testing";

    /// <summary>Seed admin/staff/viewer users at startup. Turn off to test the first-run experience.</summary>
    public bool SeedUsers { get; init; } = true;

    /// <summary>Role sent by default on every client; <c>null</c> = anonymous (real cookie auth only).</summary>
    public string? DefaultTestRole { get; init; } = "Admin";

    /// <summary>Seconds between security-stamp re-validations; 0 = every request (lets tests see deactivation immediately).</summary>
    public int ValidationIntervalSeconds { get; init; } = 0;

    /// <summary>Extra configuration values (highest priority), e.g. <c>Branding:ProductName</c>.</summary>
    public IReadOnlyDictionary<string, string?>? Configuration { get; init; }

    public string DatabasePath => Path.Combine(_dir, "test.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        if (ContentRootOverride is not null)
        {
            builder.UseContentRoot(ContentRootOverride);
        }

        var validationInterval = ValidationIntervalSeconds;
        var extra = Configuration;
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["INVENTORY_DB"] = DatabasePath,
                ["Logging:File:Enabled"] = "false",
                ["Auth:ValidationIntervalSeconds"] = validationInterval.ToString(),

                // Pinned so a developer's own Branding__* variables or .env never change what the tests see.
                ["Branding:ProductName"] = "Part Pilot",
                ["Branding:CompanyName"] = "My Company",
            });

            if (extra is not null)
            {
                config.AddInMemoryCollection(extra);
            }
        });

        var seed = SeedUsers;
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuth.Scheme, _ => { });

            // Requests that carry X-Test-Role are authenticated by the test handler; all others use the real cookie.
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, o =>
                o.ForwardDefaultSelector = ctx => ctx.Request.Headers.ContainsKey(TestAuth.RoleHeader) ? TestAuth.Scheme : null);

            if (seed)
            {
                services.AddHostedService<SeedUsersService>();
            }
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (DefaultTestRole is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, DefaultTestRole);
        }
    }

    /// <summary>A cookie-handling client with no test identity and redirects NOT followed.</summary>
    public HttpClient CreateAnonymousClient(bool followRedirects = false)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects });
        client.DefaultRequestHeaders.Remove(TestAuth.RoleHeader);
        return client;
    }

    /// <summary>A client authenticated as the given test role (Admin, Staff, Viewer, Inactive, NoRole); redirects not followed.</summary>
    public HttpClient CreateClientAs(string role)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Remove(TestAuth.RoleHeader);
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, role);
        return client;
    }

    /// <summary>Runs <paramref name="action"/> with a scoped service provider of the running app.</summary>
    public async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort temp cleanup
            }
        }
    }

    /// <summary>Creates the standard test users once, when the host starts and the database has no users.</summary>
    private sealed class SeedUsersService(IServiceScopeFactory scopeFactory) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Users.AnyAsync(cancellationToken))
            {
                return;
            }

            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            Check(await users.CreateFirstAdminAsync(TestAuth.Users.Admin, "Test Admin", "admin@example.test", TestAuth.Password, cancellationToken));
            Check(await users.CreateAsync(new CreateUserInput(TestAuth.Users.Manager, "Test Manager", null, Roles.Manager, TestAuth.Password), cancellationToken: cancellationToken));
            Check(await users.CreateAsync(new CreateUserInput(TestAuth.Users.Staff, "Test Staff", null, Roles.Staff, TestAuth.Password), cancellationToken: cancellationToken));
            Check(await users.CreateAsync(new CreateUserInput(TestAuth.Users.Viewer, "Test Viewer", null, Roles.Viewer, TestAuth.Password), cancellationToken: cancellationToken));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static void Check(Result result)
        {
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("Seeding test users failed: " + string.Join("; ", result.Errors.Select(e => e.Message)));
            }
        }
    }
}
