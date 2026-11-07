using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace InventoryTracker.Hosting;

public static class HealthEndpoint
{
    public static string Version { get; } =
        typeof(HealthEndpoint).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Maps anonymous <c>/health</c>: 200 <c>{status, version, db}</c>, or 503 when any check is unhealthy.</summary>
    public static IEndpointConventionBuilder MapHealth(this IEndpointRouteBuilder endpoints)
        => endpoints.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteAsync }).AllowAnonymous();

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var db = report.Entries.TryGetValue(DatabaseHealthCheck.Name, out var entry)
            ? entry.Status == HealthStatus.Healthy ? "ok" : "error"
            : "unknown";

        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status == HealthStatus.Healthy ? "ok" : "unhealthy",
            version = Version,
            db,
        }));
    }
}
