namespace InventoryTracker.Services;

public static class RequisitionServiceCollectionExtensions
{
    /// <summary>Registers the requisition service and its event publisher (scoped: share the request's DbContext and transaction).</summary>
    public static IServiceCollection AddRequisitions(this IServiceCollection services)
    {
        services.AddScoped<RequisitionEventPublisher>();
        services.AddScoped<IRequisitionService, RequisitionService>();
        return services;
    }
}
