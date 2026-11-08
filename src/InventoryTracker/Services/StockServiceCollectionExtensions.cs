namespace InventoryTracker.Services;

public static class StockServiceCollectionExtensions
{
    /// <summary>Registers the stock ledger service (scoped: shares the request's DbContext and transaction).</summary>
    public static IServiceCollection AddStock(this IServiceCollection services)
    {
        services.AddScoped<IStockService, StockService>();
        return services;
    }
}
