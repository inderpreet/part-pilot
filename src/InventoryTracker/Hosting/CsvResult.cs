using InventoryTracker.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace InventoryTracker.Hosting;

/// <summary>
/// A CSV download: <c>text/csv; charset=utf-8</c> with an attachment filename, written straight to the response so a big export
/// does not sit in memory. The writer callback is a report service's <c>Write…CsvAsync</c>.
/// </summary>
public sealed class CsvResult(string fileName, Func<Stream, CancellationToken, Task> write) : IActionResult
{
    public string FileName { get; } = fileName;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = CsvExport.ContentType;
        response.Headers.CacheControl = "no-store";

        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(FileName);
        response.Headers.ContentDisposition = disposition.ToString();

        await write(response.Body, context.HttpContext.RequestAborted);
    }
}

public static class ReportPageExtensions
{
    /// <summary>
    /// The URL of this page's CSV handler with the filters currently applied (and the sort), but not the page number:
    /// a CSV always holds every matching row. Call it while rendering the results, so it follows the filters of the last htmx refresh.
    /// </summary>
    public static string CsvUrl(this PageModel page)
    {
        var carried = page.Request.Query
            .Where(q => q.Key is not ("page" or "pageSize" or "handler"))
            .SelectMany(q => q.Value.Select(v => KeyValuePair.Create(q.Key, v)))
            .Append(KeyValuePair.Create("handler", (string?)"Csv"));
        return QueryHelpers.AddQueryString(page.Request.Path.Value ?? "/", carried);
    }

    /// <summary>A download name such as <c>stock-valuation-2026-11-08.csv</c>, dated by the server's clock in UTC.</summary>
    public static string CsvFileName(string report, IClock clock)
        => $"{report}-{clock.UtcNow:yyyy-MM-dd}.csv";
}
