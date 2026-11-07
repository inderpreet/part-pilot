using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface ICategoryService
{
    /// <summary>All categories by name with their product counts; pass <c>false</c> to hide archived ones (for pickers/filters).</summary>
    Task<IReadOnlyList<CategoryListItem>> ListAsync(bool includeArchived = true, CancellationToken cancellationToken = default);

    Task<CategoryListItem?> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<Result<CategoryListItem>> CreateAsync(CategoryInput input, CancellationToken cancellationToken = default);

    Task<Result<CategoryListItem>> UpdateAsync(int id, CategoryInput input, CancellationToken cancellationToken = default);

    /// <summary>Archives or restores a category. Archiving leaves it assigned to its products. Idempotent.</summary>
    Task<Result> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);
}

public sealed class CategoryService(AppDbContext db) : ICategoryService
{
    public async Task<IReadOnlyList<CategoryListItem>> ListAsync(bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        var categories = db.Categories.AsNoTracking().AsQueryable();
        if (!includeArchived)
        {
            categories = categories.Where(c => c.IsActive);
        }

        return await categories
            .OrderBy(c => c.Name)
            .Select(c => new CategoryListItem(c.Id, c.Name, c.Description, c.IsActive, c.Products.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<CategoryListItem?> GetAsync(int id, CancellationToken cancellationToken = default) =>
        await db.Categories.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CategoryListItem(c.Id, c.Name, c.Description, c.IsActive, c.Products.Count))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Result<CategoryListItem>> CreateAsync(CategoryInput input, CancellationToken cancellationToken = default)
    {
        var errors = await ValidateAsync(input, excludeId: 0, cancellationToken);
        if (errors.Count > 0)
        {
            return Result.Failure<CategoryListItem>([.. errors]);
        }

        var category = new Category { Name = Clean(input.Name)!, Description = Clean(input.Description) };
        db.Categories.Add(category);

        if (await SaveAsync(category.Name, cancellationToken) is { } error)
        {
            return Result.Failure<CategoryListItem>(error);
        }

        return Result.Success((await GetAsync(category.Id, cancellationToken))!);
    }

    public async Task<Result<CategoryListItem>> UpdateAsync(int id, CategoryInput input, CancellationToken cancellationToken = default)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return Result.Failure<CategoryListItem>("category.not_found", "That category no longer exists.");
        }

        var errors = await ValidateAsync(input, id, cancellationToken);
        if (errors.Count > 0)
        {
            return Result.Failure<CategoryListItem>([.. errors]);
        }

        category.Name = Clean(input.Name)!;
        category.Description = Clean(input.Description);

        if (await SaveAsync(category.Name, cancellationToken) is { } error)
        {
            return Result.Failure<CategoryListItem>(error);
        }

        return Result.Success((await GetAsync(id, cancellationToken))!);
    }

    public async Task<Result> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return Result.Failure("category.not_found", "That category no longer exists.");
        }

        if (category.IsActive != isActive)
        {
            category.IsActive = isActive;
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private async Task<List<Error>> ValidateAsync(CategoryInput input, int excludeId, CancellationToken cancellationToken)
    {
        var errors = new List<Error>();

        var name = Clean(input.Name);
        if (name is null)
        {
            errors.Add(new Error("category.name_required", "Name is required.", CategoryFields.Name));
        }
        else if (name.Length > Category.NameMaxLength)
        {
            errors.Add(new Error("category.name_too_long", $"Name can be at most {Category.NameMaxLength} characters.", CategoryFields.Name));
        }
        else if (await db.Categories.AnyAsync(c => c.Name == name && c.Id != excludeId, cancellationToken))
        {
            errors.Add(NameTaken(name));
        }

        if (Clean(input.Description) is { } description && description.Length > Category.DescriptionMaxLength)
        {
            errors.Add(new Error("category.description_too_long", $"Description can be at most {Category.DescriptionMaxLength} characters.", CategoryFields.Description));
        }

        return errors;
    }

    private static Error NameTaken(string name) =>
        new("category.name_taken", $"A category named '{name}' already exists (it may be archived).", CategoryFields.Name);

    private async Task<Error?> SaveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, out var target)
                                           && target.Contains("Categories.Name", StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return NameTaken(name);
        }
    }
}
