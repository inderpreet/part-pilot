using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace InventoryTracker.Services;

/// <summary>
/// One CSV column: a header and a function that returns the cell already formatted. Build them with <see cref="CsvColumn"/>
/// so numbers, money and dates are always written the same way, whatever the server's culture.
/// </summary>
/// <param name="IsText">Free text that a spreadsheet could take for a formula, so a leading <c>= + - @</c> is neutralised.</param>
public sealed record CsvColumn<T>(string Header, Func<T, string?> Value, bool IsText = false);

public static class CsvColumn
{
    /// <summary>Free text (names, references, notes).</summary>
    public static CsvColumn<T> Text<T>(string header, Func<T, string?> value) => new(header, value, IsText: true);

    public static CsvColumn<T> Int<T>(string header, Func<T, long?> value)
        => new(header, row => value(row)?.ToString(CultureInfo.InvariantCulture));

    /// <summary>Minor units (cents) written as a plain decimal such as <c>12.50</c>, with no currency symbol or thousands separator.</summary>
    public static CsvColumn<T> Money<T>(string header, Func<T, long?> cents)
        => new(header, row => cents(row) is { } value ? (value / 100m).ToString("0.00", CultureInfo.InvariantCulture) : null);

    /// <summary>A UTC instant as <c>yyyy-MM-dd HH:mm:ss</c>; put "(UTC)" in the header.</summary>
    public static CsvColumn<T> Utc<T>(string header, Func<T, DateTime?> value)
        => new(header, row => value(row)?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

    /// <summary>A calendar date as <c>yyyy-MM-dd</c>.</summary>
    public static CsvColumn<T> Date<T>(string header, Func<T, DateOnly?> value)
        => new(header, row => value(row)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>Writes report rows as CSV: UTF-8 with a byte-order mark (so Excel opens it correctly), CRLF line ends, invariant numbers.</summary>
public static class CsvExport
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static Task WriteAsync<T>(Stream destination, IEnumerable<T> rows, IReadOnlyList<CsvColumn<T>> columns, CancellationToken cancellationToken = default)
        => WriteCoreAsync(destination, columns, async write =>
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await write(row);
            }
        });

    public static Task WriteAsync<T>(Stream destination, IAsyncEnumerable<T> rows, IReadOnlyList<CsvColumn<T>> columns, CancellationToken cancellationToken = default)
        => WriteCoreAsync(destination, columns, async write =>
        {
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                await write(row);
            }
        });

    /// <summary>
    /// Stops a spreadsheet from running a cell as a formula (CSV injection): text that starts with <c>=</c>, <c>+</c>, <c>-</c>,
    /// <c>@</c>, a tab or a carriage return gets a leading apostrophe. Text already written as apostrophes before such a
    /// character gets one too, so <see cref="Restore"/> is an exact inverse.
    /// </summary>
    public static string? Neutralise(string? text)
        => text is not null && StartsLikeFormula(text) ? "'" + text : text;

    /// <summary>Removes the apostrophe <see cref="Neutralise"/> added, so an exported file imports back unchanged.</summary>
    public static string Restore(string text)
        => text.Length > 1 && text[0] == '\'' && StartsLikeFormula(text[1..]) ? text[1..] : text;

    private static bool StartsLikeFormula(string text)
    {
        var rest = text.TrimStart('\'');
        return rest.Length > 0 && rest[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
    }

    private static async Task WriteCoreAsync<T>(Stream destination, IReadOnlyList<CsvColumn<T>> columns, Func<Func<T, Task>, Task> enumerate)
    {
        await using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { NewLine = "\r\n" });

        foreach (var column in columns)
        {
            csv.WriteField(column.Header);
        }

        await csv.NextRecordAsync();

        await enumerate(async row =>
        {
            foreach (var column in columns)
            {
                var value = column.Value(row);
                csv.WriteField(column.IsText ? Neutralise(value) : value);
            }

            await csv.NextRecordAsync();
        });

        await csv.FlushAsync();
    }
}
