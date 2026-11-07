namespace InventoryTracker.Data.Entities;

/// <summary>Entity with UTC creation/update timestamps maintained by <c>TimestampInterceptor</c>.</summary>
public interface IHasTimestamps
{
    DateTime CreatedAt { get; set; }

    DateTime UpdatedAt { get; set; }
}
