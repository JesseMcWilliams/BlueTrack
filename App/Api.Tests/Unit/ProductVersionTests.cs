using BlueTrack.Api.Controllers;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-226: the version About and the Deployment page show -- the build's informational version, else the assembly version.</summary>
public class ProductVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0-dev.12+abc1234", "1.0.0-dev.12+abc1234")]
    [InlineData("1.0.0-dev.12+abc1234.dirty", "1.0.0-dev.12+abc1234.dirty")]
    [InlineData("1.0.0-dev+abc1234", "1.0.0-dev+abc1234")]
    [InlineData("  1.2.3  ", "1.2.3")]
    public void InformationalVersion_IsShownAsIs(string informational, string expected)
    {
        Assert.Equal(expected, AboutController.ProductVersion(informational, new Version(9, 9, 9, 0)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoInformationalVersion_FallsBackToTheAssemblyVersion(string? informational)
    {
        Assert.Equal("1.4.2", AboutController.ProductVersion(informational, new Version(1, 4, 2, 0)));
        Assert.Equal("2.0", AboutController.ProductVersion(informational, new Version(2, 0)));
    }

    [Fact]
    public void NeitherVersion_IsUnknown()
    {
        Assert.Equal("unknown", AboutController.ProductVersion(null, null));
    }
}
