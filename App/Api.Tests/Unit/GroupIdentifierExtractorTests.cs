using System.Security.Claims;
using BlueTrack.Api.Auth;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

public class GroupIdentifierExtractorTests
{
    [Fact]
    public void GetDevFakeAuthIdentifiers_UsesIdentityName()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "TestUser.Approver")],
            authenticationType: "Test"));

        var result = GroupIdentifierExtractor.GetDevFakeAuthIdentifiers(principal);

        var identifier = Assert.Single(result);
        Assert.Equal("TestUser.Approver", identifier);
    }

    [Fact]
    public void GetDevFakeAuthIdentifiers_NoName_ReturnsEmpty()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Empty(GroupIdentifierExtractor.GetDevFakeAuthIdentifiers(principal));
    }

    [Fact]
    public void GetClaimBasedGroupIdentifiers_ReturnsAllMatchingClaims()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("groups", "GroupA"),
                new Claim("groups", "GroupB"),
                new Claim("other", "Ignored")
            ],
            authenticationType: "Test"));

        var result = GroupIdentifierExtractor.GetClaimBasedGroupIdentifiers(principal, "groups");

        Assert.Equal(["GroupA", "GroupB"], result);
    }

    [Fact]
    public void GetClaimBasedGroupIdentifiers_EmptyClaimType_ReturnsEmpty()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("groups", "GroupA")],
            authenticationType: "Test"));

        Assert.Empty(GroupIdentifierExtractor.GetClaimBasedGroupIdentifiers(principal, ""));
    }

    [Fact]
    public void GetGroupIdentifiers_NonWindowsIdentity_ReturnsEmpty()
    {
        // WindowsIntegrated's own path reads SIDs off a real WindowsIdentity
        // (not constructible from arbitrary data in a test) -- what's
        // testable, and worth guarding, is that a non-Windows principal
        // never falls through to returning something that looks like a
        // group SID list.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "someone")],
            authenticationType: "Test"));

        Assert.Empty(GroupIdentifierExtractor.GetGroupIdentifiers(principal));
    }
}
