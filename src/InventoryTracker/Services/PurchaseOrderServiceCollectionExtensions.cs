namespace InventoryTracker.Services;

public static class PurchaseOrderServiceCollectionExtensions
{
    /// <summary>Registers the purchase order service and its event publisher (scoped: share the request's DbContext and transaction).</summary>
    public static IServiceCollection AddPurchaseOrders(this IServiceCollection services)
    {
        services.AddScoped<PurchaseOrderEventPublisher>();
        services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
        return services;
    }
}
