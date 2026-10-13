using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-211: dbo.fn_ExpandDateTokens, which usp_Import_Configured uses to turn
/// the CyberArk file names into the night's file names. It must agree with
/// the API's FeedFileResolver.ExpandPattern, which the page's Test uses.
/// </summary>
public class ExpandDateTokensTests
{
    [Theory]
    [InlineData("Export Entitlements {yyyy-MM-dd}.csv")]
    [InlineData("a{yyyyMMdd}b{MM}.csv")]
    [InlineData("Export_SafesList.csv")]
    public async Task MatchesTheApisExpansion(string pattern)
    {
        var date = new DateTime(2026, 3, 7);
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);

        var expanded = await connection.QuerySingleAsync<string>("SELECT dbo.fn_ExpandDateTokens(@pattern, @date)", new { pattern, date = date.Date });

        Assert.Equal(BlueTrack.Api.DataFeeds.FeedFileResolver.ExpandPattern(pattern, date), expanded);
    }
}
