using System.Security.Claims;
using BlueTrack.Api.Auth;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

public class ExternalIdentifierReaderTests
{
    [Fact]
    public void Resolve_PrefersNameIdentifierClaim_OverIdentityName()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "sub-12345"),
                new Claim(ClaimTypes.Name, "someone@example.com")
            ],
            authenticationType: "Test"));

        Assert.Equal("sub-12345", ExternalIdentifierReader.Resolve(principal));
    }

    [Fact]
    public void Resolve_NoNameIdentifier_FallsBackToIdentityName()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "TestUser.Viewer")],
            authenticationType: "Test"));

        Assert.Equal("TestUser.Viewer", ExternalIdentifierReader.Resolve(principal));
    }

    [Fact]
    public void Resolve_NoClaimsAtAll_ReturnsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Null(ExternalIdentifierReader.Resolve(principal));
    }
}
