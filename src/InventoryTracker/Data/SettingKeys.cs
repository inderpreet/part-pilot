namespace InventoryTracker.Data;

/// <summary>Well-known <c>AppSetting</c> keys and their seeded defaults (F00 FR-6).</summary>
public static class SettingKeys
{
    public const string CompanyName = "Company.Name";
    public const string CurrencyCode = "Currency.Code";
    public const string CurrencySymbol = "Currency.Symbol";
    public const string StockAllowNegative = "Stock.AllowNegative";
    public const string PurchaseOrderNumberPrefix = "PurchaseOrder.NumberPrefix";

    /// <summary>Default values seeded by the initial migration.</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        [CompanyName] = "My Company",
        [CurrencyCode] = "USD",
        [CurrencySymbol] = "$",
        [StockAllowNegative] = "false",
        [PurchaseOrderNumberPrefix] = "PO-",
    };
}
