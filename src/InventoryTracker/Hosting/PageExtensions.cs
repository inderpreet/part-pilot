using InventoryTracker.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Hosting;

/// <summary>Helpers for page handlers: flash messages and mapping service errors onto form validation.</summary>
public static class PageExtensions
{
    public const string FlashKey = "Flash";

    /// <summary>
    /// Queues a flash message. For HTMX requests it is sent as an <c>HX-Trigger: {"flash": ...}</c> header;
    /// otherwise it is stored in TempData and rendered by <c>_Flash</c> on the next full page.
    /// </summary>
    public static void Flash(this PageModel page, string message)
    {
        if (page.Request.IsHtmx())
        {
            page.Response.HxTrigger("flash", message);
        }
        else
        {
            page.TempData[FlashKey] = message;
        }
    }

    /// <summary>
    /// Adds each error of a failed <see cref="Result"/> to ModelState, keyed by its field (empty key = form-level).
    /// Pass <paramref name="prefix"/> (e.g. <c>"Input"</c>) when the form binds to a nested <c>Input</c> property so
    /// the keys line up with the rendered inputs.
    /// </summary>
    public static void AddErrors(this ModelStateDictionary modelState, Result result, string? prefix = null)
    {
        foreach (var error in result.Errors)
        {
            var key = error.Field is null ? string.Empty : string.IsNullOrEmpty(prefix) ? error.Field : $"{prefix}.{error.Field}";
            modelState.AddModelError(key, error.Message);
        }
    }

    /// <summary>
    /// A local URL to return to after sign-in, or <paramref name="fallback"/>. Only same-site relative URLs are
    /// accepted (no <c>//host</c>, no <c>/\host</c>, no absolute URLs) so the login page can't be used as an open redirect.
    /// </summary>
    public static string SafeReturnUrl(this PageModel page, string? returnUrl, string fallback = "/")
        => !string.IsNullOrEmpty(returnUrl) && page.Url.IsLocalUrl(returnUrl) && !returnUrl.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase)
            ? returnUrl
            : fallback;
}
