namespace InventoryTracker.Services;

public static class CatalogServiceCollectionExtensions
{
    /// <summary>Registers the product catalog services (scoped: they share the request's DbContext).</summary>
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<ICategoryService, CategoryService>();
        return services;
    }
}
