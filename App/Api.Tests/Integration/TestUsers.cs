using Dapper;
using Microsoft.Data.SqlClient;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Looks up the synthetic app_user rows seeded by
/// Database/Test/02_BlueTrack_Test_SyntheticAccountData.sql
/// (IntegrationTestUser1/2) by their stable ExternalIdentifier, since
/// UserKey is an IDENTITY column shared with whatever contract tests have
/// lazily created for TestUser.* -- never hardcode the key.
/// </summary>
public static class TestUsers
{
    public static async Task<int> GetUserKeyAsync(string externalIdentifier)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>(
            "SELECT UserKey FROM web.app_user WHERE ExternalIdentifier = @ExternalIdentifier",
            new { ExternalIdentifier = externalIdentifier });
    }
}
