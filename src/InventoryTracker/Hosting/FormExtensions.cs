using Microsoft.AspNetCore.Html;
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

    /// <summary>
    /// The message for <paramref name="key"/> rendered right after its control:
    /// <c>&lt;small class="field-error" id="Input_Name-error"&gt;…&lt;/small&gt;</c>. Renders nothing when the key has no error,
    /// so a valid form is unchanged. The page-level summary still lists every message.
    /// </summary>
    public static IHtmlContent FieldError(this IHtmlHelper html, string key)
    {
        if (!html.ViewData.ModelState.TryGetValue(key, out var entry) || entry.Errors.Count == 0)
        {
            return HtmlString.Empty;
        }

        var message = entry.Errors.Select(e => e.ErrorMessage).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        if (message is null)
        {
            return HtmlString.Empty;
        }

        var small = new TagBuilder("small");
        small.AddCssClass("field-error");
        small.Attributes["id"] = ErrorId(key);
        small.InnerHtml.Append(message);
        return small;
    }

    /// <summary>
    /// Value for <c>aria-describedby</c>: the field-error id when <paramref name="key"/> has a message, followed by the
    /// optional id of an existing hint. <c>null</c> (attribute omitted) when there is neither.
    /// </summary>
    public static string? DescribedBy(this IHtmlHelper html, string key, string? hintId = null)
    {
        var hasError = html.ViewData.ModelState.TryGetValue(key, out var entry)
            && entry.Errors.Any(e => !string.IsNullOrWhiteSpace(e.ErrorMessage));
        var ids = new[] { hasError ? ErrorId(key) : null, hintId }.Where(id => id is not null);
        var joined = string.Join(' ', ids);
        return joined.Length == 0 ? null : joined;
    }

    private static string ErrorId(string key) => key.Replace('.', '_').Replace('[', '_').Replace(']', '_') + "-error";
}
