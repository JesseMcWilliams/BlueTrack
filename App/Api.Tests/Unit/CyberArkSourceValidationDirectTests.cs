using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Models;
using BlueTrack.Api.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-248: the checks on the Self-Hosted Direct settings.</summary>
public class CyberArkSourceValidationDirectTests
{
    private static readonly AllowedFileRoots NoRoots = new(new ConfigurationBuilder().Build());

    private static CyberArkSourceSettings Direct(string? url = "https://pvwa.example.com", int? credential = 3, string method = "CyberArk",
        string pullTime = "01:30", int percent = 20, bool selfHosted = true) => new()
    {
        ImportSelfHosted = selfHosted,
        SelfHostedSource = "Direct",
        PvwaBaseUrl = url,
        PvwaCredentialKey = credential,
        PvwaLogonMethod = method,
        DirectPullTime = pullTime,
        DirectMaxChangePercent = percent
    };

    private static IReadOnlyList<string> Check(CyberArkSourceSettings s) =>
        CyberArkSourceValidation.Validate(CyberArkSourceValidation.Normalize(s), NoRoots);

    [Fact]
    public void ADirectSource_WithAddressCredentialAndDefaults_IsValid() => Assert.Empty(Check(Direct()));

    [Theory]
    [InlineData("https://pvwa.example.com")]
    [InlineData("https://pvwa.example.com/")]
    [InlineData("https://pvwa.example.com/PasswordVault")]
    [InlineData("https://pvwa.example.com:8443/PasswordVault/")]
    public void Address_InEitherForm_IsAccepted(string url) => Assert.Empty(Check(Direct(url)));

    [Theory]
    [InlineData("http://pvwa.example.com")]                 // https only: the credential goes over this connection
    [InlineData("pvwa.example.com")]
    [InlineData("https://pvwa.example.com/other")]
    [InlineData("https://pvwa.example.com/PasswordVault/API")]
    [InlineData("https://pvwa.example.com/?x=1")]
    [InlineData("https://pvwa.example.com/#frag")]
    public void Address_OfTheWrongShape_IsRejected(string url) =>
        Assert.Contains(Check(Direct(url)), e => e.StartsWith("PVWA address must be https://host"));

    [Fact]
    public void MissingAddressAndCredential_AreBothRequired()
    {
        var errors = Check(Direct(url: null, credential: null));

        Assert.Contains("PVWA address is required for Self-Hosted Direct.", errors);
        Assert.Contains("A credential is required for Self-Hosted Direct.", errors);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("SAML")]
    [InlineData("")]
    public void OnlyCyberArkAndLdapLogon_AreOffered(string method) =>
        Assert.Contains("PVWA logon method must be CyberArk or LDAP.", Check(Direct(method: method)));

    [Fact]
    public void LdapLogon_IsAccepted() => Assert.Empty(Check(Direct(method: "LDAP")));

    [Theory]
    [InlineData("25:00")]
    [InlineData("1:30pm")]
    [InlineData("")]
    public void PullTime_MustBeHHmm(string time) =>
        Assert.Contains("Pull time must be a time as HH:mm (24-hour).", Check(Direct(pullTime: time)));

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void AllowedChange_MustBeZeroToOneHundred(int percent) =>
        Assert.Contains("Allowed change must be between 0 and 100 percent.", Check(Direct(percent: percent)));

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void AllowedChange_AtTheEnds_IsAccepted(int percent) => Assert.Empty(Check(Direct(percent: percent)));

    [Fact]
    public void UnknownSource_IsRejected()
    {
        var errors = Check(new CyberArkSourceSettings { ImportSelfHosted = true, SelfHostedSource = "Rest", EvdDatabaseName = "CyberArkSH" });

        Assert.Contains("Self-Hosted source must be Evd or Direct.", errors);
    }

    [Fact]
    public void Evd_IsStillTheDefault_AndNeedsItsDatabase()
    {
        Assert.Equal("Evd", new CyberArkSourceSettings().SelfHostedSource);
        Assert.Contains("EVD database is required for Self-Hosted.", Check(new CyberArkSourceSettings { ImportSelfHosted = true }));
        Assert.Empty(Check(new CyberArkSourceSettings { ImportSelfHosted = true, EvdDatabaseName = "CyberArkSH" }));
    }

    [Fact]
    public void Normalize_DropsTheValuesOfTheSourceNotInUse()
    {
        var direct = CyberArkSourceValidation.Normalize(new CyberArkSourceSettings
        {
            ImportSelfHosted = true, SelfHostedSource = "Direct", EvdDatabaseName = "CyberArkSH", PvwaBaseUrl = " https://pvwa.example.com/ ", PvwaCredentialKey = 3
        });
        var evd = CyberArkSourceValidation.Normalize(new CyberArkSourceSettings
        {
            ImportSelfHosted = true, SelfHostedSource = "Evd", EvdDatabaseName = "CyberArkSH", PvwaBaseUrl = "https://pvwa.example.com", PvwaCredentialKey = 3
        });
        var off = CyberArkSourceValidation.Normalize(new CyberArkSourceSettings
        {
            ImportPrivilegeCloud = true, ExportFolderPath = "D:\\x", ImportSelfHosted = false, SelfHostedSource = "Direct", PvwaBaseUrl = "https://pvwa.example.com", PvwaCredentialKey = 3
        });

        Assert.Null(direct.EvdDatabaseName);
        Assert.Equal("https://pvwa.example.com", direct.PvwaBaseUrl);
        Assert.Equal(3, direct.PvwaCredentialKey);
        Assert.Equal("CyberArkSH", evd.EvdDatabaseName);
        Assert.Null(evd.PvwaBaseUrl);
        Assert.Null(evd.PvwaCredentialKey);
        Assert.Null(off.PvwaBaseUrl);
        Assert.Null(off.PvwaCredentialKey);
    }

    [Fact]
    public void ADirectSettingForASourceThatIsOff_IsNotValidated()
    {
        // Privilege Cloud only; the unused Direct fields hold nothing that could fail.
        var settings = new CyberArkSourceSettings { ImportPrivilegeCloud = true, ExportFolderPath = "D:\\Exports", ImportSelfHosted = false, SelfHostedSource = "Direct", PvwaBaseUrl = "http://bad" };

        Assert.Empty(Check(settings));
    }

    [Fact]
    public void OptionsOffered_AreTheFourSources_WithPrivilegeCloudDirectPlanned()
    {
        var options = CyberArkSourceOption.All;

        Assert.Equal(["SelfHostedEvd", "SelfHostedDirect", "PrivilegeCloudFolder", "PrivilegeCloudDirect"], options.Select(o => o.Key));
        Assert.Equal([true, true, true, false], options.Select(o => o.Available));
    }
}
