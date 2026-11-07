using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InventoryTracker.Data.Interceptors;

/// <summary>Applies the SQLite pragmas required by the app each time a connection is opened (architecture §7).</summary>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string PragmaSql =
        "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Applies the pragmas to an already-open connection (e.g. a shared in-memory test connection).</summary>
    public static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        command.ExecuteNonQuery();
    }
}
