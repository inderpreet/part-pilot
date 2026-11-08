using System.Runtime.CompilerServices;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InventoryTracker.Data.Interceptors;

/// <summary>
/// A process-wide counter that goes up whenever something the low-stock figures depend on has been saved (stock, parts,
/// purchase orders). Caches remember the value they were computed at and recompute when it moved, so the nav badge never
/// waits out its full minute after a stock movement or an order change. A bare number: sharing <see cref="Shared"/> between
/// hosts (tests run many in one process) only causes harmless extra recomputation, and tests that need isolation make their own.
/// </summary>
public sealed class ReportingChangeSignal
{
    private long _version;

    /// <summary>The instance the application uses.</summary>
    public static ReportingChangeSignal Shared { get; } = new();

    public long Version => Interlocked.Read(ref _version);

    public void Bump() => Interlocked.Increment(ref _version);
}

/// <summary>Bumps a <see cref="ReportingChangeSignal"/> after a save that touched parts, stock transactions or purchase orders.</summary>
public class ReportingChangeInterceptor(ReportingChangeSignal? signal = null) : SaveChangesInterceptor
{
    private readonly ReportingChangeSignal _signal = signal ?? ReportingChangeSignal.Shared;
    private readonly ConditionalWeakTable<DbContext, object> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Mark(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Mark(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Signal(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Signal(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    private void Mark(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var relevant = context.ChangeTracker.Entries().Any(e =>
            e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && e.Entity is Product or StockTransaction or PurchaseOrder or PurchaseOrderLine);
        if (relevant)
        {
            _pending.AddOrUpdate(context, this);
        }
    }

    private void Signal(DbContext? context)
    {
        if (context is not null && _pending.Remove(context))
        {
            _signal.Bump();
        }
    }

    private void Forget(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }
}
