using System.Diagnostics;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using InventoryTracker.Tests.Data;
using Xunit.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-07-2 acceptance. Run with <c>$env:INVENTORY_BENCH = 1; dotnet test --filter ImportBenchmarks</c>.</summary>
public sealed class ImportBenchmarks(ITestOutputHelper output) : ImportTestBase
{
    [BenchFact]
    public async Task Importing_10k_parts_finishes_within_30_seconds()
    {
        var rows = Enumerable.Range(1, 10_000).Select(i => $"BENCH-{i:00000},Part {i},,Hardware,each,,A-1,5,10,1.25,true").ToArray();
        var clock = Stopwatch.StartNew();

        var preview = await PreviewAsync(ImportKind.Products, ProductsCsv(rows));
        Assert.Equal(10_000, preview.CreateCount);
        var committed = await CommitAsync(preview.Token);

        clock.Stop();
        output.WriteLine($"10k parts: preview + commit in {clock.Elapsed.TotalSeconds:N1} s");
        Assert.True(committed.Succeeded, string.Join("; ", committed.Errors.Select(e => e.Message)));
        Assert.Equal(10_000, committed.Value.CreatedCount);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Took {clock.Elapsed.TotalSeconds:N1} s.");
    }
}
