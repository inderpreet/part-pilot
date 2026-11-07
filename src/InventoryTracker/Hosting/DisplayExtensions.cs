using System.Globalization;
using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace InventoryTracker.Hosting;

/// <summary>Razor helpers: <c>@Html.Money(cents)</c>, <c>@Html.LocalTime(utc)</c>, <c>@Html.LocalDate(utc)</c>.</summary>
public static class DisplayExtensions
{
    /// <summary>Formats cents with the configured currency symbol (e.g. <c>$1,234.50</c>).</summary>
    public static IHtmlContent Money(this IHtmlHelper html, long cents)
        => new HtmlString(html.Encode(MoneyFormatter.Format(cents, CurrencySymbol(html))));

    public static IHtmlContent Money(this IHtmlHelper html, long? cents, string emptyText = "—")
        => cents is { } value ? html.Money(value) : new HtmlString(html.Encode(emptyText));

    /// <summary>
    /// Renders a UTC instant as a <c>&lt;time&gt;</c> element: server-local text for no-JS, upgraded to the
    /// browser's local time by <c>site.js</c> (<c>data-local</c>).
    /// </summary>
    public static IHtmlContent LocalTime(this IHtmlHelper html, DateTime? utc, string format = "g")
        => Time(html, utc, format, "datetime");

    public static IHtmlContent LocalDate(this IHtmlHelper html, DateTime? utc)
        => Time(html, utc, "d", "date");

    private static IHtmlContent Time(IHtmlHelper html, DateTime? utc, string format, string kind)
    {
        if (utc is not { } value)
        {
            return new HtmlString("—");
        }

        var asUtc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(asUtc, TimeZoneInfo.Local);
        var iso = asUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var text = local.ToString(format, CultureInfo.CurrentCulture);

        return new HtmlString($"<time datetime=\"{iso}\" data-local=\"{kind}\">{html.Encode(text)}</time>");
    }

    private static string CurrencySymbol(IHtmlHelper html)
    {
        var settings = html.ViewContext.HttpContext.RequestServices.GetRequiredService<ISettingsService>();

        // The settings snapshot is cached in memory, so after the first load this completes synchronously.
        return settings.GetStringAsync(SettingKeys.CurrencySymbol).GetAwaiter().GetResult();
    }
}
