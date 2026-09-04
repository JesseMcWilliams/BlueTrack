using BlueTrack.Api.Auth;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

public class ProviderSettingsReaderTests
{
    [Fact]
    public void ReadGroupClaimType_Oidc_ReturnsConfiguredClaimType()
    {
        var provider = new IdentityProviderConfig
        {
            ProviderType = "OIDC",
            DisplayName = "Test OIDC",
            ConfigurationValues = """{"Authority":"https://idp.example.com","ClientId":"abc","GroupsClaimType":"groups"}"""
        };

        Assert.Equal("groups", ProviderSettingsReader.ReadGroupClaimType(provider));
    }

    [Fact]
    public void ReadGroupClaimType_Saml_ReturnsConfiguredClaimType()
    {
        var provider = new IdentityProviderConfig
        {
            ProviderType = "SAML",
            DisplayName = "Test SAML",
            ConfigurationValues = """{"GroupClaimType":"http://example.com/claims/Group"}"""
        };

        Assert.Equal("http://example.com/claims/Group", ProviderSettingsReader.ReadGroupClaimType(provider));
    }

    [Fact]
    public void ReadGroupClaimType_UnknownProviderType_ReturnsEmpty()
    {
        var provider = new IdentityProviderConfig
        {
            ProviderType = "WindowsIntegrated",
            DisplayName = "Windows",
            ConfigurationValues = """{"GroupsClaimType":"groups"}"""
        };

        Assert.Equal("", ProviderSettingsReader.ReadGroupClaimType(provider));
    }

    [Fact]
    public void ReadGroupClaimType_NullConfigurationValues_ReturnsEmpty()
    {
        var provider = new IdentityProviderConfig
        {
            ProviderType = "OIDC",
            DisplayName = "Test OIDC",
            ConfigurationValues = null
        };

        Assert.Equal("", ProviderSettingsReader.ReadGroupClaimType(provider));
    }

    [Fact]
    public void ReadGroupClaimType_MalformedJson_FailsSoftToEmpty()
    {
        var provider = new IdentityProviderConfig
        {
            ProviderType = "OIDC",
            DisplayName = "Test OIDC",
            ConfigurationValues = "{ not valid json"
        };

        Assert.Equal("", ProviderSettingsReader.ReadGroupClaimType(provider));
    }

    [Fact]
    public void ReadOidc_MalformedJson_ReturnsNull()
    {
        Assert.Null(ProviderSettingsReader.ReadOidc("{ not valid json"));
    }

    [Fact]
    public void ReadOidc_ValidJson_ReturnsSettings()
    {
        var settings = ProviderSettingsReader.ReadOidc("""{"Authority":"https://idp.example.com","ClientId":"abc"}""");

        Assert.NotNull(settings);
        Assert.Equal("https://idp.example.com", settings!.Authority);
        Assert.Equal("abc", settings.ClientId);
    }

    [Fact]
    public void ReadSaml_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(ProviderSettingsReader.ReadSaml(null));
        Assert.Null(ProviderSettingsReader.ReadSaml("   "));
    }
}
