namespace InventoryTracker.Services;

public static class RequisitionServiceCollectionExtensions
{
    /// <summary>Registers the requisition and requisition-ordering services and their event publisher (scoped: share the request's DbContext and transaction).</summary>
    public static IServiceCollection AddRequisitions(this IServiceCollection services)
    {
        services.AddScoped<RequisitionEventPublisher>();
        services.AddScoped<IRequisitionService, RequisitionService>();
        services.AddScoped<IRequisitionOrderService, RequisitionOrderService>();
        return services;
    }
}
