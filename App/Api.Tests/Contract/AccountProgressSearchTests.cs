using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-191: an account with neither a username nor an address (e.g. a
/// Privilege Cloud key object) can still be found by the Account Progress
/// search, which also matches the account name.
/// </summary>
public class AccountProgressSearchTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountProgressSearchTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Search_FindsAnAccountWithNoUsernameOrAddress_ByItsAccountName()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var accountName = $"hash_key_{tag}";
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DECLARE @src INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY');
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, Address, IsDeleted)
            VALUES (@src, @id, @accountName, NULL, NULL, 0);
            INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
            SELECT AccountKey,
                   (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered'),
                   (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started')
            FROM fact_account WHERE SourceAccountId = @id;
            """, new { id = $"search-{tag}", accountName });

        try
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
            var results = await client.GetFromJsonAsync<JsonArray>($"/api/account-progress?search=hash_key_{tag}");
            var row = Assert.Single(results!);
            Assert.Equal(accountName, row!["accountName"]!.GetValue<string>());
            Assert.Null(row["userName"]);
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM fact_account_progress WHERE AccountKey IN (SELECT AccountKey FROM fact_account WHERE SourceAccountId = @id);
                DELETE FROM fact_account WHERE SourceAccountId = @id;
                """, new { id = $"search-{tag}" });
        }
    }
}
