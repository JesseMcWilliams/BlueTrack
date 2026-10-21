using BlueTrack.Api.CyberArkDirect;
using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Models;
using BlueTrack.Api.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-271: the shadow pull's settings, which kind of pull the settings call for, and the clean-night count.</summary>
public class ShadowPullTests
{
    private static readonly AllowedFileRoots NoRoots = new(new ConfigurationBuilder().Build());

    private static CyberArkSourceSettings EvdWithShadow(string? url = "https://pvwa.example.com", int? credential = 3, bool shadow = true,
        string shadowTime = "04:00", string source = "Evd") => new()
    {
        ImportSelfHosted = true,
        SelfHostedSource = source,
        EvdDatabaseName = "CyberArkSH",
        PvwaBaseUrl = url,
        PvwaCredentialKey = credential,
        ShadowPullEnabled = shadow,
        ShadowPullTime = shadowTime
    };

    private static IReadOnlyList<string> Check(CyberArkSourceSettings s) =>
        CyberArkSourceValidation.Validate(CyberArkSourceValidation.Normalize(s), NoRoots);

    [Fact]
    public void TheEvd_WithAShadowPull_AddressAndCredential_IsValid() => Assert.Empty(Check(EvdWithShadow()));

    [Fact]
    public void AShadowPull_NeedsTheAddressAndCredential()
    {
        var errors = Check(EvdWithShadow(url: null, credential: null));

        Assert.Contains("PVWA address is required for the shadow pull.", errors);
        Assert.Contains("A credential is required for the shadow pull.", errors);
    }

    [Fact]
    public void AShadowPull_KeepsAnInvalidAddress_SoItIsReported_RatherThanDroppingIt()
    {
        var normalized = CyberArkSourceValidation.Normalize(EvdWithShadow(url: "http://pvwa.example.com"));

        Assert.Equal("http://pvwa.example.com", normalized.PvwaBaseUrl);
        Assert.Contains(CyberArkSourceValidation.Validate(normalized, NoRoots), e => e.StartsWith("PVWA address must be https://host"));
    }

    [Theory]
    [InlineData("4am")]
    [InlineData("25:00")]
    [InlineData("")]
    public void AShadowPullTime_ThatIsNotATime_IsRejected(string time) =>
        Assert.Contains("Shadow pull time must be a time as HH:mm (24-hour).", Check(EvdWithShadow(shadowTime: time)));

    [Fact]
    public void WithDirectAsTheSource_TheShadowPullIsTurnedOff()
    {
        var normalized = CyberArkSourceValidation.Normalize(EvdWithShadow(source: "Direct"));

        Assert.False(normalized.ShadowPullEnabled);
        Assert.Equal("Direct", SelfHostedDirectPullService.ModeFor(normalized));
    }

    [Fact]
    public void WithSelfHostedOff_TheShadowPullIsTurnedOff() =>
        Assert.False(CyberArkSourceValidation.Normalize(new CyberArkSourceSettings { ImportSelfHosted = false, ShadowPullEnabled = true }).ShadowPullEnabled);

    [Theory]
    [InlineData(true, "Evd", true, "Shadow")]
    [InlineData(true, "Evd", false, null)]
    [InlineData(true, "Direct", false, "Direct")]
    [InlineData(false, "Evd", true, null)]
    public void ModeFor_FollowsTheSavedSettings(bool selfHosted, string source, bool shadow, string? expected) =>
        Assert.Equal(expected, SelfHostedDirectPullService.ModeFor(new CyberArkSourceSettings
        {
            ImportSelfHosted = selfHosted, SelfHostedSource = source, ShadowPullEnabled = shadow
        }));

    // ---- Clean nights ----

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Today = new(2026, 10, 20, 9, 0, 0);
    private static long _key;

    private static ShadowComparisonRun Run(int daysAgo, bool clean, int hour = 4) =>
        new() { RunKey = ++_key, ComparedAt = Today.Date.AddDays(-daysAgo).AddHours(hour), IsClean = clean };

    [Fact]
    public void SevenCleanNightsInARow_EndingThisMorning_Count() =>
        Assert.Equal(7, ShadowCleanNights.Count(Enumerable.Range(0, 7).Select(d => Run(d, true)), Today, Utc));

    [Fact]
    public void BeforeTonightsPull_TheRunEndingLastNightCounts() =>
        Assert.Equal(3, ShadowCleanNights.Count([Run(1, true), Run(2, true), Run(3, true)], Today, Utc));

    [Fact]
    public void ANightThatWasNotClean_EndsTheRun() =>
        Assert.Equal(2, ShadowCleanNights.Count([Run(0, true), Run(1, true), Run(2, false), Run(3, true)], Today, Utc));

    [Fact]
    public void ANightWithNoComparison_EndsTheRun() =>
        Assert.Equal(1, ShadowCleanNights.Count([Run(0, true), Run(2, true), Run(3, true)], Today, Utc));

    [Fact]
    public void ANightIsJudgedByItsLatestComparison()
    {
        // Pull now after marking a difference: the night's second comparison is clean.
        Assert.Equal(2, ShadowCleanNights.Count([Run(1, true), Run(0, false, hour: 4), Run(0, true, hour: 8)], Today, Utc));
        Assert.Equal(0, ShadowCleanNights.Count([Run(1, true), Run(0, true, hour: 4), Run(0, false, hour: 8)], Today, Utc));
    }

    [Fact]
    public void NoComparisonSinceTheDayBeforeYesterday_CountsNothing() =>
        Assert.Equal(0, ShadowCleanNights.Count([Run(2, true), Run(3, true)], Today, Utc));

    [Fact]
    public void NightsFollowTheServersTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC-7", TimeSpan.FromHours(-7), "UTC-7", "UTC-7");
        // 04:00 local is 11:00 UTC; 02:00 UTC on the 20th is still the 19th locally.
        ShadowComparisonRun[] runs =
        [
            new() { RunKey = 1, ComparedAt = new DateTime(2026, 10, 20, 11, 0, 0), IsClean = true },
            new() { RunKey = 2, ComparedAt = new DateTime(2026, 10, 20, 2, 0, 0), IsClean = true },
        ];
        Assert.Equal(2, ShadowCleanNights.Count(runs, new DateTime(2026, 10, 20, 9, 0, 0), zone));
    }
}
