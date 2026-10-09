using BlueTrack.Api.DataFeeds;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-181: the data-feed schedule settings and file-pattern matching.</summary>
public class DataFeedScheduleTests
{
    [Theory]
    [InlineData("04:00", true)]
    [InlineData("23:59", true)]
    [InlineData("4:00", false)]
    [InlineData("24:00", false)]
    [InlineData("04:00:00", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseTime_AcceptsOnlyHHmm(string? value, bool expected) =>
        Assert.Equal(expected, FeedSchedule.TryParseTime(value, out _));

    [Fact]
    public void TryParseDays_ParsesNamesCaseInsensitively_AndRejectsUnknownOnes()
    {
        var days = FeedSchedule.TryParseDays("mon, Tue,FRI,Fri");
        Assert.NotNull(days);
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Friday }, days);
        Assert.Null(FeedSchedule.TryParseDays("Mon,Funday"));
        Assert.Null(FeedSchedule.TryParseDays(""));
        Assert.Null(FeedSchedule.TryParseDays(" , "));
    }

    [Theory]
    [InlineData("2026-10-09 07:00", true)]   // Friday, at the start
    [InlineData("2026-10-09 17:59", true)]
    [InlineData("2026-10-09 18:00", false)]  // the end is exclusive
    [InlineData("2026-10-09 06:59", false)]
    [InlineData("2026-10-10 12:00", false)]  // Saturday
    public void IsWithinBusinessHours_ChecksDayAndTime(string now, bool expected) =>
        Assert.Equal(expected, FeedSchedule.IsWithinBusinessHours(DateTime.Parse(now), "07:00", "18:00", "Mon,Tue,Wed,Thu,Fri"));

    [Fact]
    public void IsWithinBusinessHours_OvernightOrInvalidWindow_IsNeverInside()
    {
        Assert.False(FeedSchedule.IsWithinBusinessHours(new DateTime(2026, 10, 9, 23, 0, 0), "22:00", "06:00", "Fri"));
        Assert.False(FeedSchedule.IsWithinBusinessHours(new DateTime(2026, 10, 9, 12, 0, 0), "bad", "18:00", "Fri"));
    }

    [Fact]
    public void NextRun_IsTodayIfNotYetPassed_ElseTomorrow()
    {
        var runTime = new TimeOnly(4, 0);
        Assert.Equal(new DateTime(2026, 10, 9, 4, 0, 0), FeedSchedule.NextRun(new DateTime(2026, 10, 9, 3, 59, 0), runTime));
        Assert.Equal(new DateTime(2026, 10, 9, 4, 0, 0), FeedSchedule.NextRun(new DateTime(2026, 10, 9, 4, 0, 0), runTime));
        Assert.Equal(new DateTime(2026, 10, 10, 4, 0, 0), FeedSchedule.NextRun(new DateTime(2026, 10, 9, 4, 0, 1), runTime));
    }

    [Fact]
    public void RunTimeFellBetween_FiresOnceAsTheRunTimePasses()
    {
        var runTime = new TimeOnly(4, 0);
        var day = new DateTime(2026, 10, 9);
        Assert.False(FeedSchedule.RunTimeFellBetween(day.AddHours(3).AddMinutes(58), day.AddHours(3).AddMinutes(59), runTime));
        Assert.True(FeedSchedule.RunTimeFellBetween(day.AddHours(3).AddMinutes(59), day.AddHours(4), runTime));
        // The next wake starts after 04:00, so it doesn't fire again.
        Assert.False(FeedSchedule.RunTimeFellBetween(day.AddHours(4), day.AddHours(4).AddMinutes(1), runTime));
        // Across midnight.
        Assert.True(FeedSchedule.RunTimeFellBetween(day.AddHours(23).AddMinutes(59), day.AddDays(1).AddMinutes(1), new TimeOnly(0, 0)));
    }

