using BlueTrack.Api.Reports;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-221: the Trends report's defaults and range limits.</summary>
public class TrendsRequestTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);

    [Fact]
    public void NoInput_IsTheLast90Days_ByDay()
    {
        var r = TrendsRequest.Validate(null, null, null, null, null, Today);

        Assert.Null(r.Error);
        Assert.Equal(new DateOnly(2026, 7, 16), r.From);
        Assert.Equal(Today, r.To);
        Assert.Equal("day", r.Granularity);
    }

    [Fact]
    public void Granularity_IsCaseInsensitive()
    {
        Assert.Equal("week", TrendsRequest.Validate(null, null, " Week ", null, null, Today).Granularity);
    }

    [Fact]
    public void TwoYearsAtDay_IsAllowed_OneDayMoreIsNot()
    {
        var from = new DateOnly(2024, 10, 13);
        Assert.Null(TrendsRequest.Validate(from, from.AddDays(TrendsRequest.MaxDayRangeDays - 1), "day", null, null, Today).Error);
        Assert.Contains("two years", TrendsRequest.Validate(from, from.AddDays(TrendsRequest.MaxDayRangeDays), "day", null, null, Today).Error);
        Assert.Null(TrendsRequest.Validate(from, from.AddDays(TrendsRequest.MaxDayRangeDays), "week", null, null, Today).Error);
        Assert.Contains("five years", TrendsRequest.Validate(from, from.AddDays(TrendsRequest.MaxWeekRangeDays), "week", null, null, Today).Error);
    }

    [Theory]
    [InlineData("2026-02-02", "2026-02-01", null, null, null)]
    [InlineData(null, null, "month", null, null)]
    [InlineData(null, null, null, 0, null)]
    [InlineData(null, null, null, null, -1)]
    public void BadInput_HasAnError(string? from, string? to, string? granularity, int? applicationKey, int? safeKey)
    {
        var r = TrendsRequest.Validate(from is null ? null : DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to),
            granularity, applicationKey, safeKey, Today);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void OneDay_IsAllowed()
    {
        Assert.Null(TrendsRequest.Validate(Today, Today, "day", 1, 1, Today).Error);
    }
}
