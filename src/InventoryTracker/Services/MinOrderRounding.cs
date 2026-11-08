namespace InventoryTracker.Services;

/// <summary>
/// The "raise to the supplier's minimum order quantity" rule, in one place. Used by BOM shortage orders (F10) and the
/// suggested reorder quantity (F06 FR-5).
/// </summary>
public static class MinOrderRounding
{
    /// <summary>
    /// <paramref name="quantity"/> raised to <paramref name="minOrderQuantity"/> when that is set (above 1) and larger; otherwise unchanged.
    /// A quantity of zero or less stays as it is: nothing to order.
    /// </summary>
    public static int RoundUp(int quantity, int? minOrderQuantity)
        => quantity > 0 && minOrderQuantity is > 1 && quantity < minOrderQuantity.Value ? minOrderQuantity.Value : quantity;
}
