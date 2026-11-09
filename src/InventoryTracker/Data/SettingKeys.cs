namespace InventoryTracker.Data;

/// <summary>Well-known <c>AppSetting</c> keys and their seeded defaults (F00 FR-6).</summary>
public static class SettingKeys
{
    public const string CompanyName = "Company.Name";
    public const string CurrencyCode = "Currency.Code";
    public const string CurrencySymbol = "Currency.Symbol";
    public const string StockAllowNegative = "Stock.AllowNegative";
    public const string PurchaseOrderNumberPrefix = "PurchaseOrder.NumberPrefix";
    public const string ProjectNumberPrefix = "Project.NumberPrefix";
    public const string RequisitionNumberPrefix = "Requisition.NumberPrefix";

    /// <summary>
    /// The key of a data permission for a role (F07 FR-9): <c>Permission.ImportData.Staff</c>. Admin has no key because
    /// an Admin always holds every permission.
    /// </summary>
    public static string Permission(DataPermission permission, string role)
        => $"Permission.{permission}.{role}";

    /// <summary>Default values seeded by the migrations.</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = BuildDefaults();

    private static Dictionary<string, string> BuildDefaults()
    {
        var defaults = new Dictionary<string, string>
        {
            [CompanyName] = "My Company",
            [CurrencyCode] = "USD",
            [CurrencySymbol] = "$",
            [StockAllowNegative] = "false",
            [PurchaseOrderNumberPrefix] = "PO-",
            [ProjectNumberPrefix] = "PRJ-",
            [RequisitionNumberPrefix] = "REQ-",
        };

        foreach (var permission in Enum.GetValues<DataPermission>())
        {
            foreach (var role in DataPermissions.ConfigurableRoles)
            {
                defaults[Permission(permission, role)] = DataPermissions.DefaultAllowed(permission, role) ? "true" : "false";
            }
        }

        return defaults;
    }
}

/// <summary>The four things an Admin can allow or deny a role (F07 FR-9). Backup is not one of them: it is always Admin only.</summary>
public enum DataPermission
{
    /// <summary>Import parts, suppliers, part-supplier links and opening stock.</summary>
    ImportData,

    /// <summary>Import BOMs (projects with their parts).</summary>
    ImportBoms,

    /// <summary>Export parts, suppliers, links and stock transactions.</summary>
    ExportData,

    /// <summary>Export BOMs.</summary>
    ExportBoms,
}

public static class DataPermissions
{
    /// <summary>The roles an Admin can grant permissions to. Admin always has every permission.</summary>
    public static IReadOnlyList<string> ConfigurableRoles { get; } = [Roles.Staff, Roles.Viewer];

    /// <summary>Staff may export by default; nothing else is allowed until an Admin says so.</summary>
    public static bool DefaultAllowed(DataPermission permission, string role)
        => role == Roles.Staff && permission is DataPermission.ExportData or DataPermission.ExportBoms;
}
