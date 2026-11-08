namespace InventoryTracker.Hosting;

/// <summary>
/// Turns what a user typed into a <c>datetime-local</c> / <c>date</c> input back into UTC. The browser fills a hidden
/// <c>tz</c> field with <c>new Date().getTimezoneOffset()</c> (minutes to <em>add</em> to local time to get UTC); without
/// JavaScript the server's own time zone is assumed, which is what <c>Html.LocalTime</c> shows before <c>site.js</c> upgrades it.
/// </summary>
public static class LocalTimeInput
{
    private const int MaxOffsetMinutes = 14 * 60;

    public static DateTime ToUtc(DateTime local, int? browserOffsetMinutes)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        try
        {
            if (browserOffsetMinutes is { } offset && Math.Abs(offset) <= MaxOffsetMinutes)
            {
                return DateTime.SpecifyKind(unspecified.AddMinutes(offset), DateTimeKind.Utc);
            }

            var zone = TimeZoneInfo.Local;
            if (zone.IsInvalidTime(unspecified))
            {
                unspecified = unspecified.AddHours(1); // skipped by a spring-forward change: the next valid hour
            }

            return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A date at the very edge of DateTime's range: nobody typed that on purpose; treat it as already UTC.
            return DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);
        }
    }

    /// <summary>A date range typed as whole days (both ends inclusive) as UTC instants: from the start of <paramref name="from"/> up to, not including, the day after <paramref name="to"/>.</summary>
    public static (DateTime? FromUtc, DateTime? ToUtcExclusive) DayRange(DateOnly? from, DateOnly? to, int? browserOffsetMinutes)
        => (
            from is { } f ? ToUtc(f.ToDateTime(TimeOnly.MinValue), browserOffsetMinutes) : null,
            to is { } t && t < DateOnly.MaxValue ? ToUtc(t.AddDays(1).ToDateTime(TimeOnly.MinValue), browserOffsetMinutes) : null);
}
