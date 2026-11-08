using InventoryTracker.Data;
using InventoryTracker.Data.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

/// <summary>The low-stock count behind the nav badge (F06 FR-3), cached so it costs nothing on most page views.</summary>
public interface ILowStockCounter
{
    /// <summary>The number of low-stock products. At most <see cref="LowStockCounter.CacheSeconds"/> old, and recomputed sooner after stock, part or order changes.</summary>
    Task<int> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the cached value; the next <see cref="GetAsync"/> counts again.</summary>
    void Invalidate();
}

/// <summary>
/// Singleton cache of <c>ReportingQueries.LowStock().Count()</c>. It also watches a <see cref="ReportingChangeSignal"/>, so a
/// saved stock movement or purchase order change shows in the badge on the next page rather than a minute later.
/// </summary>
public sealed class LowStockCounter(IServiceScopeFactory scopes, IClock clock, ReportingChangeSignal? signal = null) : ILowStockCounter
{
    public const int CacheSeconds = 60;

    private readonly ReportingChangeSignal _signal = signal ?? ReportingChangeSignal.Shared;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (int Count, DateTime At, long Version)? _cached;

    public async Task<int> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Fresh() is { } hit)
        {
            return hit;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh() is { } again)
            {
                return again;
            }

            // Read the version first: a change that lands while counting makes the stored value stale on the next call.
            var version = _signal.Version;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var count = await db.LowStock().CountAsync(cancellationToken);
            _cached = (count, clock.UtcNow, version);
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _signal.Bump();

    private int? Fresh()
        => _cached is { } c && c.Version == _signal.Version && (clock.UtcNow - c.At).TotalSeconds < CacheSeconds
            ? c.Count
            : null;
}
