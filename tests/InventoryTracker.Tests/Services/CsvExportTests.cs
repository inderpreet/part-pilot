using System.Text;
using InventoryTracker.Services;

namespace InventoryTracker.Tests.Services;

public sealed class CsvExportTests
{
    private sealed record Row(string? Name, long Qty, long? Cents, DateTime? At, DateOnly? Day);

    private static readonly IReadOnlyList<CsvColumn<Row>> Columns =
    [
        CsvColumn.Text<Row>("Name", r => r.Name),
        CsvColumn.Int<Row>("Qty", r => r.Qty),
        CsvColumn.Money<Row>("Value", r => r.Cents),
        CsvColumn.Utc<Row>("At (UTC)", r => r.At),
        CsvColumn.Date<Row>("Day", r => r.Day),
    ];

    private static async Task<byte[]> WriteAsync(params Row[] rows)
    {
        using var stream = new MemoryStream();
        await CsvExport.WriteAsync(stream, rows, Columns);
        return stream.ToArray();
    }

    [Fact]
    public async Task The_file_starts_with_a_utf8_byte_order_mark_and_the_header_row_with_crlf_line_ends()
    {
        var bytes = await WriteAsync(new Row("Bolt", 1, 100, null, null));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split("\r\n");
        Assert.Equal("Name,Qty,Value,At (UTC),Day", lines[0]);
        Assert.Equal("Bolt,1,1.00,,", lines[1]);
    }

    [Fact]
    public async Task No_rows_still_gives_the_header()
    {
        var text = Encoding.UTF8.GetString(await WriteAsync()).TrimStart('\uFEFF');

        Assert.Equal("Name,Qty,Value,At (UTC),Day\r\n", text);
    }

    [Fact]
    public async Task Numbers_money_and_dates_are_invariant_whatever_the_server_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var at = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            var text = Encoding.UTF8.GetString(await WriteAsync(new Row("X", 1234567, 123456, at, new DateOnly(2026, 3, 4))));

            Assert.Contains("X,1234567,1234.56,2026-03-04 05:06:07,2026-03-04", text);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Negative_money_and_quantities_keep_their_sign_and_are_not_treated_as_formulas()
    {
        var text = Encoding.UTF8.GetString(await WriteAsync(new Row("Adj", -5, -250, null, null)));

        Assert.Contains("Adj,-5,-2.50,,", text);
    }

    [Fact]
    public async Task Commas_quotes_and_line_breaks_in_text_are_escaped()
    {
        var text = Encoding.UTF8.GetString(await WriteAsync(new Row("5\" pipe, \"long\"\nline", 1, null, null, null)));

        Assert.Contains("\"5\"\" pipe, \"\"long\"\"\nline\",1,,,", text);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tTabbed", "'\tTabbed")]
    [InlineData("Normal", "Normal")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    public void Text_that_could_run_as_a_formula_is_neutralised(string input, string expected)
        => Assert.Equal(expected, CsvExport.Neutralise(input));

    [Fact]
    public async Task Only_text_columns_are_neutralised()
    {
        var text = Encoding.UTF8.GetString(await WriteAsync(new Row("=HYPERLINK(\"x\")", -1, null, null, null)));

        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\",-1,,,", text);
    }

    [Fact]
    public async Task Async_sequences_are_written_the_same_way()
    {
        static async IAsyncEnumerable<Row> Rows()
        {
            yield return new Row("One", 1, 5, null, null);
            await Task.Yield();
            yield return new Row("Two", 2, 10, null, null);
        }

        using var stream = new MemoryStream();
        await CsvExport.WriteAsync(stream, Rows(), Columns);

        Assert.Equal(
            "Name,Qty,Value,At (UTC),Day\r\nOne,1,0.05,,\r\nTwo,2,0.10,,\r\n",
            Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF'));
    }

    [Fact]
    public async Task The_destination_stream_is_left_open()
    {
        using var stream = new MemoryStream();

        await CsvExport.WriteAsync(stream, [new Row("A", 1, null, null, null)], Columns);

        Assert.True(stream.CanWrite);
    }
}
