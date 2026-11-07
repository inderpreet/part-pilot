using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Hosting;

/// <summary>Helpers for the HTMX request/response conventions in architecture §4.</summary>
public static class HtmxExtensions
{
    /// <summary>True when the request came from htmx (<c>HX-Request: true</c>).</summary>
    public static bool IsHtmx(this HttpRequest request)
        => string.Equals(request.Headers["HX-Request"], "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsHtmx(this PageModel page) => page.Request.IsHtmx();

    /// <summary>Sets <c>HX-Trigger</c> with a single event, e.g. <c>HxTrigger("flash", "Saved")</c> or <c>HxTrigger("closeModal")</c>.</summary>
    public static void HxTrigger(this HttpResponse response, string eventName, object? detail = null)
    {
        var payload = new Dictionary<string, object?> { [eventName] = detail ?? true };

        // Merge with a trigger already set earlier in the request.
        if (response.Headers.TryGetValue("HX-Trigger", out var existing) && existing.ToString() is { Length: > 0 } json)
        {
            try
            {
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [])
                {
                    payload.TryAdd(key, value);
                }
            }
            catch (JsonException)
            {
                payload.TryAdd(json, true);
            }
        }

        response.Headers["HX-Trigger"] = JsonSerializer.Serialize(payload);
    }

    /// <summary>Sets <c>HX-Redirect</c> on a raw response (middleware / auth events, where there is no PageModel).</summary>
    public static void HxRedirect(this HttpResponse response, string url) => response.Headers["HX-Redirect"] = url;

    /// <summary>Client-side redirect for htmx requests (a plain 302 would be followed by XHR and swapped into the target).</summary>
    public static IActionResult HxRedirect(this PageModel page, string url)
    {
        page.Response.Headers["HX-Redirect"] = url;
        return new NoContentResult();
    }
}
