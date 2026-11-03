using BlueTrack.Api.Auth;
using BlueTrack.Api.Ldap;
using BlueTrack.Api.Tests.Contract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-303: a Windows user's groups come from AD after a Reload Rights, with a stub directory in place of AD and the
/// real rights cache (web.distributed_cache). Each test uses its own identity and removes its marker.
/// </summary>
public class DirectoryGroupsTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private const string Domain = "S-1-5-21-1000-2000-3000";
    private const string UserSid = Domain + "-1105";
    private const int ProviderKey = 1;

    // The sign-in token: the user's domain groups (Domain Users, an old group) and two that aren't the domain's.
    private static readonly string[] Token = [Domain + "-513", Domain + "-2001", "S-1-1-0", "S-1-5-11"];

    private sealed class StubDirectory : IDirectoryGroupReader
    {
        public IReadOnlyList<string>? Groups { get; set; } = [Domain + "-513", Domain + "-2002"];
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }

        public Task<IReadOnlyList<string>?> ReadGroupSidsAsync(string userSid)
        {
            Reads++;
            return Failure is null ? Task.FromResult(Groups) : Task.FromException<IReadOnlyList<string>?>(Failure);
        }
    }

    private async Task WithIdentityAsync(Func<string, UserRightsCache, Task> test)
    {
        var identity = UserSid + "-test-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<UserRightsCache>();
        try
        {
            await test(identity, cache);
        }
        finally
        {
            await cache.ClearDirectoryGroupsTokenAsync(ProviderKey, identity);
        }
    }

    private static DirectoryGroups Groups(StubDirectory directory, UserRightsCache cache) => new(directory, cache, NullLogger<DirectoryGroups>.Instance);

    [Fact]
    public void Merge_ReplacesTheUsersDomainGroups_AndKeepsTheOthers()
    {
        var merged = GroupIdentifierExtractor.MergeDirectoryGroups(Token, UserSid, [Domain + "-513", Domain + "-2002"]);

        Assert.Equal(["S-1-1-0", "S-1-5-11", Domain + "-513", Domain + "-2002"], merged);
    }

    [Fact]
    public Task AReload_ReadsAd_AndKeepsReadingItOnLaterResolves_ForTheSameToken() => WithIdentityAsync(async (identity, cache) =>
    {
        var directory = new StubDirectory();
        var (groups, source) = await Groups(directory, cache).ResolveAsync(ProviderKey, identity, UserSid, Token, reload: true);
        Assert.Equal(GroupSources.Directory, source);
        Assert.Contains(Domain + "-2002", groups);
        Assert.DoesNotContain(Domain + "-2001", groups);

        directory.Groups = [Domain + "-513"];
        (groups, source) = await Groups(directory, cache).ResolveAsync(ProviderKey, identity, UserSid, Token, reload: false);
        Assert.Equal((GroupSources.Directory, 2), (source, directory.Reads));
        Assert.DoesNotContain(Domain + "-2002", groups);
    });

    [Fact]
    public Task WithoutAReload_TheSignInsGroupsAreUsed_AndAdIsNotAsked() => WithIdentityAsync(async (identity, cache) =>
    {
        var directory = new StubDirectory();
        var (groups, source) = await Groups(directory, cache).ResolveAsync(ProviderKey, identity, UserSid, Token, reload: false);

        Assert.Equal((GroupSources.WindowsSignIn, 0), (source, directory.Reads));
        Assert.Equal(Token, groups);
    });

    [Fact]
    public Task ANewWindowsSignIn_EndsTheAdReads() => WithIdentityAsync(async (identity, cache) =>
    {
        var directory = new StubDirectory();
        await Groups(directory, cache).ResolveAsync(ProviderKey, identity, UserSid, Token, reload: true);

        string[] newToken = [Domain + "-513", Domain + "-2002", "S-1-1-0", "S-1-5-11"];
        var (groups, source) = await Groups(directory, cache).ResolveAsync(ProviderKey, identity, UserSid, newToken, reload: false);
        Assert.Equal((GroupSources.WindowsSignIn, 1), (source, directory.Reads));
        Assert.Equal(newToken, groups);
        Assert.Null(await cache.GetDirectoryGroupsTokenAsync(ProviderKey, identity));
    });

    [Fact]
    public Task AnAdminsReload_ReadsAdAtTheUsersNextResolve_WhateverTheirToken() => WithIdentityAsync(async (identity, cache) =>
    {
        await cache.SetDirectoryGroupsTokenAsync(ProviderKey, identity, "");
        var directory = new StubDirectory();

        var (_, source) = await Groups(directory, cache).ResolveAsync(ProviderKey, identity, UserSid, Token, reload: false);

        Assert.Equal(GroupSources.Directory, source);
        Assert.NotEqual("", await cache.GetDirectoryGroupsTokenAsync(ProviderKey, identity));
    });

    [Fact]
    public Task WhenAdCantBeRead_TheSignInsGroupsAreUsed_AndSaySo() => WithIdentityAsync(async (identity, cache) =>
    {
        var directory = new StubDirectory { Failure = new InvalidOperationException("The server is not operational.") };

        var (groups, source) = await Groups(directory, cache).ResolveAsync(ProviderKey, identity, UserSid, Token, reload: true);

        Assert.Equal(GroupSources.DirectoryUnavailable, source);
        Assert.Equal(Token, groups);
    });

    [Fact]
    public Task WhenNoLdapDomainKnowsTheUser_TheSignInsGroupsAreUsed() => WithIdentityAsync(async (identity, cache) =>
    {
        var directory = new StubDirectory { Groups = null };

        var (groups, source) = await Groups(directory, cache).ResolveAsync(ProviderKey, identity, UserSid, Token, reload: true);

        Assert.Equal(GroupSources.WindowsSignIn, source);
        Assert.Equal(Token, groups);
        Assert.Null(await cache.GetDirectoryGroupsTokenAsync(ProviderKey, identity));
    });
}
