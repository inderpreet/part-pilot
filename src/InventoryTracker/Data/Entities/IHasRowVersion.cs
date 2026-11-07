namespace InventoryTracker.Data.Entities;

/// <summary>
/// Entity with an optimistic-concurrency counter. SQLite has no rowversion type, so
/// <c>RowVersionInterceptor</c> increments it on every save and it is configured as a concurrency token.
/// </summary>
public interface IHasRowVersion
{
    long RowVersion { get; set; }
}
