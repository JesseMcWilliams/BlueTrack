using System.Security.Claims;
using BlueTrack.Api.Auth;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>Found live 2026-10-13: Authentik's OIDC "name" claim was "" and the user's name was stored empty.</summary>
public class DisplayNameForTests
{
    private static ClaimsPrincipal With(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "OIDC"));

    [Fact]
    public void ABlankNameClaim_FallsBackToPreferredUsername()
    {
        Assert.Equal("AI_Admin_User", CurrentUserResolver.DisplayNameFor(With(("name", ""), ("preferred_username", "AI_Admin_User")), "sub-123"));
    }

    [Fact]
    public void ANameClaim_WinsWhenItHasAValue()
    {
        Assert.Equal("Jesse McWilliams", CurrentUserResolver.DisplayNameFor(With(("name", "Jesse McWilliams"), ("preferred_username", "adm.jesse")), "sub-123"));
    }

    [Fact]
    public void NoUsableClaim_FallsBackToTheIdentifier()
    {
        Assert.Equal("sub-123", CurrentUserResolver.DisplayNameFor(With(("name", "  ")), "sub-123"));
    }
}
