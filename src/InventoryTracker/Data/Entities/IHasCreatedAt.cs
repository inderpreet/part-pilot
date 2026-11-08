namespace InventoryTracker.Data.Entities;

/// <summary>
/// Entity that is only ever inserted (an append-only ledger row): it has a UTC creation time set by
/// <c>TimestampInterceptor</c> but no <c>UpdatedAt</c>. Mutable entities implement <see cref="IHasTimestamps"/> instead.
/// </summary>
public interface IHasCreatedAt
{
    DateTime CreatedAt { get; set; }
}
