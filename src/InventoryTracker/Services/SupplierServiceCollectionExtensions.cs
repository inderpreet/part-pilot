namespace InventoryTracker.Services;

public static class SupplierServiceCollectionExtensions
{
    /// <summary>Registers the supplier service (scoped: shares the request's DbContext).</summary>
    public static IServiceCollection AddSuppliers(this IServiceCollection services)
    {
        services.AddScoped<ISupplierService, SupplierService>();
        return services;
    }
}
