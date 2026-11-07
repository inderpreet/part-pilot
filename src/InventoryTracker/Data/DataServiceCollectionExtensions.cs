using InventoryTracker.Data.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Applies SQLite and the app's interceptors to a context options builder.</summary>
    public static DbContextOptionsBuilder UseInventorySqlite(this DbContextOptionsBuilder options, string connectionString)
        => options.UseSqlite(connectionString).AddInventoryInterceptors();

    /// <summary>Adds the pragma, timestamp and row-version interceptors.</summary>
    public static DbContextOptionsBuilder AddInventoryInterceptors(this DbContextOptionsBuilder options)
        => options.AddInterceptors(new SqlitePragmaInterceptor(), new TimestampInterceptor(), new RowVersionInterceptor());

    public static IServiceCollection AddInventoryData(this IServiceCollection services, string connectionString)
        => services.AddInventoryData(_ => connectionString);

    /// <summary>Registers the DbContext with a connection string resolved lazily from the final configuration.</summary>
    public static IServiceCollection AddInventoryData(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddDbContext<AppDbContext>((sp, o) => o.UseInventorySqlite(connectionString(sp)));
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
