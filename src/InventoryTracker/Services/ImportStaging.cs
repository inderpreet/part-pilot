using System.Collections.Concurrent;
using System.Security.Cryptography;
using InventoryTracker.Data.Entities;

namespace InventoryTracker.Services;

/// <summary>
/// Holds an uploaded CSV between preview and commit: a random token names a temp file that expires after 30 minutes.
/// Never keyed by the user-supplied file name (architecture D22).
/// </summary>
public sealed class ImportStaging : IDisposable
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private const string FilePrefix = "pp-import-";

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly IClock clock;

    public ImportStaging(IClock clock)
    {
        this.clock = clock;
        SweepOrphans();
    }

    public sealed record Slot(string UserId, ImportKind Kind, string FileName, ImportOptions Options, string Path, DateTime ExpiresAt);

    public async Task<(string Token, string Path)> SaveAsync(ImportKind kind, string fileName, ImportOptions options, string userId, Stream stream, CancellationToken cancellationToken)
    {
        Purge();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), FilePrefix + token + ".csv");
        await using (var file = File.Create(path))
        {
            var buffer = new byte[81_920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                total += read;
                if (total > CsvImport.MaxBytes)
                {
                    file.Dispose();
                    TryDelete(path);
                    throw new ImportTooLargeException();
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        var slot = new Slot(userId, kind, SanitizeFileName(fileName), options, path, clock.UtcNow.Add(Ttl));
        _slots[token] = slot;
        return (token, path);
    }

    public Slot? TryGet(string token, string userId)
    {
        Purge();
        if (!_slots.TryGetValue(token, out var slot) || !string.Equals(slot.UserId, userId, StringComparison.Ordinal))
        {
            return null;
        }

        if (slot.ExpiresAt <= clock.UtcNow)
        {
            Remove(token);
            return null;
        }

        return slot;
    }

    public void Remove(string token)
    {
        if (_slots.TryRemove(token, out var slot))
        {
            TryDelete(slot.Path);
        }
    }

    public void Dispose()
    {
        foreach (var token in _slots.Keys)
        {
            Remove(token);
        }
    }

    private void Purge()
    {
        var now = clock.UtcNow;
        foreach (var (token, slot) in _slots)
        {
            if (slot.ExpiresAt <= now)
            {
                Remove(token);
            }
        }
    }

    /// <summary>
    /// Removes staged files a previous process left behind (crash, kill). Compares real file times, and only past the TTL,
    /// so another running instance's live uploads are safe.
    /// </summary>
    private static void SweepOrphans()
    {
        try
        {
            var cutoff = DateTime.UtcNow - Ttl;
            foreach (var path in Directory.EnumerateFiles(System.IO.Path.GetTempPath(), FilePrefix + "*.csv"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort housekeeping: never stop the app over it.
        }
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = System.IO.Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return "upload.csv";
        }

        return name.Length <= ImportBatch.FileNameMaxLength ? name : name[..ImportBatch.FileNameMaxLength];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the sweep at the next process start retries it.
        }
    }
}

public sealed class ImportTooLargeException : Exception;
