using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Services;

public class CatalogRegistrationTests
{
    [Fact]
    public async Task Catalog_services_resolve_from_the_real_app_container_and_work_against_the_migrated_database()
    {
        using var factory = new AppFactory();
        using var _ = factory.CreateClient(); // starts the host (migrations run on startup)

        await factory.WithScopeAsync(async sp =>
        {
            var categories = sp.GetRequiredService<ICategoryService>();
            var products = sp.GetRequiredService<IProductService>();

            var category = await categories.CreateAsync(new CategoryInput("Registered", null));
            Assert.True(category.Succeeded);

            var product = await products.CreateAsync(new ProductInput("REG-1", "Registered product", null, category.Value.Id, null, null, null, 0, 0, 0));
            Assert.True(product.Succeeded);
            Assert.Equal("REG-1", (await products.FindByCodeAsync("reg-1"))!.Sku);
        });
    }
}
