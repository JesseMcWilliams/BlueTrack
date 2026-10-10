using Microsoft.Extensions.Configuration;
using BlueTrack.Api.Security;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-197: which folders data feeds may read and backups may be written to.</summary>
public class AllowedFileRootsTests
{
    private static AllowedFileRoots With(params string[] roots)
    {
        var settings = roots.Select((root, i) => new KeyValuePair<string, string?>($"FileAccess:AllowedRoots:{i}", root));
        return new AllowedFileRoots(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Theory]
    [InlineData(@"D:\Feeds")]
    [InlineData(@"C:\Data\Exports\")]
    public void WithoutRoots_AnyLocalFolderIsAllowed(string folder) => Assert.Null(With().Check(folder));

    [Theory]
    [InlineData(@"\\attacker\share")]
    [InlineData(@"\\fileserver\feeds\bluetrack")]
    public void WithoutRoots_UncFoldersAreRefused(string folder) => Assert.Contains("UNC", With().Check(folder));

    [Theory]
    [InlineData("")]
    [InlineData("relative\\folder")]
    [InlineData(null)]
    public void ARelativeOrEmptyPath_IsRefused(string? folder) => Assert.NotNull(With().Check(folder));

    [Theory]
    [InlineData(@"D:\Feeds")]
    [InlineData(@"d:\feeds\today")]
    [InlineData(@"\\fileserver\feeds\bluetrack\daily")]
    public void WithRoots_TheRootsAndFoldersInsideThemAreAllowed(string folder) =>
        Assert.Null(With(@"D:\Feeds\", @"\\fileserver\feeds\bluetrack").Check(folder));

    [Theory]
    [InlineData(@"D:\FeedsOther")]                        // shares a prefix, but isn't inside
    [InlineData(@"D:\Feeds\..\Windows")]                  // climbs out
    [InlineData(@"C:\Data")]                              // local but not listed
    [InlineData(@"\\attacker\share")]
    [InlineData(@"\\fileserver\feeds\bluetrack-other")]
    public void WithRoots_EverythingElseIsRefused(string folder) =>
        Assert.NotNull(With(@"D:\Feeds", @"\\fileserver\feeds\bluetrack").Check(folder));
}
