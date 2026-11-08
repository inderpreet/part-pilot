using InventoryTracker.Data.Interceptors;

namespace InventoryTracker.Services;

public static class ReportingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dashboard, reorder and report services (scoped: they share the request's DbContext) and the low-stock badge
    /// counter (a singleton: its cache is shared by every request).
    /// </summary>
    public static IServiceCollection AddReporting(this IServiceCollection services)
    {
        services.AddSingleton(ReportingChangeSignal.Shared);
        services.AddSingleton<ILowStockCounter, LowStockCounter>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IReorderService, ReorderService>();
        services.AddScoped<IReportService, ReportService>();
        return services;
    }
}
