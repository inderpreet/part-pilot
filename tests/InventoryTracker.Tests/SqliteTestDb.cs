using InventoryTracker.Data;
using InventoryTracker.Data.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests;

/// <summary>
/// Real SQLite in-memory database kept alive by one open connection, with migrations applied.
/// Each test class instance gets its own isolated database.
/// </summary>
public sealed class SqliteTestDb : IAsyncDisposable
{
    private SqliteTestDb(SqliteConnection connection) => Connection = connection;

    public SqliteConnection Connection { get; }

    public static async Task<SqliteTestDb> CreateAsync(TimeProvider? timeProvider = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        // EF only fires connection-opened interceptors for connections it opens itself.
        SqlitePragmaInterceptor.Apply(connection);

        var db = new SqliteTestDb(connection);
        await using var context = db.CreateContext(timeProvider);
        await context.Database.MigrateAsync();
        return db;
    }

    public AppDbContext CreateContext(TimeProvider? timeProvider = null)
        => new(Configure(new DbContextOptionsBuilder<AppDbContext>(), timeProvider).Options);

    /// <summary>Builds options for <typeparamref name="TContext"/> against this database (for test-only contexts).</summary>
    public DbContextOptions<TContext> CreateOptions<TContext>(TimeProvider? timeProvider = null)
        where TContext : DbContext
        => Configure(new DbContextOptionsBuilder<TContext>(), timeProvider).Options;

    private DbContextOptionsBuilder<T> Configure<T>(DbContextOptionsBuilder<T> builder, TimeProvider? timeProvider)
        where T : DbContext
    {
        builder.UseSqlite(Connection)
            .AddInterceptors(new TimestampInterceptor(timeProvider), new RowVersionInterceptor());
        return builder;
    }

    /// <summary>Runs a non-composable scalar query (e.g. a PRAGMA) on the context's connection.</summary>
    public static async Task<object?> ScalarAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await context.Database.OpenConnectionAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
