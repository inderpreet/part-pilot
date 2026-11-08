using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace InventoryTracker.Tests.Data;

/// <summary>A fact that only runs when <c>INVENTORY_BENCH=1</c>: it builds a 1M-row database, which is far too slow for every test run.</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "INVENTORY_BENCH";

    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Set {EnvironmentVariable}=1 to run the F06 query benchmarks (seeds 10k products and 1M stock transactions).";
        }
    }
}

/// <summary>
/// WI-06-1: times every dashboard and report query shape on the demo data set (10k products, 200 suppliers, 2k purchase
/// orders, 1M ledger rows) and records the SQLite query plans. Run it with
/// <c>$env:INVENTORY_BENCH = 1; dotnet test --filter ReportingBenchmarks</c>; the table is written to the test output and to
/// <c>%TEMP%\f06-benchmark.md</c>. Without indexes beyond the F00-F05 ones SQLite plans from heuristics only (the app
/// never runs ANALYZE), so this measures the worst realistic case.
/// </summary>
public sealed class ReportingBenchmarks(ITestOutputHelper output)
{
    private const int DashboardBudgetMs = 100;
    private const int ReportPageBudgetMs = 300;
    private const int Runs = 5;

    private sealed record Case(string Name, bool IsReport, Func<AppDbContext, IQueryable?>? Plan, Func<AppDbContext, Task> Run);

    [BenchFact]
    public async Task Every_dashboard_query_is_under_100_ms_and_every_report_first_page_under_300_ms()
    {
        var path = Path.Combine(Path.GetTempPath(), $"f06-bench-{Guid.NewGuid():N}.db");
        try
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            builder.UseInventorySqlite($"Data Source={path}").UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            var options = builder.Options;

            await using var context = new AppDbContext(options);
            await context.Database.MigrateAsync();
            context.Users.Add(new ApplicationUser { UserName = "bench", DisplayName = "Bench" });
            await context.SaveChangesAsync();

            var seedTime = Stopwatch.StartNew();
            var seeded = await new DevSeeder(context).SeedAsync();
            output.WriteLine($"Seeded {seeded.Products} products, {seeded.Suppliers} suppliers, {seeded.PurchaseOrders} POs, {seeded.Transactions} transactions in {seedTime.Elapsed.TotalSeconds:F1}s");
            Assert.True(seeded.Seeded, seeded.Message);

            var report = new StringBuilder();
            report.AppendLine("| Query | Kind | Median ms | Max ms | Budget ms |");
            report.AppendLine("|---|---|---:|---:|---:|");
            var plans = new StringBuilder();
            var over = new List<string>();

            foreach (var c in Cases())
            {
                await c.Run(context); // warm-up (query compilation, page cache)
                var times = new List<double>();
                for (var i = 0; i < Runs; i++)
                {
                    var sw = Stopwatch.StartNew();
                    await c.Run(context);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                times.Sort();
                var median = times[times.Count / 2];
                var budget = c.IsReport ? ReportPageBudgetMs : DashboardBudgetMs;
                report.AppendLine($"| {c.Name} | {(c.IsReport ? "report page" : "dashboard")} | {median:F1} | {times[^1]:F1} | {budget} |");
                if (median > budget)
                {
                    over.Add($"{c.Name}: {median:F1} ms > {budget} ms");
                }

                if (c.Plan?.Invoke(context) is { } query)
                {
                    plans.AppendLine($"### {c.Name}");
                    plans.AppendLine("```");
                    plans.AppendLine(await ExplainAsync(context, query));
                    plans.AppendLine("```");
                }
            }

            var text = report + Environment.NewLine + plans;
            output.WriteLine(text);
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "f06-benchmark.md"), text);

