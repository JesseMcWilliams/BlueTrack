using BlueTrack.Api.Auth;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>
/// D-179: SID -> DOMAIN\Group for the Group → Role Mapping list. Uses
/// well-known SIDs, which resolve on any Windows host without a domain
/// (names assume an English-language Windows install, as on the CI host).
/// </summary>
public class WindowsGroupResolverTests
{
    [Fact]
    public void TryGetAccountName_WellKnownSid_ReturnsAccountName()
    {
        Assert.Equal(@"BUILTIN\Administrators", WindowsGroupResolver.TryGetAccountName("S-1-5-32-544"));
    }

    [Fact]
    public void TryGetAccountName_WellFormedButUnknownSid_ReturnsNull()
    {
        Assert.Null(WindowsGroupResolver.TryGetAccountName("S-1-5-32-999999"));
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("")]
    public void TryGetAccountName_NotASid_ReturnsNull(string value)
    {
        Assert.Null(WindowsGroupResolver.TryGetAccountName(value));
    }

    [Fact]
    public void TryResolve_ThenTryGetAccountName_RoundTrips()
    {
        var resolved = WindowsGroupResolver.TryResolve(@"BUILTIN\Users");

        Assert.NotNull(resolved);
        Assert.Equal(resolved.Value.ResolvedAccountName, WindowsGroupResolver.TryGetAccountName(resolved.Value.Sid));
    }
}
