using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

public class CategoryServiceTests : CatalogTestBase
{
    [Fact]
    public async Task Create_trims_and_stores_the_category()
    {
        var (context, _, categories) = NewRequest();
        await using var _ = context;

        var result = await categories.CreateAsync(new CategoryInput("  Hardware  ", "  Nuts and bolts  "));

        Assert.True(result.Succeeded);
        Assert.Equal("Hardware", result.Value.Name);
        Assert.Equal("Nuts and bolts", result.Value.Description);
        Assert.True(result.Value.IsActive);
        Assert.Equal(0, result.Value.ProductCount);
    }

    [Fact]
    public async Task Blank_description_is_stored_as_null()
    {
        var (context, _, categories) = NewRequest();
        await using var _ = context;

        var result = await categories.CreateAsync(new CategoryInput("Plain", "   "));

        Assert.Null(result.Value.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Name_is_required(string? name)
    {
        var (context, _, categories) = NewRequest();
        await using var _ = context;

        var result = await categories.CreateAsync(new CategoryInput(name, null));

        var error = Assert.Single(result.Errors);
        Assert.Equal("category.name_required", error.Code);
        Assert.Equal("Name", error.Field);
        Assert.Equal(0, await context.Categories.CountAsync());
    }

    [Fact]
    public async Task Lengths_are_enforced_at_the_limit()
    {
        var (context, _, categories) = NewRequest();
        await using var _ = context;

        var tooLong = await categories.CreateAsync(new CategoryInput(new string('n', 101), new string('d', 501)));
        Assert.Equivalent(new[] { "Name", "Description" }, tooLong.Errors.Select(e => e.Field).ToArray());

        var exact = await categories.CreateAsync(new CategoryInput(new string('n', 100), new string('d', 500)));
        Assert.True(exact.Succeeded);
    }

    [Fact]
    public async Task Duplicate_name_is_rejected_case_insensitively_including_archived_categories()
    {
        var existing = await CreateCategoryAsync("Hardware");
        var (context, _, categories) = NewRequest();
        await using var _ = context;

        var duplicate = await categories.CreateAsync(new CategoryInput("HARDWARE", null));
        Assert.Equal("category.name_taken", Assert.Single(duplicate.Errors).Code);
        Assert.Equal("Name", duplicate.Errors[0].Field);

        await categories.SetActiveAsync(existing.Id, false);
        var againArchived = await categories.CreateAsync(new CategoryInput("hardware", null));
        Assert.Equal("category.name_taken", Assert.Single(againArchived.Errors).Code);
    }

    [Fact]
    public async Task Losing_a_unique_race_is_reported_as_a_field_error()
    {
        var race = new RunSqlBeforeSave("INSERT INTO Categories (Name, IsActive, CreatedAt, UpdatedAt) VALUES ('Racing', 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        var (context, _, categories) = NewRequest(race);
        await using var _ = context;

        var result = await categories.CreateAsync(new CategoryInput("racing", null));

        var error = Assert.Single(result.Errors);
        Assert.Equal("category.name_taken", error.Code);
        Assert.Equal("Name", error.Field);
    }

    [Fact]
    public async Task Update_changes_name_and_description_and_may_keep_its_own_name()
    {
        var created = await CreateCategoryAsync("Tools");
        var (context, _, categories) = NewRequest();
        await using var _ = context;

        var sameName = await categories.UpdateAsync(created.Id, new CategoryInput("TOOLS", "Hand tools"));
        Assert.True(sameName.Succeeded);
        Assert.Equal("TOOLS", sameName.Value.Name);
        Assert.Equal("Hand tools", sameName.Value.Description);

        var renamed = await categories.UpdateAsync(created.Id, new CategoryInput("Power tools", null));
        Assert.Equal("Power tools", renamed.Value.Name);
        Assert.Null(renamed.Value.Description);
    }

    [Fact]
    public async Task Update_cannot_take_another_categorys_name()
    {
        await CreateCategoryAsync("One");
        var two = await CreateCategoryAsync("Two");
        var (context, _, categories) = NewRequest();
        await using var _ = context;

        var result = await categories.UpdateAsync(two.Id, new CategoryInput("one", null));

        Assert.Equal("category.name_taken", Assert.Single(result.Errors).Code);
        Assert.Equal("Two", (await categories.GetAsync(two.Id))!.Name);
    }

    [Fact]
    public async Task Update_of_a_missing_category_reports_not_found()
    {
        var (context, _, categories) = NewRequest();
        await using var _ = context;

        Assert.Equal("category.not_found", Assert.Single((await categories.UpdateAsync(404, new CategoryInput("X", null))).Errors).Code);
        Assert.Equal("category.not_found", Assert.Single((await categories.SetActiveAsync(404, false)).Errors).Code);
        Assert.Null(await categories.GetAsync(404));
    }

    [Fact]
    public async Task List_is_ordered_by_name_with_product_counts_and_can_hide_archived()
    {
        var zebra = await CreateCategoryAsync("zebra");
        var apple = await CreateCategoryAsync("Apple");
        var mango = await CreateCategoryAsync("Mango");
        await CreateProductAsync(Input(sku: "1", categoryId: apple.Id));
        await CreateProductAsync(Input(sku: "2", categoryId: apple.Id));
        await CreateProductAsync(Input(sku: "3", categoryId: zebra.Id));
        var (context, _, categories) = NewRequest();
        await using var _ = context;
        await categories.SetActiveAsync(mango.Id, false);

        var all = await categories.ListAsync();
        Assert.Equal(["Apple", "Mango", "zebra"], all.Select(c => c.Name));
        Assert.Equal([2, 0, 1], all.Select(c => c.ProductCount));
        Assert.Equal([true, false, true], all.Select(c => c.IsActive));

        var active = await categories.ListAsync(includeArchived: false);
        Assert.Equal(["Apple", "zebra"], active.Select(c => c.Name));
    }

    [Fact]
    public async Task Archiving_a_category_leaves_it_assigned_and_restoring_works()
    {
        var category = await CreateCategoryAsync("Seasonal");
        var product = await CreateProductAsync(Input(categoryId: category.Id));
        var (context, products, categories) = NewRequest();
        await using var _ = context;

        Assert.True((await categories.SetActiveAsync(category.Id, false)).Succeeded);
        Assert.True((await categories.SetActiveAsync(category.Id, false)).Succeeded); // idempotent

        var archived = (await categories.GetAsync(category.Id))!;
        Assert.False(archived.IsActive);
        Assert.Equal(1, archived.ProductCount);
        Assert.Equal(category.Id, (await products.GetAsync(product.Id))!.CategoryId);

        Assert.True((await categories.SetActiveAsync(category.Id, true)).Succeeded);
        Assert.True((await categories.GetAsync(category.Id))!.IsActive);
    }
}
