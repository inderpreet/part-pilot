using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Auth;

public static class AuthServiceCollectionExtensions
{
    public const string LoginPath = "/Account/Login";
    public const string AccessDeniedPath = "/AccessDenied";
    public const string CookieName = ".Inventory.Auth";

    /// <summary>
    /// Registers Identity (cookie auth, no external logins), the three authorization policies plus the fallback
    /// policy, persisted data-protection keys, and the user/sign-in services.
    /// </summary>
    public static IServiceCollection AddInventoryAuth(this IServiceCollection services)
    {
        services
            .AddIdentityCore<ApplicationUser>(o =>
            {
                // NIST-style: length only, no composition rules.
                o.Password.RequiredLength = 8;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;

                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                o.User.RequireUniqueEmail = false;
                o.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders() // needed by UserManager.ResetPasswordAsync (admin password reset)
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
            .AddPasswordValidator<PasswordLengthValidator>();

        services.AddScoped<ISecurityStampValidator, ActiveUserSecurityStampValidator>();
        services.AddOptions<SecurityStampValidatorOptions>().Configure<IConfiguration>((o, config) =>
            o.ValidationInterval = TimeSpan.FromSeconds(Math.Max(0, config.GetValue(AuthConfigKeys.ValidationIntervalSeconds, 60))));

        // AddIdentityCookies registers every scheme Identity signs out of when it rejects a session (the security-stamp
        // validator also signs out the two-factor schemes); registering only the application cookie makes a rejected
        // session - a deactivated user, a password reset - throw a 500 instead of redirecting to the login page.
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies(cookies => cookies.ApplicationCookie!.Configure(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // plain HTTP on a LAN is a supported deployment
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.LoginPath = LoginPath;
                o.AccessDeniedPath = AccessDeniedPath;
                o.ReturnUrlParameter = "ReturnUrl";

                o.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync,

                    // htmx requests must not be answered with a 302 (XHR would follow it and swap the login page into a fragment).
                    OnRedirectToLogin = ctx =>
                    {
                        if (ctx.Request.IsHtmx())
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            ctx.Response.HxRedirect(HtmxLoginUrl(ctx.HttpContext));
                        }
                        else
                        {
                            ctx.Response.Redirect(ctx.RedirectUri);
                        }

                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = ctx =>
                    {
                        if (ctx.Request.IsHtmx())
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        }
                        else
                        {
                            ctx.Response.Redirect(ctx.RedirectUri);
                        }

                        return Task.CompletedTask;
                    },
                };
            }));

        // Every policy - not just the fallback - requires an authenticated *active* user, because a page that names
        // a policy is not evaluated against the fallback policy.
        services.AddSingleton<IAuthorizationHandler, DataPermissionHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(ActiveUser().Build())
            .AddPolicy(Policies.RequireAdmin, p => Apply(p).RequireRole(Roles.Admin))
            .AddPolicy(Policies.CanEdit, p => Apply(p).RequireRole(Roles.Admin, Roles.Staff))
            .AddPolicy(Policies.CanView, p => Apply(p).RequireRole(Roles.Admin, Roles.Staff, Roles.Viewer))
            .AddPolicy(Policies.CanImportData, p => Apply(p).AddRequirements(new DataPermissionRequirement(DataPermission.ImportData)))
            .AddPolicy(Policies.CanImportBoms, p => Apply(p).AddRequirements(new DataPermissionRequirement(DataPermission.ImportBoms)))
            .AddPolicy(Policies.CanExportData, p => Apply(p).AddRequirements(new DataPermissionRequirement(DataPermission.ExportData)))
            .AddPolicy(Policies.CanExportBoms, p => Apply(p).AddRequirements(new DataPermissionRequirement(DataPermission.ExportBoms)))
            .AddPolicy(Policies.CanImport, p => Apply(p).AddRequirements(new DataPermissionRequirement(DataPermission.ImportData, DataPermission.ImportBoms)))
            .AddPolicy(Policies.CanExport, p => Apply(p).AddRequirements(new DataPermissionRequirement(DataPermission.ExportData, DataPermission.ExportBoms)));

        // Keys that sign the auth cookie and antiforgery tokens live beside the database so they survive restarts
        // (sessions would otherwise all end on every restart) and travel with a backup of that folder.
        services.AddDataProtection().SetApplicationName("InventoryTracker");
        services.AddOptions<KeyManagementOptions>().Configure<HostSettings, ILoggerFactory>((o, host, loggerFactory) =>
            o.XmlRepository = new FileSystemXmlRepository(
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(host.DatabasePath) ?? AppContext.BaseDirectory, "keys")),
                loggerFactory));

        services.AddHttpContextAccessor();
        services.AddSingleton<IFirstRunState, FirstRunState>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<UserEventPublisher>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ISignInService, SignInService>();

        return services;
    }

    /// <summary>First-run redirect, then authentication and authorization. Call after <c>UseStaticFiles</c> so assets stay public.</summary>
    public static IApplicationBuilder UseInventoryAuth(this IApplicationBuilder app)
    {
        app.UseMiddleware<FirstRunMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// Login URL for an htmx request that lost its session. After signing in the user should land on the page they
    /// were looking at (<c>HX-Current-URL</c>), not on the URL of the fragment or POST endpoint htmx happened to call.
    /// </summary>
    private static string HtmxLoginUrl(HttpContext context)
    {
        string? returnUrl = null;
        if (Uri.TryCreate(context.Request.Headers["HX-Current-URL"].ToString(), UriKind.Absolute, out var current)
            && string.Equals(current.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
        {
            returnUrl = current.PathAndQuery;
        }
        else if (HttpMethods.IsGet(context.Request.Method))
        {
            returnUrl = context.Request.Path + context.Request.QueryString;
        }

        return returnUrl is null ? LoginPath : $"{LoginPath}?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    private static AuthorizationPolicyBuilder ActiveUser() => Apply(new AuthorizationPolicyBuilder());

    private static AuthorizationPolicyBuilder Apply(AuthorizationPolicyBuilder builder)
        => builder.RequireAuthenticatedUser().RequireClaim(AppClaimTypes.Active, "true");
}
