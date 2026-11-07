using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

/// <summary>Recognises SQLite constraint failures so services can turn races into field errors.</summary>
public static class DbErrors
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE (extended result code).</summary>
    private const int ConstraintUnique = 2067;

    /// <summary>
    /// True when <paramref name="exception"/> is a unique-index violation; <paramref name="target"/> is the offending
    /// <c>Table.Column</c> (e.g. <c>Products.Sku</c>) taken from SQLite's message.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException exception, out string target)
    {
        target = string.Empty;
        if (exception.InnerException is not SqliteException { SqliteExtendedErrorCode: ConstraintUnique } sqlite)
        {
            return false;
        }

        // "SQLite Error 19: 'UNIQUE constraint failed: Products.Sku'."
        const string marker = "UNIQUE constraint failed:";
        var index = sqlite.Message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            target = sqlite.Message[(index + marker.Length)..].Trim().Trim('\'', '.', ' ');
        }

        return true;
    }
}
