using System.Globalization;
using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-222: when a scheduled digest is due, from web.notification_config's
/// schedule (server local time). A digest is due at the first check on or
/// after its time -- the same day for a daily one, on the set day for the
/// weekly one -- and its DedupKey (the day, or the ISO week) makes it go
/// out once, even across restarts. A server that's down at the send time
/// sends when it's back the same day; a weekly summary missed for the whole
/// day isn't sent late.
/// </summary>
public static class DigestSchedule
{
    public static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>The day's DedupKey when the daily digest is due at <paramref name="localNow"/>, else null.</summary>
    public static string? DailyDueKey(DateTime localNow, string dailyDigestTime)
    {
        if (!FeedSchedule.TryParseTime(dailyDigestTime, out var time) || TimeOnly.FromDateTime(localNow) < time)
        {
            return null;
        }
        return "day:" + localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The ISO week's DedupKey when the weekly summary is due at <paramref name="localNow"/>, else null.</summary>
    public static string? WeeklyDueKey(DateTime localNow, string weeklySummaryDay, string weeklySummaryTime)
    {
        var dayIndex = Array.FindIndex(DayNames, d => string.Equals(d, weeklySummaryDay?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (dayIndex < 0 || (DayOfWeek)dayIndex != localNow.DayOfWeek
            || !FeedSchedule.TryParseTime(weeklySummaryTime, out var time) || TimeOnly.FromDateTime(localNow) < time)
        {
            return null;
        }
        return string.Create(CultureInfo.InvariantCulture, $"week:{ISOWeek.GetYear(localNow)}-W{ISOWeek.GetWeekOfYear(localNow):00}");
    }

    /// <summary>Validation messages for a schedule save; empty when valid.</summary>
    public static IReadOnlyList<string> Validate(NotificationSchedule schedule)
    {
        var errors = new List<string>();
        if (!FeedSchedule.TryParseTime(schedule.DailyDigestTime, out _)) errors.Add("DailyDigestTime must be a time as HH:mm (24-hour).");
        if (!FeedSchedule.TryParseTime(schedule.WeeklySummaryTime, out _)) errors.Add("WeeklySummaryTime must be a time as HH:mm (24-hour).");
        if (!DayNames.Contains(schedule.WeeklySummaryDay, StringComparer.Ordinal)) errors.Add("WeeklySummaryDay must be one of Sun, Mon, Tue, Wed, Thu, Fri, Sat.");
        return errors;
    }
}
