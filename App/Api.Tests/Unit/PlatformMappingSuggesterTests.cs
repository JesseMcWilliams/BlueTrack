using BlueTrack.Api.Data;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-293: the suggestions on Admin > Platform Mapping.</summary>
public class PlatformMappingSuggesterTests
{
    private static readonly PlatformMappingOptions Options = new(
        [
            new(1, "Domain Account"), new(2, "Local/OS Account"), new(3, "Database Account"), new(4, "Network Device"),
            new(5, "Cloud IAM"), new(6, "Infrastructure (PSM/CPM)"), new(7, "Application/Service Account"), new(8, "Emergency/Break-glass"),
        ],
        [new(10, "Active Directory"), new(11, "LDAP"), new(12, "Local OS"), new(13, "Local Database"), new(14, "Local Application")]);

    private static int _nextKey = 100;

    private static PlatformMappingRow Unmapped(string platformId, string? platformName = null, string source = "CyberArk Self-Hosted Vault", string? platformType = "regular") =>
        new(_nextKey++, source, platformId, platformName ?? platformId, null, null, null, null, null, true, 1, 1, 1, platformType);

    private static PlatformMappingRow Mapped(string platformId, int typeKey, string typeName, int? sorKey, string? sorName, string source) =>
        new(_nextKey++, source, platformId, platformId, typeKey, typeName, sorKey, sorName, null, false, 1, 0, 0, "regular");

    private static PlatformMappingSuggestion? SuggestionFor(string platformId, string? platformName = null) =>
        PlatformMappingSuggester.Suggest([Unmapped(platformId, platformName)], Options)[0].Suggestion;

    [Theory]
    [InlineData("WinDomain", "Domain Account", "Active Directory", "windomain")]
    [InlineData("Win_Dom_Rec", "Domain Account", "Active Directory", "dom")]
    [InlineData("WinServerLocal", "Local/OS Account", "Local OS", "winserverlocal")]
    [InlineData("Win_Loc", "Local/OS Account", "Local OS", "win")]
    [InlineData("UnixSSHKeys", "Local/OS Account", "Local OS", "unix")]
    [InlineData("LNX_LOC_PW", "Local/OS Account", "Local OS", "lnx")]
    [InlineData("DB2UnixSSH", "Database Account", "Local Database", "db2")]
    [InlineData("Oracle", "Database Account", "Local Database", "oracle")]
    [InlineData("MSSql", "Database Account", "Local Database", "mssql")]
    [InlineData("Novell-eDirectory", "Domain Account", "LDAP", "directory")]
    [InlineData("CiscoIOS", "Network Device", null, "cisco")]
    [InlineData("F5-BigIP", "Network Device", null, "f5")]
    [InlineData("AWSAccessKeys", "Cloud IAM", null, "aws")]
    [InlineData("PSMSecureConnect", "Infrastructure (PSM/CPM)", "Local Application", "psm")]
    [InlineData("CyberArkDigitalVault", "Infrastructure (PSM/CPM)", "Local Application", "vault")]
    [InlineData("BusinessWebsite", "Application/Service Account", "Local Application", "website")]
    [InlineData("SchedTask", "Application/Service Account", "Local Application", "schedtask")]
    [InlineData("BreakGlassDomain", "Emergency/Break-glass", null, "breakglass")]
    public void AKeyword_SuggestsItsTypeAndSource(string platformId, string type, string? sor, string keyword)
    {
        var suggestion = SuggestionFor(platformId);

        Assert.NotNull(suggestion);
        Assert.Equal(type, suggestion!.AccountTypeName);
        Assert.Equal(sor, suggestion.SourceOfRecord);
        Assert.Equal("Keyword", suggestion.Basis);
        Assert.Equal(keyword, suggestion.Detail);
    }

    [Theory]
    [InlineData("Laws")]          // "aws" only as a whole word
    [InlineData("Twinkle")]       // "win" only as a whole word
    [InlineData("SIAStrongAccounts")]
    public void NoKeyword_NoSuggestion(string platformId) => Assert.Null(SuggestionFor(platformId));

    [Fact]
    public void TheNameIsSearchedToo() =>
        Assert.Equal("Database Account", SuggestionFor("Custom01", "Oracle on Linux")!.AccountTypeName);

    [Fact]
    public void TheSamePlatformMappedInAnotherSource_ComesFirst()
    {
        var other = Mapped("UnixSSH", 3, "Database Account", 13, "Local Database", "CyberArk Privilege Cloud (SaaS)");
        var row = Unmapped("unixssh");

        var suggestion = PlatformMappingSuggester.Suggest([row, other], Options)[0].Suggestion;

        Assert.Equal(new PlatformMappingSuggestion(3, "Database Account", 13, "Local Database", "OtherSource", "CyberArk Privilege Cloud (SaaS)"), suggestion);
    }

    [Fact]
    public void GroupPlatformsAndMappedPlatforms_GetNone()
    {
        var group = Unmapped("WinDomain", platformType: "Group");
        var mapped = Mapped("WinServerLocal", 2, "Local/OS Account", 12, "Local OS", "CyberArk Self-Hosted Vault");

        var result = PlatformMappingSuggester.Suggest([group, mapped], Options);

        Assert.All(result, r => Assert.Null(r.Suggestion));
    }

    [Fact]
    public void ATypeThatNoLongerExists_IsNotSuggested()
    {
        var options = Options with { AccountTypes = [new(2, "Local/OS Account")] };
        Assert.Null(PlatformMappingSuggester.Suggest([Unmapped("WinDomain")], options)[0].Suggestion);
    }
}
