using System.Globalization;
using BlueTrack.Api.Models;
using BlueTrack.Api.Notifications;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-222: when the digests are due, and the email builder's encoding and number formatting.</summary>
public class NotificationDigestTests
{
    [Theory]
    [InlineData("2026-10-13 06:59", "07:00", null)]
    [InlineData("2026-10-13 07:00", "07:00", "day:2026-10-13")]
    [InlineData("2026-10-13 23:59", "07:00", "day:2026-10-13")]
    [InlineData("2026-10-13 08:00", "bad", null)]
    public void DailyDueKey_FromTheDigestTimeOnward_KeyedByTheDay(string now, string time, string? expected) =>
        Assert.Equal(expected, DigestSchedule.DailyDueKey(DateTime.ParseExact(now, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), time));

    [Theory]
    [InlineData("2026-10-12 06:59", "Mon", "07:00", null)]               // Monday, too early
    [InlineData("2026-10-12 07:00", "Mon", "07:00", "week:2026-W42")]
    [InlineData("2026-10-13 07:00", "Mon", "07:00", null)]               // Tuesday
    [InlineData("2026-10-18 09:00", "sun", "08:30", "week:2026-W42")]    // Sunday ends the ISO week
    [InlineData("2027-01-01 07:00", "Fri", "07:00", "week:2026-W53")]    // ISO week-year, not the calendar year
    [InlineData("2026-10-12 07:00", "Monday", "07:00", null)]
    public void WeeklyDueKey_OnlyOnTheDay_FromTheTime_KeyedByTheIsoWeek(string now, string day, string time, string? expected) =>
        Assert.Equal(expected, DigestSchedule.WeeklyDueKey(DateTime.ParseExact(now, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), day, time));

    [Fact]
    public void Validate_RejectsBadTimesAndDays()
    {
        Assert.Empty(DigestSchedule.Validate(new NotificationSchedule { DailyDigestTime = "06:30", WeeklySummaryDay = "Fri", WeeklySummaryTime = "17:00" }));
        var errors = DigestSchedule.Validate(new NotificationSchedule { DailyDigestTime = "25:00", WeeklySummaryDay = "Friday", WeeklySummaryTime = "7" });
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void Email_EncodesEveryValueInHtml_AndKeepsTheTextAsIs()
    {
        var email = new NotificationEmail("Heading <h1>")
            .Paragraph("a < b & \"c\" 'd'")
            .Facts([("<label>", "<script>alert(1)</script>")])
            .Table(["<th>"], [["<td>"]]);

        Assert.Contains("Heading &lt;h1&gt;", email.Html);
        Assert.Contains("a &lt; b &amp; &quot;c&quot; &#39;d&#39;", email.Html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", email.Html);
        Assert.DoesNotContain("<script>", email.Html);
        Assert.DoesNotContain("<h1>", email.Html);
        Assert.DoesNotContain("<td><td>", email.Html);
        Assert.Contains("<label>: <script>alert(1)</script>", email.Text);
    }

    [Fact]
    public void Counts_AndPercentages_UseInvariantFormatting_WhateverTheCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1,234", NotificationEmail.Count(1234));
            Assert.Equal("1,234 of 2,000 (61.7%)", NotificationEmail.Ratio(1234, 2000));
            Assert.Equal("0 of 0 (N/A)", NotificationEmail.Ratio(0, 0));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
