using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>
/// Real file database, one connection per "request", configured exactly as in production (WAL, busy timeout, BEGIN IMMEDIATE):
/// concurrent posting must never lose an update or break the cache = ledger invariant.
/// </summary>
public sealed class StockConcurrencyTests : IAsyncLifetime
{
    private string _path = null!;
    private string _userId = null!;
    private int _productId;
    private readonly FakeSettings _settings = new();
    private readonly TestClock _clock = new(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc));

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"inventory-stock-{Guid.NewGuid():N}.db");
        await using var context = NewContext();
        await context.Database.MigrateAsync();

        var user = new ApplicationUser { UserName = "racer", DisplayName = "Race R" };
        var product = new Product { Sku = "RACE-1", Name = "Racing widget" };
        context.Users.Add(user);
        context.Products.Add(product);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _productId = product.Id;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    private AppDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseInventorySqlite($"Data Source={_path}");
        return new AppDbContext(builder.Options);
    }

    private async Task<Result<IReadOnlyList<PostedMovement>>> PostAsync(params StockMovement[] lines)
    {
        await using var context = NewContext();
        return await new StockService(context, _settings, _clock, NullLogger<StockService>.Instance).PostAsync(lines, new StockContext(_userId));
    }

    private static async Task<T[]> RunConcurrentlyAsync<T>(int workers, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await work(i);
        })).ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Concurrent_posting_from_many_connections_keeps_the_cache_equal_to_the_ledger()
    {
        _settings.AllowNegative = true;
        const int workers = 8;
        const int opsPerWorker = 25;

        var failures = await RunConcurrentlyAsync(workers, async worker =>
        {
            var failed = new List<string>();
            for (var i = 0; i < opsPerWorker; i++)
            {
                // Mix of single lines, two-line batches and a reversal of the worker's own previous receipt.
                var result = (i % 5) switch
                {
                    0 => await PostAsync(new StockMovement(_productId, StockTransactionType.Receive, 3)),
                    1 => await PostAsync(new StockMovement(_productId, StockTransactionType.Issue, 1)),
                    2 => await PostAsync(
                        new StockMovement(_productId, StockTransactionType.Receive, 2),
                        new StockMovement(_productId, StockTransactionType.Issue, 4)),
                    3 => await PostAsync(new StockMovement(_productId, StockTransactionType.Adjust, -2, Reason: "w" + worker)),
                    _ => await PostAsync(new StockMovement(_productId, StockTransactionType.Count, (worker * 7) + i)),
                };

                if (!result.Succeeded)
                {
                    failed.Add(string.Join("; ", result.Errors.Select(e => e.Message)));
                }
            }

            return failed;
        });

        Assert.All(failures, f => Assert.Empty(f));

        await using var context = NewContext();
        var rows = await context.StockTransactions.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        Assert.True(rows.Count >= workers * opsPerWorker, $"Expected at least {workers * opsPerWorker} rows, found {rows.Count}.");

        var running = 0;
        foreach (var row in rows)
        {
            running += row.QuantityDelta;
            Assert.Equal(running, row.QuantityAfter);
        }

        Assert.Equal(running, await context.Products.Where(p => p.Id == _productId).Select(p => p.QuantityOnHand).SingleAsync());
    }

    [Fact]
    public async Task Only_one_of_several_simultaneous_issues_of_the_last_unit_succeeds()
    {
        Assert.True((await PostAsync(new StockMovement(_productId, StockTransactionType.Receive, 1))).Succeeded);

        var results = await RunConcurrentlyAsync(10, _ => PostAsync(new StockMovement(_productId, StockTransactionType.Issue, 1)));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal("stock.insufficient", Assert.Single(r.Errors).Code));

        await using var context = NewContext();
        Assert.Equal(0, await context.Products.Where(p => p.Id == _productId).Select(p => p.QuantityOnHand).SingleAsync());
        Assert.Equal(2, await context.StockTransactions.CountAsync());
    }

    [Fact]
    public async Task Concurrent_reversals_of_one_transaction_create_exactly_one_reversal()
    {
        var original = (await PostAsync(new StockMovement(_productId, StockTransactionType.Receive, 5))).Value[0].TransactionId!.Value;

        var results = await RunConcurrentlyAsync(6, async _ =>
        {
            await using var context = NewContext();
            return await new StockService(context, _settings, _clock, NullLogger<StockService>.Instance).ReverseAsync(original, new StockContext(_userId));
        });

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal("stock.already_reversed", Assert.Single(r.Errors).Code));

        await using var check = NewContext();
        Assert.Equal(0, await check.Products.Where(p => p.Id == _productId).Select(p => p.QuantityOnHand).SingleAsync());
        Assert.Equal(1, await check.StockTransactions.CountAsync(t => t.Type == StockTransactionType.Reversal));
    }
}

/// <summary>Architecture invariant 1: nothing but <see cref="StockService"/> writes <c>QuantityOnHand</c>.</summary>
public sealed partial class StockInvariantSourceTests
{
    // Assignment, compound assignment, ++/--, EF SetProperty, or raw SQL "QuantityOnHand =".
    [GeneratedRegex(@"QuantityOnHand\s*(=(?![=>])|\+=|-=|\+\+|--)|SetProperty\s*\([^,)]*QuantityOnHand", RegexOptions.CultureInvariant)]
    private static partial Regex Write();

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root from " + thisFile);
    }

    [Fact]
    public void No_production_code_outside_the_stock_service_assigns_quantity_on_hand()
    {
        var source = Path.Combine(RepoRoot(), "src", "InventoryTracker");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".cshtml", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.StartsWith("Data/Migrations/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative == "Services/StockService.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Write().IsMatch(lines[i]))
                {
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "QuantityOnHand may only be written by StockService:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void The_scan_does_catch_the_ways_a_write_could_look()
    {
        foreach (var bad in new[]
                 {
                     "product.QuantityOnHand = 5;",
                     "p.QuantityOnHand += delta;",
                     "p.QuantityOnHand--;",
                     "s.SetProperty(p => p.QuantityOnHand, 3)",
                     "UPDATE Products SET QuantityOnHand = 0",
                     "new Product { QuantityOnHand = 4 }",
                 })
        {
            Assert.True(Write().IsMatch(bad), bad);
        }

        foreach (var fine in new[]
                 {
                     "p.QuantityOnHand == 0",
                     "x => x.QuantityOnHand >= y",
                     "p.QuantityOnHand <= p.ReorderPoint",
                     "public int QuantityOnHand { get; set; }",
                     "Cached = p.QuantityOnHand,",
                 })
        {
            // "Cached = p.QuantityOnHand," has "=" before the name, which is not an assignment to it.
            Assert.False(Write().IsMatch(fine), fine);
        }
    }
}
