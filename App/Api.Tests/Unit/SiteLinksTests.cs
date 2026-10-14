using BlueTrack.Api.Notifications;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-224: the Site URL setting's rules, and the links the emails build from it.</summary>
public class SiteLinksTests
{
    [Theory]
    [InlineData("https://bluetrack.company.com", "https://bluetrack.company.com")]
    [InlineData("  https://BlueTrack.Company.com/  ", "https://bluetrack.company.com")]
    [InlineData("https://bluetrack.company.com:443/", "https://bluetrack.company.com")]
    [InlineData("https://bluetrack.company.com:8443", "https://bluetrack.company.com:8443")]
    [InlineData("https://proxy.company.com/bluetrack/", "https://proxy.company.com/bluetrack")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("http://127.0.0.1", "http://127.0.0.1")]
    public void TryNormalize_AcceptsHttps_AndHttpForLocalhostOnly(string value, string expected)
    {
        Assert.True(SiteLinks.TryNormalize(value, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_BlankMeansNotSet(string? value)
    {
        Assert.True(SiteLinks.TryNormalize(value, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://bluetrack.company.com")]
    [InlineData("bluetrack.company.com")]
    [InlineData("/reports")]
    [InlineData("ftp://bluetrack.company.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@bluetrack.company.com")]
    [InlineData("https://bluetrack.company.com/?a=1")]
    [InlineData("https://bluetrack.company.com/#top")]
    public void TryNormalize_RejectsAnythingElse_WithAMessage(string value)
    {
        Assert.False(SiteLinks.TryNormalize(value, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryNormalize_RejectsMoreThan500Characters()
    {
        Assert.False(SiteLinks.TryNormalize("https://bluetrack.company.com/" + new string('a', 480), out _, out var error));
        Assert.Contains("500", error);
    }

    [Fact]
    public void Link_IsTheSiteUrlPlusTheRoute_OrNullWithoutAUsableSiteUrl()
    {
        Assert.Equal("https://bluetrack.company.com/exceptions/overdue", SiteLinks.Link("https://bluetrack.company.com/", SiteLinks.OverdueReviews));
        Assert.Equal("https://proxy.company.com/bt/reports/trends", SiteLinks.Link("https://proxy.company.com/bt", SiteLinks.Trends));
        Assert.Null(SiteLinks.Link(null, SiteLinks.Trends));
        Assert.Null(SiteLinks.Link("not a url", SiteLinks.Trends));   // e.g. typed straight into the table
        Assert.Equal("Reports > Trends", SiteLinks.Describe(null, SiteLinks.Trends));
        Assert.Equal("Reports > Trends (https://bt.example.com/reports/trends)", SiteLinks.Describe("https://bt.example.com", SiteLinks.Trends));
    }

    [Fact]
    public void Email_WithoutASiteUrl_NamesThePages_WithNoLinks()
    {
        var email = new NotificationEmail("Heading").SeePages(null, SiteLinks.KpiSummary, SiteLinks.Trends, SiteLinks.OverdueAtRisk);

        Assert.Contains("In BlueTrack, open Reports > KPI Summary, Reports > Trends and Reports > Overdue / At-Risk.", email.Text);
        Assert.Contains("In BlueTrack, open Reports &gt; KPI Summary, Reports &gt; Trends and Reports &gt; Overdue / At-Risk.", email.Html);
        Assert.DoesNotContain("<a ", email.Html);
        Assert.Contains("Notification settings: Admin &gt; Notifications.", email.Html);
    }

    [Fact]
    public void Email_WithASiteUrl_LinksEachPage_EncodingTheAddressAndTheName()
    {
        var email = new NotificationEmail("Heading", "https://bt.example.com/a&b").SeePage(SiteLinks.CyberArkSources, "to check <the> settings");

        Assert.Contains("In BlueTrack, open Admin > Data Sources > CyberArk Sources (https://bt.example.com/a&b/admin/data-sources/cyberark) to check <the> settings.", email.Text);
        Assert.Contains("<a href=\"https://bt.example.com/a&amp;b/admin/data-sources/cyberark\">Admin &gt; Data Sources &gt; CyberArk Sources</a> to check &lt;the&gt; settings.", email.Html);
        Assert.Contains("<a href=\"https://bt.example.com/a&amp;b/admin/notifications\">Admin &gt; Notifications</a>", email.Html);
        Assert.DoesNotContain("<the>", email.Html);
    }

    [Fact]
    public void Email_ASiteUrlPathWithAQuoteOrSpace_IsEscapedInTheHref()
    {
        var email = new NotificationEmail("Heading", "https://bt.example.com/my \"site\"").SeePage(SiteLinks.Trends);

        Assert.Contains("<a href=\"https://bt.example.com/my%20%22site%22/reports/trends\">", email.Html);
    }

    [Fact]
    public void BreakGlassBody_LinksTheAuditLog_OnlyWithASiteUrl()
    {
        var when = new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);
        var withLink = BreakGlassAlerter.BuildBody("bg-admin", "Windows", "10.0.0.1", when, SiteLinks.Describe("https://bt.example.com", SiteLinks.AuditLog));
        var withoutLink = BreakGlassAlerter.BuildBody("bg-admin", "Windows", null, when, SiteLinks.Describe(null, SiteLinks.AuditLog));

        Assert.Contains("check Admin > Audit Log (https://bt.example.com/admin/audit-log) (event BreakGlassLogon)", withLink);
        Assert.Contains("check Admin > Audit Log (event BreakGlassLogon)", withoutLink);
        Assert.Contains("From: unknown", withoutLink);
    }
}
