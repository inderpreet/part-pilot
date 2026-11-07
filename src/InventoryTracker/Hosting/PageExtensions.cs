using InventoryTracker.Auth;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Hosting;

/// <summary>Helpers for page handlers: flash messages and mapping service errors onto form validation.</summary>
public static class PageExtensions
{
    public const string FlashKey = "Flash";
    public const string FlashErrorKey = "FlashError";

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
    /// Like <see cref="Flash"/> but styled as an error and shown longer. For htmx the toast is sent in <c>HX-Trigger</c>
    /// (<c>{"flash": {"message": ..., "level": "error"}}</c>); otherwise it waits in TempData for the next full page.
    /// </summary>
    public static void FlashError(this PageModel page, string message)
    {
        if (page.Request.IsHtmx())
        {
            page.Response.HxTrigger("flash", new { message, level = "error" });
        }
        else
        {
            page.TempData[FlashErrorKey] = message;
        }
    }

    /// <summary>
    /// For handlers on a page that only requires <c>CanView</c> but whose POST/modal handlers change data: the page-level
    /// attribute cannot vary per handler, so check the <c>CanEdit</c> policy explicitly and <c>return Forbid()</c> when false.
    /// </summary>
    public static async Task<bool> CanEditAsync(this PageModel page)
    {
        var authorization = page.HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(page.User, Policies.CanEdit)).Succeeded;
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
