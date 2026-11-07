using InventoryTracker.Hosting;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Tests.Hosting;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "logs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 3, 10, 23, 59, 0, TimeSpan.Zero));

    [Fact]
    public void Writes_to_a_daily_file_and_rolls_over_at_midnight_utc()
    {
        using (var provider = new RollingFileLoggerProvider(_dir, timeProvider: _clock))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("first {Value}", 1);
            _clock.Advance(TimeSpan.FromMinutes(2));
            logger.LogError(new InvalidOperationException("boom"), "second");
        }

        var day1 = File.ReadAllText(Path.Combine(_dir, "inventory-20260310.log"));
        var day2 = File.ReadAllText(Path.Combine(_dir, "inventory-20260311.log"));
        Assert.Contains("[INF] Test: first 1", day1);
        Assert.Contains("[ERR] Test: second", day2);
        Assert.Contains("InvalidOperationException: boom", day2);
    }

    [Fact]
    public void Prunes_files_older_than_the_retention_window()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "inventory-20260101.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "inventory-20260309.log"), "recent");
        File.WriteAllText(Path.Combine(_dir, "unrelated.log"), "keep");

        using (var provider = new RollingFileLoggerProvider(_dir, retainDays: 7, timeProvider: _clock))
        {
            provider.CreateLogger("Test").LogInformation("hello");
        }

        Assert.False(File.Exists(Path.Combine(_dir, "inventory-20260101.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "inventory-20260309.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "unrelated.log")));
    }

    [Fact]
    public void Unwritable_location_does_not_throw()
    {
        // A file where the directory should be makes Directory.CreateDirectory fail with IOException.
        var blocker = Path.Combine(Path.GetTempPath(), "blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            using var provider = new RollingFileLoggerProvider(blocker, timeProvider: _clock);
            provider.CreateLogger("Test").LogInformation("ignored");
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
