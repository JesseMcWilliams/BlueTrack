using BlueTrack.Api.Data;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>D-50 pessimistic locking, against real BlueTrackTest.</summary>
public class AccountProgressLockRepositoryTests
{
    private static async Task<(long AccountKey, int User1, int User2)> GetFixtureAsync()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var user1 = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var user2 = await TestUsers.GetUserKeyAsync("IntegrationTestUser2");
        // ForceReleaseAsync, not ReleaseAsync(accountKey, user1/user2) --
        // TestAccount03 is a shared fixture also used by the Playwright E2E
        // suite and manual interactive testing under real DevFakeAuth
        // identities (e.g. TestUser.Approver), none of which match either
        // synthetic integration-test user. ReleaseAsync only releases a
        // lock held by the exact user passed in (by design -- see
        // ReleaseAsync_ByNonHolder_DoesNotReleaseTheHoldersLock below), so
        // a lock left by anyone else silently survived this "clean slate"
        // step and failed GetStatusAsync_NoLock_ReturnsNull with someone
        // else's lock still in place. Found 2026-09-23 via exactly that
        // failure, traced to leftover manual-testing state. ForceReleaseAsync
        // clears the row unconditionally, regardless of who (if anyone)
        // holds it.
        await new AccountProgressLockRepository(new TestDbConnectionFactory()).ForceReleaseAsync(accountKey);
        return (accountKey, user1, user2);
    }

    [Fact]
    public async Task TryAcquireAsync_NoExistingLock_GrantsToCaller()
    {
        var (accountKey, user1, user2) = await GetFixtureAsync();
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());

        var status = await repository.TryAcquireAsync(accountKey, user1);

        Assert.NotNull(status);
        Assert.Equal(user1, status!.LockedByUserKey);

        await repository.ReleaseAsync(accountKey, user1);
    }

    [Fact]
    public async Task TryAcquireAsync_AlreadyLockedByAnotherUser_ReturnsExistingHolder()
    {
        var (accountKey, user1, user2) = await GetFixtureAsync();
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        await repository.TryAcquireAsync(accountKey, user1);

        var status = await repository.TryAcquireAsync(accountKey, user2);

        Assert.NotNull(status);
        Assert.Equal(user1, status!.LockedByUserKey);
        Assert.NotEqual(user2, status.LockedByUserKey);

        await repository.ReleaseAsync(accountKey, user1);
    }

    [Fact]
    public async Task HeartbeatAsync_ByLockHolder_Succeeds()
    {
        var (accountKey, user1, _) = await GetFixtureAsync();
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        await repository.TryAcquireAsync(accountKey, user1);

        var refreshed = await repository.HeartbeatAsync(accountKey, user1);

        Assert.True(refreshed);
        await repository.ReleaseAsync(accountKey, user1);
    }

    [Fact]
    public async Task HeartbeatAsync_ByNonHolder_Fails()
    {
        var (accountKey, user1, user2) = await GetFixtureAsync();
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        await repository.TryAcquireAsync(accountKey, user1);

        var refreshed = await repository.HeartbeatAsync(accountKey, user2);

        Assert.False(refreshed);
        await repository.ReleaseAsync(accountKey, user1);
    }

    [Fact]
    public async Task ReleaseAsync_ByNonHolder_DoesNotReleaseTheHoldersLock()
    {
        var (accountKey, user1, user2) = await GetFixtureAsync();
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        await repository.TryAcquireAsync(accountKey, user1);

        await repository.ReleaseAsync(accountKey, user2);
        var status = await repository.GetStatusAsync(accountKey);

        Assert.NotNull(status);
        Assert.Equal(user1, status!.LockedByUserKey);

        await repository.ReleaseAsync(accountKey, user1);
    }

    [Fact]
    public async Task ForceReleaseAsync_ReleasesRegardlessOfHolder()
    {
        var (accountKey, user1, _) = await GetFixtureAsync();
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        await repository.TryAcquireAsync(accountKey, user1);

        await repository.ForceReleaseAsync(accountKey);
        var status = await repository.GetStatusAsync(accountKey);

        Assert.Null(status);
    }

    [Fact]
    public async Task GetStatusAsync_NoLock_ReturnsNull()
    {
        var (accountKey, _, _) = await GetFixtureAsync();
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());

        var status = await repository.GetStatusAsync(accountKey);

        Assert.Null(status);
    }
}
