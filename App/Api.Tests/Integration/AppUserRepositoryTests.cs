using BlueTrack.Api.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// The web.app_user upsert-on-login step (Design_Authentication_Architecture.md
/// step 6). Uses the real DevFakeAuth provider row (seeded by
/// Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql) rather than a
/// fake ProviderKey, since ProviderKey has a real FK to web.identity_provider_config.
/// </summary>
public class AppUserRepositoryTests
{
    private static AppUserRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task UpsertOnLoginAsync_FirstCall_Inserts()
    {
        var repository = CreateRepository();
        var providerKey = await GetDevFakeAuthProviderKeyAsync();
        var externalIdentifier = $"IntegrationTest.AppUserRepo.{Guid.NewGuid():N}";

        try
        {
            var user = await repository.UpsertOnLoginAsync(providerKey, externalIdentifier, "First Login Display Name", "first@example.test");

            Assert.Equal(providerKey, user.ProviderKey);
            Assert.Equal(externalIdentifier, user.ExternalIdentifier);
            Assert.Equal("First Login Display Name", user.DisplayName);
            Assert.Equal(user.FirstLogin, user.LastLogin);
        }
        finally
        {
            await DeleteAppUserAsync(externalIdentifier);
        }
    }

    [Fact]
    public async Task UpsertOnLoginAsync_SecondCall_UpdatesDisplayNameAndLastLoginWithoutChangingUserKey()
    {
        var repository = CreateRepository();
        var providerKey = await GetDevFakeAuthProviderKeyAsync();
        var externalIdentifier = $"IntegrationTest.AppUserRepo.{Guid.NewGuid():N}";

        try
        {
            var firstLogin = await repository.UpsertOnLoginAsync(providerKey, externalIdentifier, "Original Name", "original@example.test");
            var secondLogin = await repository.UpsertOnLoginAsync(providerKey, externalIdentifier, "Updated Name", "updated@example.test");

            Assert.Equal(firstLogin.UserKey, secondLogin.UserKey);
            Assert.Equal(firstLogin.FirstLogin, secondLogin.FirstLogin);
            Assert.Equal("Updated Name", secondLogin.DisplayName);
            Assert.Equal("updated@example.test", secondLogin.Email);
        }
        finally
        {
            await DeleteAppUserAsync(externalIdentifier);
        }
    }

    [Fact]
    public async Task GetByKeyAsync_KnownUser_ReturnsMatchingRow()
    {
        var repository = CreateRepository();
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");

        var user = await repository.GetByKeyAsync(userKey);

        Assert.NotNull(user);
        Assert.Equal("IntegrationTestUser1", user!.ExternalIdentifier);
    }

    [Fact]
    public async Task GetByKeyAsync_UnknownKey_ReturnsNull()
    {
        var repository = CreateRepository();

        var user = await repository.GetByKeyAsync(-1);

        Assert.Null(user);
    }

    private static async Task<int> GetDevFakeAuthProviderKeyAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>(
            "SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth'");
    }

    private static async Task DeleteAppUserAsync(string externalIdentifier)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM web.app_user WHERE ExternalIdentifier = @ExternalIdentifier", new { ExternalIdentifier = externalIdentifier });
    }
}
