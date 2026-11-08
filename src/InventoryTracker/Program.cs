using System.Diagnostics;
using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

// .env first, so INVENTORY_* values are visible as ordinary environment variables (real env vars still win).
DotEnv.LoadDefaults();

var cli = CliOptions.Parse(args);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = cli.ConfigurationArgs });

// Built-in defaults live in code (lowest priority) so the executable needs no files beside it.
builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?>
    {
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
        ["AllowedHosts"] = "*",
    },
});

// Optional appsettings.json beside the executable, then the command line again so --db/--bind/--port/--urls win over everything.
builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
    .AddCommandLine(cli.ConfigurationArgs, CliOptions.SwitchMappings);

// Works as a Windows Service / systemd unit; both are no-ops when launched normally.
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

var hostSettings = HostSettings.Resolve(builder.Configuration);
builder.WebHost.UseUrls(hostSettings.Urls);

// Resolved lazily so test hosts (and anything else that adjusts configuration late) are honoured.
builder.Services.AddSingleton(sp => HostSettings.Resolve(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddInventoryData(sp => sp.GetRequiredService<HostSettings>().ConnectionString);

builder.Services.AddSingleton<ILoggerProvider>(sp =>
    sp.GetRequiredService<IConfiguration>().GetValue("Logging:File:Enabled", true)
        ? new RollingFileLoggerProvider(sp.GetRequiredService<HostSettings>().LogDirectory)
        : NullLoggerProvider.Instance);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();

builder.Services.AddInventoryAuth();
builder.Services.AddCatalog();
builder.Services.AddStock();
builder.Services.AddSuppliers();

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name);
builder.Services.AddRazorPages();

// htmx sends the token as a header (set in _Layout via hx-headers); plain forms still use the hidden field.
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync();
}

// Pages return a partial for HX-Request and a full page otherwise, so caches must key on that header.
app.Use((context, next) =>
{
    context.Response.Headers.Append("Vary", "HX-Request");
    return next(context);
});

app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error/{0}");

// Development serves wwwroot from disk (hot edit); everything else serves the copy embedded in the assembly.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = app.Environment.IsDevelopment()
        ? app.Environment.WebRootFileProvider
        : new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot"),
});

// First-run redirect, then authentication + authorization (fallback policy: signed-in and active).
app.UseInventoryAuth();

app.MapHealth();
app.MapRazorPages();

if (app.Environment.IsEnvironment("Testing"))
{
    // Test-only endpoints (never mapped in Development or Production).
    // Lets tests exercise the unhandled-exception path.
    app.MapGet("/__fault", () => { throw new InvalidOperationException("Test fault"); }).AllowAnonymous();

    // One endpoint per policy, so the role matrix can be asserted without depending on page content.
    app.MapGet("/__policy/admin", () => "ok").RequireAuthorization(Policies.RequireAdmin);
    app.MapGet("/__policy/edit", () => "ok").RequireAuthorization(Policies.CanEdit);
    app.MapGet("/__policy/view", () => "ok").RequireAuthorization(Policies.CanView);
    app.MapGet("/__policy/fallback", () => "ok"); // no metadata: only the fallback policy applies
}

var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = app.Urls.ToArray();
    startupLog.LogInformation("Inventory Tracker {Version} listening on {Urls}; database {Database}", HealthEndpoint.Version, string.Join(", ", urls), hostSettings.DatabasePath);
    if (hostSettings.ExposedToNetwork)
    {
        startupLog.LogWarning("Bound to network interfaces: other machines can reach this app over plain HTTP. Use INVENTORY_BIND=localhost to restrict it to this machine.");
    }

    if (cli.OpenBrowser && urls.FirstOrDefault() is { } first)
    {
        var url = first.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost").Replace("+", "localhost").Replace("*", "localhost");
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            startupLog.LogWarning(ex, "Could not open the browser at {Url}", url);
        }
    }
});

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the app in tests.</summary>
public partial class Program;
