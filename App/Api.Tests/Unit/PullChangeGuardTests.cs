using BlueTrack.Api.CyberArkDirect;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-248: the Self-Hosted Direct change guard (Safe and account counts against the last good pull).</summary>
public class PullChangeGuardTests
{
    private static PullCounts Counts(int safes, int accounts) => new(safes, accounts, 0, 0, 0, 0);

    [Fact]
    public void NoPreviousPull_IsAccepted() =>
        Assert.Null(PullChangeGuard.Evaluate(null, Counts(40, 2452), 20));

    [Theory]
    [InlineData(40, 2452, 40, 2452)]      // unchanged
    [InlineData(40, 2452, 38, 2500)]      // small moves either way
    [InlineData(40, 2452, 40, 1962)]      // exactly 20% down (490 of 2452 floors to 490)
    [InlineData(40, 2452, 40, 2942)]      // exactly 20% up
    public void ChangeWithinThePercentage_IsAccepted(int safesBefore, int accountsBefore, int safesAfter, int accountsAfter) =>
        Assert.Null(PullChangeGuard.Evaluate(Counts(safesBefore, accountsBefore), Counts(safesAfter, accountsAfter), 20));

    [Fact]
    public void AccountCountDroppingPastThePercentage_IsRejected_NamingTheCounts()
    {
        var reason = PullChangeGuard.Evaluate(Counts(40, 2452), Counts(40, 1000), 20);

        Assert.NotNull(reason);
        Assert.Contains("accounts went from 2,452 to 1,000", reason);
        Assert.DoesNotContain("Safes went", reason);
    }

    [Fact]
    public void SafeCountGrowingPastThePercentage_IsRejectedToo()
    {
        var reason = PullChangeGuard.Evaluate(Counts(40, 2452), Counts(60, 2452), 20);

        Assert.NotNull(reason);
        Assert.Contains("Safes went from 40 to 60", reason);
    }

    [Fact]
    public void BothCountsOver_ReportsBoth()
    {
        var reason = PullChangeGuard.Evaluate(Counts(40, 2452), Counts(10, 100), 20);

        Assert.Contains("Safes went from 40 to 10", reason);
        Assert.Contains("accounts went from 2,452 to 100", reason);
    }

    [Theory]
    [InlineData(3, 0, true)]       // a tiny Vault: losing every one of 3 Safes is within the always-allowed 5
    [InlineData(10, 5, true)]      // 5 of 10 is 50%, but 5 is always allowed
    [InlineData(10, 4, false)]     // 6 is over both 5 and 20% of 10 (2)
    [InlineData(20, 12, false)]    // 8 is over both 5 and 20% of 20 (4)
    public void SmallChanges_AreAlwaysAllowed(int before, int after, bool accepted) =>
        Assert.Equal(accepted, PullChangeGuard.Evaluate(Counts(before, 100), Counts(after, 100), 20) is null);

    [Fact]
    public void ZeroPercent_AllowsOnlyTheAlwaysAllowedChange()
    {
        Assert.Null(PullChangeGuard.Evaluate(Counts(40, 2452), Counts(40, 2457), 0));
        Assert.NotNull(PullChangeGuard.Evaluate(Counts(40, 2452), Counts(40, 2458), 0));
    }

    [Fact]
    public void HundredPercent_AcceptsAnyDrop() =>
        Assert.Null(PullChangeGuard.Evaluate(Counts(40, 2452), Counts(0, 0), 100));

    [Fact]
    public void UsersGroupsMembersAndPlatforms_AreNotCounted() =>
        Assert.Null(PullChangeGuard.Evaluate(new PullCounts(40, 2452, 50, 21, 700, 20), new PullCounts(40, 2452, 1, 1, 1, 1), 20));
}
