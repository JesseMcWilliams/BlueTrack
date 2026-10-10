using System.Security.Claims;
using BlueTrack.Api.Auth;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-197: an OIDC/SAML provider's own claims never set BlueTrack's permissions or provider marker.</summary>
public class ExternalIdentityClaimsTests
{
    [Fact]
    public void MarkProvider_DropsPermissionAndProviderClaimsTheProviderSent_ThenAddsItsOwnMarker()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "alice@example.com"),
            new Claim(PermissionClaimsTransformation.PermissionClaimType, "ManageRolesAndPermissions"),
            new Claim(BlueTrackClaimTypes.ProviderType, "DevFakeAuth"),
        ], "SAML");
        var principal = new ClaimsPrincipal(identity);

        ExternalIdentityClaims.MarkProvider(principal, "SAML");

        Assert.DoesNotContain(principal.Claims, c => c.Type == PermissionClaimsTransformation.PermissionClaimType);
        Assert.Equal("SAML", Assert.Single(principal.FindAll(BlueTrackClaimTypes.ProviderType)).Value);
        Assert.Equal("alice@example.com", principal.FindFirst(ClaimTypes.Name)!.Value);
    }
}
