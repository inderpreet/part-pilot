using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace InventoryTracker.Services;

/// <summary>One data row of an uploaded CSV, keyed by normalised (trimmed, lower-case) header.</summary>
public sealed record CsvDataRow(int LineNumber, IReadOnlyDictionary<string, string> Cells)
{
    public string Cell(string header) => Cells.TryGetValue(header, out var value) ? value : string.Empty;

    public bool HasValue(string header) => Cells.TryGetValue(header, out var value) && value.Length > 0;
}

/// <summary>Parsed CSV: original headers plus data rows. Unknown columns are kept but ignored by callers.</summary>
public sealed record CsvTable(IReadOnlyList<string> Headers, IReadOnlyList<CsvDataRow> Rows);

/// <summary>Reads an import CSV: UTF-8 (BOM optional), invariant culture, case-insensitive headers, 10 MB / 50k row cap.</summary>
public static class CsvImport
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxRows = 50_000;

    public static Result<CsvTable> Read(Stream stream, IReadOnlyList<string> requiredHeaders)
    {
        try
        {
            return ReadCore(stream, requiredHeaders);
        }
        catch (CsvHelperException)
        {
            return Result.Failure<CsvTable>("import.malformed", "The file could not be read as CSV. Save it again as CSV and retry.");
        }
    }

    private static Result<CsvTable> ReadCore(Stream stream, IReadOnlyList<string> requiredHeaders)
    {
        if (stream.CanSeek)
        {
            if (stream.Length > MaxBytes)
            {
                return Result.Failure<CsvTable>("import.too_large", $"The file is larger than {MaxBytes / (1024 * 1024)} MB.");
            }

            stream.Position = 0;
        }

        string? malformed = null;
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // A stray quote inside a plain field (3.5" screw) is kept as typed. A field that opens a quote and does not close it
            // properly has swallowed the separators after it, so the row cannot be trusted.
            BadDataFound = args =>
            {
                if (args.Field.TrimStart().StartsWith('"'))
                {
                    malformed ??= $"Line {args.Context.Parser?.Row}: a quoted field is not closed properly.";
                }
            },
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
            DetectColumnCountChanges = false,
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
        });

        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is null || csv.HeaderRecord.Length == 0)
        {
            return Result.Failure<CsvTable>("import.empty", "The file has no header row.");
        }

        var headers = csv.HeaderRecord.Select(h => h.Trim()).Where(h => h.Length > 0).ToList();
        var normalised = new HashSet<string>(headers.Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
        var missing = requiredHeaders.Where(h => !normalised.Contains(h.ToLowerInvariant())).ToList();
        if (missing.Count > 0)
        {
            return Result.Failure<CsvTable>("import.missing_columns", "Missing column(s): " + string.Join(", ", missing) + ".");
        }

        var rows = new List<CsvDataRow>();
        while (csv.Read())
        {
            if (rows.Count >= MaxRows)
            {
                return Result.Failure<CsvTable>("import.too_many_rows", $"The file has more than {MaxRows:N0} data rows.");
            }

            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in csv.HeaderRecord)
            {
                var key = header.Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                cells[key.ToLowerInvariant()] = CsvExport.Restore((csv.GetField(key) ?? string.Empty).Trim()).Trim();
            }

            if (cells.Values.All(string.IsNullOrEmpty))
            {
                continue;
            }

            rows.Add(new CsvDataRow(csv.Context.Parser?.Row ?? rows.Count + 2, cells));
        }

        if (malformed is not null)
        {
            return Result.Failure<CsvTable>("import.malformed", malformed);
        }

        if (rows.Count == 0)
        {
            return Result.Failure<CsvTable>("import.no_rows", "The file has a header but no data rows.");
        }

        return Result.Success(new CsvTable(headers, rows));
    }

    public static bool? ParseBool(string text, out string? error)
    {
        error = null;
        if (text.Length == 0)
        {
            return null;
        }

        switch (text.ToLowerInvariant())
        {
            case "true" or "yes" or "1":
                return true;
            case "false" or "no" or "0":
                return false;
            default:
                error = $"'{text}' is not a yes/no value.";
                return null;
        }
    }

    public static int? ParseInt(string text, out string? error, int min = 0)
    {
        error = null;
        if (text.Length == 0)
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            error = $"'{text}' is not a whole number.";
            return null;
        }

        if (value < min)
        {
            error = $"Must be at least {min}.";
            return null;
        }

        return value;
    }

    public static long? ParseMoneyCents(string text, out string? error)
    {
        error = null;
        if (text.Length == 0)
        {
            return null;
        }

        if (!MoneyParser.TryParseCents(text, out var cents, CultureInfo.InvariantCulture) || cents < 0)
        {
            error = $"'{text}' is not a money amount.";
            return null;
        }

        return cents;
    }
}
