using Microsoft.AspNetCore.Mvc.Rendering;

namespace InventoryTracker.Hosting;

public static class FormExtensions
{
    /// <summary>
    /// <c>"true"</c> when ModelState has an error for <paramref name="key"/> (e.g. <c>"Input.UserName"</c>), else <c>null</c>.
    /// Use as <c>aria-invalid="@Html.AriaInvalid("Input.UserName")"</c>: a null value omits the attribute and Pico
    /// styles <c>aria-invalid="true"</c> inputs as errors.
    /// </summary>
    public static string? AriaInvalid(this IHtmlHelper html, string key)
        => html.ViewData.ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0 ? "true" : null;
}
