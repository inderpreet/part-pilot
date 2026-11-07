using InventoryTracker.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using InventoryTracker.Hosting;

namespace InventoryTracker.Tests.Hosting;

public class DatabaseHealthCheckTests
{
    private static async Task<HealthStatus> CheckAsync(Action<DbContextOptionsBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(configure);
        await using var provider = services.BuildServiceProvider();

        var result = await new DatabaseHealthCheck(provider.GetRequiredService<IServiceScopeFactory>())
            .CheckHealthAsync(new HealthCheckContext());
        return result.Status;
    }

    [Fact]
    public async Task Healthy_when_select_1_succeeds()
    {
        await using var db = await SqliteTestDb.CreateAsync();

        Assert.Equal(HealthStatus.Healthy, await CheckAsync(o => o.UseSqlite(db.Connection)));
    }

    [Fact]
    public async Task Unhealthy_when_the_database_cannot_be_opened()
    {
        var unreachable = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"), "x.db");

        Assert.Equal(HealthStatus.Unhealthy, await CheckAsync(o => o.UseSqlite($"Data Source={unreachable};Mode=ReadWrite")));
    }
}
