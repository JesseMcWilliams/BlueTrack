using BlueTrack.Api.Data;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>web.user_preference (Design_Accessibility_And_Theming.md, D-93) -- a generalized per-user key/value preference store, Theme being the first consumer.</summary>
public class UserPreferenceRepositoryTests
{
    private static UserPreferenceRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task GetAllForUserAsync_NoPreferencesSet_ReturnsEmpty()
    {
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser2");
        var repository = CreateRepository();

        var preferences = await repository.GetAllForUserAsync(userKey);

        Assert.Empty(preferences);
    }

    [Fact]
    public async Task SetAsync_ThenGetAllForUserAsync_RoundTrips()
    {
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = CreateRepository();

        try
        {
            await repository.SetAsync(userKey, "Theme", "Dark");

            var preferences = await repository.GetAllForUserAsync(userKey);
            Assert.Equal("Dark", preferences["Theme"]);
        }
        finally
        {
            await ClearPreferenceAsync(userKey, "Theme");
        }
    }

    [Fact]
    public async Task SetAsync_CalledTwice_UpdatesTheExistingRowRatherThanDuplicating()
    {
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = CreateRepository();

        try
        {
            await repository.SetAsync(userKey, "Theme", "Dark");
            await repository.SetAsync(userKey, "Theme", "HighVisibility");

            var preferences = await repository.GetAllForUserAsync(userKey);
            Assert.Equal("HighVisibility", preferences["Theme"]);
            Assert.Single(preferences);
        }
        finally
        {
            await ClearPreferenceAsync(userKey, "Theme");
        }
    }

    [Fact]
    public async Task GetAllForUserAsync_OnlyReturnsThisUsersOwnPreferences()
    {
        var user1Key = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var user2Key = await TestUsers.GetUserKeyAsync("IntegrationTestUser2");
        var repository = CreateRepository();

        try
        {
            await repository.SetAsync(user1Key, "Theme", "Dark");

            var user2Preferences = await repository.GetAllForUserAsync(user2Key);
            Assert.False(user2Preferences.ContainsKey("Theme"));
        }
        finally
        {
            await ClearPreferenceAsync(user1Key, "Theme");
        }
    }

    private static async Task ClearPreferenceAsync(int userKey, string preferenceKey)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        await Dapper.SqlMapper.ExecuteAsync(connection,
            "DELETE FROM web.user_preference WHERE UserKey = @UserKey AND PreferenceKey = @PreferenceKey",
            new { UserKey = userKey, PreferenceKey = preferenceKey });
    }
}
