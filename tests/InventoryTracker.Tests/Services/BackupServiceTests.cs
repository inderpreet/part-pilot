using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

public sealed class BackupServiceTests : ImportTestBase
{
    [Fact]
    public async Task Snapshot_opens_as_sqlite_and_has_the_same_row_counts()
    {
        await CreateProductAsync(Input(sku: "B-1", name: "Backed"));

        var (context, _, _, backup) = NewTransfer();
        await using (context)
        {
            using var snapshot = await backup.CreateSnapshotAsync();
            Assert.True(File.Exists(snapshot.Path));
            Assert.StartsWith("inventory-20260615", snapshot.DownloadName);

            await using var copy = new SqliteConnection($"Data Source={snapshot.Path};Pooling=False");
            await copy.OpenAsync();

            var source = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync();
            var expected = await RowCountsAsync(source);
            Assert.Contains("Products", expected.Keys);
            Assert.Equal(1L, expected["Products"]);
            Assert.Equal(expected, await RowCountsAsync(copy));
        }
    }

    private static async Task<SortedDictionary<string, long>> RowCountsAsync(System.Data.Common.DbConnection connection)
    {
        var tables = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            counts[table] = (long)(await count.ExecuteScalarAsync())!;
        }

        return counts;
    }
}