    [Fact]
    public void Validate_ReportsEachBadSetting_AndSkipsNulls()
    {
        Assert.Empty(FeedSchedule.Validate(null, null, null, null, null));
        Assert.Empty(FeedSchedule.Validate("04:00", 15, "07:00", "18:00", "Mon,Tue"));
        var errors = FeedSchedule.Validate("4am", 0, "18:00", "07:00", "Someday");
        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("DataFeedRunTime"));
        Assert.Contains(errors, e => e.Contains("DataFeedRunRetentionDays"));
        Assert.Contains(errors, e => e.Contains("later than"));
        Assert.Contains(errors, e => e.Contains("BusinessDays"));
    }

    [Fact]
    public void ExpandPattern_FillsDateTokens()
    {
        var today = new DateTime(2026, 10, 9);
        Assert.Equal("targets_2026-10-09.csv", FeedFileResolver.ExpandPattern("targets_{yyyy-MM-dd}.csv", today));
        Assert.Equal("t_20261009_*.csv", FeedFileResolver.ExpandPattern("t_{yyyyMMdd}_*.csv", today));
        Assert.Equal("plain.csv", FeedFileResolver.ExpandPattern("plain.csv", today));
    }

    [Theory]
    [InlineData("targets_{yyyy-MM-dd}.csv", true)]
    [InlineData("*.csv", true)]
    [InlineData("", false)]
    [InlineData("sub\\file.csv", false)]
    [InlineData("../file.csv", false)]
    [InlineData("file|.csv", false)]
    public void ValidatePattern_AcceptsFileNamesOnly(string pattern, bool valid) =>
        Assert.Equal(valid, FeedFileResolver.ValidatePattern(pattern) is null);

    [Fact]
    public void FindMatches_ReturnsNewestFirst_AndOnlyMatchingFiles()
    {
        var folder = Directory.CreateTempSubdirectory("BlueTrackFeedTest_").FullName;
        try
        {
            var older = Path.Combine(folder, "apps_1.csv");
            var newer = Path.Combine(folder, "apps_2.csv");
            File.WriteAllText(older, "a");
            File.WriteAllText(newer, "b");
            File.WriteAllText(Path.Combine(folder, "other.csv"), "c");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(-1));

            var matches = FeedFileResolver.FindMatches(folder, "APPS_*.csv", DateTime.Today);
            Assert.Equal(["apps_2.csv", "apps_1.csv"], matches.Select(m => m.Name));
            Assert.Empty(FeedFileResolver.FindMatches(folder, "none_{yyyy-MM-dd}.csv", DateTime.Today));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
    private static readonly IReadOnlyDictionary<string, BlueTrack.Api.RiskScoring.ImportMappingService.FieldMapping> NoMapping =
        new Dictionary<string, BlueTrack.Api.RiskScoring.ImportMappingService.FieldMapping>();

    [Fact]
    public void FeedColumns_ReportsMissingRequiredColumns_CaseInsensitively()
    {
        Assert.Empty(FeedColumns.FindMissing("SafeAssignments", ["safename", "APPLICATION", "Source"], NoMapping));
        Assert.Equal(["Application"], FeedColumns.FindMissing("SafeAssignments", ["SafeName"], NoMapping));
        Assert.Equal(["TargetName", "RiskScore"], FeedColumns.FindMissing("TargetInventory", ["TargetType"], NoMapping));
    }

    [Fact]
    public void FeedColumns_AcceptsAnyOneOfTheAlternatives()
    {
        Assert.Empty(FeedColumns.FindMissing("AccountTargetMap", ["AccountKey", "TargetIdentifierType", "TargetIdentifierValue"], NoMapping));
        Assert.Equal(["AccountName or AccountKey"], FeedColumns.FindMissing("AccountTargetMap", ["TargetIdentifierType", "TargetIdentifierValue"], NoMapping));
    }

    [Fact]
    public void FeedColumns_UsesTheMappingProfilesColumnNames_AndDefaults()
    {
        var mapping = new Dictionary<string, BlueTrack.Api.RiskScoring.ImportMappingService.FieldMapping>
        {
            ["GroupName"] = new("cn", true, null),
            ["BaseRiskScore"] = new("score", false, "50")
        };
        // GroupName must be under "cn"; BaseRiskScore has a default, so it's never missing.
        Assert.Equal(["cn"], FeedColumns.FindMissing("AccessGroupInventory", ["GroupName", "GroupIdentifier"], mapping));
        Assert.Empty(FeedColumns.FindMissing("AccessGroupInventory", ["cn", "GroupIdentifier"], mapping));
    }
}
