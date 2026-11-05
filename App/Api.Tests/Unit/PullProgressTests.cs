using BlueTrack.Api.CyberArkDirect;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-337: what a pull records of where it is.</summary>
public class PullProgressTests
{
    private sealed class StepClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void EachStepEndsWhenTheNextBegins_AndCountsItsItems()
    {
        var clock = new StepClock();
        var progress = new PullProgress(clock);
        progress.Begin(PullProgress.Steps.Safes);
        progress.SetTotal(55);
        progress.Advance(50);
        progress.Advance(5);
        clock.Now = clock.Now.AddMinutes(2);
        progress.Begin(PullProgress.Steps.AccountDetails, 2454);
        progress.Advance();

        var (_, steps) = progress.Snapshot();
        Assert.Equal(
            [new PullStepRecord(1, "Safes", new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 5, 9, 2, 0), 55, 55),
             new PullStepRecord(2, "AccountDetails", new DateTime(2026, 11, 5, 9, 2, 0), null, 1, 2454)],
            steps);
        Assert.Equal(" It stopped during: Account details (1 of 2,454).", progress.StoppedNote());
    }

    [Fact]
    public void AStepWithNoCount_IsNamedAlone_AndACompletedPullHasNoStoppedNote()
    {
        var progress = new PullProgress(new StepClock());
        Assert.Equal("", progress.StoppedNote());
        progress.Begin(PullProgress.Steps.Logon);
        Assert.Equal(" It stopped during: Logging on to PVWA.", progress.StoppedNote());
        progress.Complete();
        Assert.Equal("", progress.StoppedNote());
        Assert.NotNull(progress.Snapshot().Steps[0].CompletedAt);
    }

    [Fact]
    public void ACountWithoutATotal_SaysHowManyWereRead()
    {
        var progress = new PullProgress(new StepClock());
        progress.Begin(PullProgress.Steps.Accounts);
        progress.Advance(1000);
        Assert.Equal(" It stopped during: Accounts (1,000 read).", progress.StoppedNote());
    }

    [Fact]
    public void TheVersionChanges_OnlyWhenSomethingDoes()
    {
        var progress = new PullProgress(new StepClock());
        progress.Begin(PullProgress.Steps.Users, 3);
        var first = progress.Snapshot().Version;
        Assert.Equal(first, progress.Snapshot().Version);
        progress.Advance();
        Assert.NotEqual(first, progress.Snapshot().Version);
    }

    [Fact]
    public void ParallelCalls_AreAllCounted()
    {
        var progress = new PullProgress(new StepClock());
        progress.Begin(PullProgress.Steps.SafeMembers, 1000);
        Parallel.For(0, 1000, _ => progress.Advance());
        Assert.Equal(1000, progress.Snapshot().Steps[0].ItemsDone);
    }
}
