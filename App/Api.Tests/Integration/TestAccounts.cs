using Dapper;
using Microsoft.Data.SqlClient;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Looks up the synthetic accounts seeded by
/// Database/Test/02_BlueTrack_Test_SyntheticAccountData.sql by name, since
/// their AccountKey (an IDENTITY column) isn't stable across a database
/// rebuild -- never hardcode the key in a test.
/// </summary>
public static class TestAccounts
{
    public static async Task<long> GetAccountKeyAsync(string sourceAccountId)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<long>(
            "SELECT AccountKey FROM dbo.fact_account WHERE SourceAccountId = @SourceAccountId",
            new { SourceAccountId = sourceAccountId });
    }
}
