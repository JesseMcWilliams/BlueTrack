using System.Globalization;

namespace BlueTrack.Api.DataFeeds;

/// <summary>
/// D-181: parsing and checks for the data-feed schedule settings in
/// web.app_config -- the nightly run time, and the business hours inside
/// which Run now warns. Times are "HH:mm", server local time; days are
/// comma-separated three-letter English names ("Mon,Tue,Wed,Thu,Fri").
/// </summary>
public static class FeedSchedule
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>Null if any entry isn't a three-letter day name; duplicates are ignored.</summary>
    public static IReadOnlySet<DayOfWeek>? TryParseDays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var days = new HashSet<DayOfWeek>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var index = Array.FindIndex(DayNames, d => string.Equals(d, part, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            days.Add((DayOfWeek)index);
        }
        return days.Count == 0 ? null : days;
    }

    /// <summary>
    /// Whether <paramref name="now"/> falls inside business hours: on one of
    /// the business days, at or after the start and before the end. An end
    /// at or before the start (an overnight window) isn't supported and
    /// means "never inside"; the settings page rejects it.
    /// </summary>
    public static bool IsWithinBusinessHours(DateTime now, string start, string end, string days)
    {
        if (!TryParseTime(start, out var startTime) || !TryParseTime(end, out var endTime) || endTime <= startTime) return false;
        var businessDays = TryParseDays(days);
        if (businessDays is null || !businessDays.Contains(now.DayOfWeek)) return false;
        var time = TimeOnly.FromDateTime(now);
        return time >= startTime && time < endTime;
    }

    /// <summary>The next moment at or after <paramref name="now"/> whose time of day is <paramref name="runTime"/>.</summary>
    public static DateTime NextRun(DateTime now, TimeOnly runTime)
    {
        var today = now.Date + runTime.ToTimeSpan();
        return today >= now ? today : today.AddDays(1);
    }

    /// <summary>
    /// Whether the daily <paramref name="runTime"/> fell in the window after
    /// <paramref name="after"/> up to and including <paramref name="upTo"/>.
    /// The background service checks this each time it wakes, with the
    /// previous wake time as <paramref name="after"/>, so a run time changed
    /// on the settings page takes effect without a restart, and a run time
    /// already past when the app starts doesn't fire until the next day.
    /// </summary>
    public static bool RunTimeFellBetween(DateTime after, DateTime upTo, TimeOnly runTime) =>
        NextRun(after.AddTicks(1), runTime) <= upTo;

    /// <summary>Validation messages for the schedule settings in a save request; empty when valid. Null fields are skipped (unchanged).</summary>
    public static IReadOnlyList<string> Validate(string? runTime, int? retentionDays, string? start, string? end, string? days)
    {
        var errors = new List<string>();
        if (runTime is not null && !TryParseTime(runTime, out _)) errors.Add("DataFeedRunTime must be a time as HH:mm (24-hour).");
        if (retentionDays is not null && (retentionDays < 1 || retentionDays > 365)) errors.Add("DataFeedRunRetentionDays must be between 1 and 365.");
        TimeOnly startTime = default, endTime = default;
        var startOk = start is null || TryParseTime(start, out startTime);
        var endOk = end is null || TryParseTime(end, out endTime);
        if (!startOk) errors.Add("BusinessHoursStart must be a time as HH:mm (24-hour).");
        if (!endOk) errors.Add("BusinessHoursEnd must be a time as HH:mm (24-hour).");
        if (start is not null && end is not null && startOk && endOk && endTime <= startTime) errors.Add("BusinessHoursEnd must be later than BusinessHoursStart.");
        if (days is not null && TryParseDays(days) is null) errors.Add("BusinessDays must be comma-separated day names, e.g. Mon,Tue,Wed,Thu,Fri.");
        return errors;
    }
}
