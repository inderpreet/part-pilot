using System.Text;

namespace InventoryTracker.Hosting;

/// <summary>Writes one log file per UTC day (<c>inventory-yyyyMMdd.log</c>) and prunes files older than the retention window.</summary>
public sealed class RollingFileLoggerProvider(string directory, int retainDays = 14, TimeProvider? timeProvider = null) : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private StreamWriter? _writer;
    private DateOnly _currentDay;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var day = DateOnly.FromDateTime(now);
                if (_writer is null || day != _currentDay)
                {
                    Roll(day);
                }

                _writer!.WriteLine(line);
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down (disk full, file locked, ...).
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Roll(DateOnly day)
    {
        _writer?.Dispose();
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"inventory-{day:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        _currentDay = day;

        var cutoff = day.AddDays(-retainDays);
        foreach (var file in Directory.EnumerateFiles(directory, "inventory-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file)["inventory-".Length..];
            if (DateOnly.TryParseExact(name, "yyyyMMdd", out var fileDay) && fileDay < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var sb = new StringBuilder()
                .Append(provider._time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'"))
                .Append(" [").Append(logLevel.ToString()[..3].ToUpperInvariant()).Append("] ")
                .Append(category).Append(": ").Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }

            provider.Write(sb.ToString());
        }
    }
}
