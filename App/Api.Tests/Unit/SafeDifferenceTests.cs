using BlueTrack.Api.CyberArkDirect;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-250: naming the Safes that disappeared from, or appeared in, a pull.</summary>
public class SafeDifferenceTests
{
    private static (long, string)[] Safes(params (long, string)[] s) => s;

    [Fact]
    public void SameSafes_SayNothing() =>
        Assert.Empty(SafeDifference.Describe(Safes((1, "A"), (2, "B")), Safes((2, "B"), (1, "A"))));

    [Fact]
    public void MissingAndNewSafes_AreNamedWithCounts_SortedIgnoringCase()
    {
        var lines = SafeDifference.Describe(Safes((1, "zeta"), (2, "Alpha"), (3, "kept")), Safes((3, "kept"), (4, "New One")));

        Assert.Equal(["Safes no longer returned (2): Alpha, zeta", "Safes new since the last good pull (1): New One"], lines);
    }

    [Fact]
    public void ARenamedSafe_IsNotMissing_BecauseTheNumberIsTheIdentity() =>
        Assert.Empty(SafeDifference.Describe(Safes((1, "Old name")), Safes((1, "New name"))));

    [Fact]
    public void MoreThanTwentyNames_AreCut_WithTheRestCounted()
    {
        var previous = Enumerable.Range(1, 23).Select(i => ((long)i, $"S{i:00}")).ToList();

        var line = Assert.Single(SafeDifference.Describe(previous, []));

        Assert.StartsWith("Safes no longer returned (23): S01, S02", line);
        Assert.EndsWith("S20 and 3 more", line);
        Assert.DoesNotContain("S21", line);
    }

    [Fact]
    public void WithNoPreviousSafes_EverySafeIsNew()
    {
        var line = Assert.Single(SafeDifference.Describe([], Safes((1, "A"))));

        Assert.Equal("Safes new since the last good pull (1): A", line);
    }
}