            Assert.True(over.Count == 0, "Over budget: " + string.Join("; ", over));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }

    private static IEnumerable<Case> Cases()
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var weekEnd = today.AddDays(7);
        var month = now.AddDays(-30);
        var quarter = now.AddDays(-90);
        var year = DateOnly.FromDateTime(now.AddDays(-365));
        var deadCutoff = now.AddDays(-90);
        var open = new[] { PurchaseOrderStatus.Submitted, PurchaseOrderStatus.PartiallyReceived };

        // Dashboard (FR-1, FR-2, FR-3)
        yield return new("Nav badge / tile: low-stock count", false, db => db.LowStock(), async db => await db.LowStock().CountAsync());
        yield return new("Tile: active SKUs", false, db => db.Products.Where(p => p.IsActive), async db => await db.Products.CountAsync(p => p.IsActive));
        yield return new("Tile: total stock value", false, null, async db => await db.Products.Where(p => p.IsActive).SumAsync(p => (long)p.QuantityOnHand * p.UnitCostCents));
        yield return new("Tile: out-of-stock count", false, db => db.Products.Where(p => p.IsActive && p.QuantityOnHand <= 0), async db => await db.Products.CountAsync(p => p.IsActive && p.QuantityOnHand <= 0));
        yield return new("Tile: open POs (count)", false, db => db.PurchaseOrders.Where(p => open.Contains(p.Status)), async db => await db.PurchaseOrders.CountAsync(p => open.Contains(p.Status)));
        yield return new("Tile: open POs (value)", false, null, async db => await db.PurchaseOrderLines.Where(l => open.Contains(l.PurchaseOrder!.Status)).SumAsync(l => (long)l.QuantityOrdered * l.UnitCostCents));
        yield return new("Tile: overdue POs", false, db => db.PurchaseOrders.Where(p => open.Contains(p.Status) && p.ExpectedDate < today),
            async db => await db.PurchaseOrders.CountAsync(p => open.Contains(p.Status) && p.ExpectedDate < today));
        yield return new("List: top 10 low-stock", false, db => LowestFirst(db).Take(10), async db => await LowestFirst(db).Take(10).ToListAsync());
        yield return new("List: last 10 movements", false, db => LatestMovements(db), async db => await LatestMovements(db).ToListAsync());
        yield return new("List: open POs due this week", false, db => DueThisWeek(db, open, today, weekEnd), async db => await DueThisWeek(db, open, today, weekEnd).ToListAsync());
        yield return new("On-order aggregate", false, db => db.OnOrder(), async db => await db.OnOrder().ToListAsync());

        // Reports and the low-stock page, first page (rows + total, as the screen needs both)
        yield return new("Low-stock page (50 + count)", true, db => db.LowStock().OrderBy(r => r.Name).Take(50),
            async db => { await db.LowStock().CountAsync(); await db.LowStock().OrderBy(r => r.Name).Take(50).ToListAsync(); });
        yield return new("Valuation report (50 + count + total)", true, db => db.Products.Where(p => p.IsActive).OrderBy(p => p.Name).Take(50),
            async db =>
            {
                await db.Products.CountAsync(p => p.IsActive);
                await db.Products.Where(p => p.IsActive).SumAsync(p => (long)p.QuantityOnHand * p.UnitCostCents);
                await db.Products.Where(p => p.IsActive).OrderBy(p => p.Name).Select(p => new { p.Sku, p.Name, p.QuantityOnHand, p.UnitCostCents }).Take(50).ToListAsync();
            });
        yield return new("Valuation by category", true, null,
            async db => await db.Products.Where(p => p.IsActive).GroupBy(p => p.CategoryId).Select(g => new { g.Key, Value = g.Sum(p => (long)p.QuantityOnHand * p.UnitCostCents) }).ToListAsync());
        yield return new("Movements report: 30 days (50 + count)", true, db => db.StockTransactions.Where(t => t.OccurredAt >= month).OrderByDescending(t => t.OccurredAt).Take(50),
            async db =>
            {
                await db.StockTransactions.CountAsync(t => t.OccurredAt >= month);
                await db.StockTransactions.Where(t => t.OccurredAt >= month).OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id).Take(50).ToListAsync();
            });
        yield return new("Movements report: 30 days, Issue only (50 + count)", true, db => db.StockTransactions.Where(t => t.OccurredAt >= month && t.Type == StockTransactionType.Issue).OrderByDescending(t => t.OccurredAt).Take(50),
            async db =>
            {
                await db.StockTransactions.CountAsync(t => t.OccurredAt >= month && t.Type == StockTransactionType.Issue);
                await db.StockTransactions.Where(t => t.OccurredAt >= month && t.Type == StockTransactionType.Issue).OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id).Take(50).ToListAsync();
            });
        yield return new("Movements report: one product (50 + count)", true, db => db.StockTransactions.Where(t => t.ProductId == 42).OrderByDescending(t => t.Id).Take(50),
            async db =>
            {
                await db.StockTransactions.CountAsync(t => t.ProductId == 42);
                await db.StockTransactions.Where(t => t.ProductId == 42).OrderByDescending(t => t.Id).Take(50).ToListAsync();
            });
        yield return new("Usage report: top consumers, 90 days", true, db => Usage(db, quarter).Take(25),
            async db => { await Usage(db, quarter).CountAsync(); await Usage(db, quarter).Take(25).ToListAsync(); });
        yield return new("PO summary: by supplier, 12 months", true, null,
            async db => await db.PurchaseOrders.Where(p => p.OrderDate >= year)
                .GroupBy(p => p.SupplierId)
                .Select(g => new { g.Key, Orders = g.Count(), Value = g.SelectMany(p => p.Lines).Sum(l => (long)l.QuantityOrdered * l.UnitCostCents) })
                .OrderByDescending(r => r.Value).Take(50).ToListAsync());
        yield return new("PO summary: list in date range (50 + count)", true, db => db.PurchaseOrders.Where(p => p.OrderDate >= year).OrderByDescending(p => p.OrderDate).Take(50),
            async db =>
            {
                await db.PurchaseOrders.CountAsync(p => p.OrderDate >= year);
                await db.PurchaseOrders.Where(p => p.OrderDate >= year).OrderByDescending(p => p.OrderDate).ThenByDescending(p => p.Id).Take(50).ToListAsync();
            });
        yield return new("Dead stock: no movement for 90 days (50 + count)", true, db => DeadStock(db, deadCutoff).OrderBy(p => p.Name).Take(50),
            async db => { await DeadStock(db, deadCutoff).CountAsync(); await DeadStock(db, deadCutoff).OrderBy(p => p.Name).Take(50).ToListAsync(); });
    }

    private static IQueryable<LowStockRow> LowestFirst(AppDbContext db)
        => db.LowStock().OrderBy(r => r.OnHand + r.OnOrder - r.ReorderPoint).ThenBy(r => r.Name);

    private static IQueryable<object> LatestMovements(AppDbContext db)
        => db.StockTransactions.OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id).Take(10)
            .Select(t => (object)new { t.Id, t.Product!.Sku, t.Product.Name, t.Type, t.QuantityDelta, t.OccurredAt });

    private static IQueryable<object> DueThisWeek(AppDbContext db, PurchaseOrderStatus[] open, DateOnly today, DateOnly weekEnd)
        => db.PurchaseOrders.Where(p => open.Contains(p.Status) && p.ExpectedDate >= today && p.ExpectedDate <= weekEnd)
            .OrderBy(p => p.ExpectedDate).Take(10)
            .Select(p => (object)new { p.Id, p.Number, SupplierName = p.Supplier!.Name, p.ExpectedDate });

    private static IQueryable<object> Usage(AppDbContext db, DateTime since)
        => db.StockTransactions.Where(t => t.Type == StockTransactionType.Issue && t.OccurredAt >= since)
            .GroupBy(t => t.ProductId)
            .Select(g => new { ProductId = g.Key, Used = -g.Sum(t => t.QuantityDelta) })
            .OrderByDescending(r => r.Used)
            .Select(r => (object)r);

    private static IQueryable<Product> DeadStock(AppDbContext db, DateTime cutoff)
        => db.Products.Where(p => p.IsActive && p.QuantityOnHand > 0
                                  && !db.StockTransactions.Any(t => t.ProductId == p.Id && t.OccurredAt >= cutoff));

    /// <summary>Runs <c>EXPLAIN QUERY PLAN</c> for the SQL EF generates, with the parameter values written in.</summary>
    private static async Task<string> ExplainAsync(AppDbContext db, IQueryable query)
    {
        var text = query.ToQueryString();
        var values = new Dictionary<string, string>();
        var sql = new StringBuilder();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var match = Regex.Match(line, @"^\.param set (@\w+) (.*)$");
            if (match.Success)
            {
                values[match.Groups[1].Value] = match.Groups[2].Value;
            }
            else if (!line.StartsWith("--", StringComparison.Ordinal))
            {
                sql.AppendLine(line);
            }
        }

        var statement = sql.ToString();
        foreach (var (name, value) in values.OrderByDescending(v => v.Key.Length))
        {
            statement = statement.Replace(name, value);
        }

        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + statement;
        var result = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.AppendLine($"{reader.GetInt32(0),3} {reader.GetInt32(1),3}  {reader.GetString(3)}");
        }

        return result.ToString().TrimEnd();
    }
}
