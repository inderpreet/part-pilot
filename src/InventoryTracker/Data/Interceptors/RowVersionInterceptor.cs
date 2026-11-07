using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InventoryTracker.Data.Interceptors;

/// <summary>
/// Increments <see cref="IHasRowVersion.RowVersion"/> on every insert/update. The original value stays as loaded,
/// so EF's <c>WHERE RowVersion = @original</c> check detects concurrent edits.
/// </summary>
public class RowVersionInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Bump(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Bump(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Bump(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<IHasRowVersion>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.RowVersion = 1;
                    break;
                case EntityState.Modified:
                    entry.Entity.RowVersion = entry.Property(e => e.RowVersion).OriginalValue + 1;
                    break;
            }
        }
    }
}
