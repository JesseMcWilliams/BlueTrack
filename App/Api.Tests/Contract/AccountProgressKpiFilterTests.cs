using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// UI review W7-1: the Accounts list's <c>kpi</c> filter selects the same accounts the KPI Summary counts (D-143), so a
/// KPI count can open the accounts behind it. Each filter's matching count must equal the KPI Summary's own number.
/// </summary>
public class AccountProgressKpiFilterTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Viewer()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
        return client;
    }

    // "Total" and "In scope" count every inventory account (D-143), including one with no Account Progress row, which the
    // list can't show (it is the progress list). So those two match the list minus exactly those accounts; the stage-based
    // counts need a progress row and match exactly.
    [Theory]
    [InlineData("total", "totalAccounts", true)]
    [InlineData("inScope", "inScopeAccounts", true)]
    [InlineData("onboarded", "onboardedAccounts", false)]
    [InlineData("managed", "managedAccounts", false)]
    [InlineData("compliant", "compliantAccounts", false)]
    public async Task KpiFilter_MatchesTheKpiSummaryCount(string kpi, string summaryField, bool inventoryCount)
    {
        var client = Viewer();
        var summary = await client.GetFromJsonAsync<JsonObject>("/api/reports/kpi-summary");

        var response = await client.GetAsync($"/api/account-progress?kpi={kpi}&pageSize=1");
        response.EnsureSuccessStatusCode();
        var listed = int.Parse(response.Headers.GetValues("X-Filtered-Count").Single());

        var untracked = 0;
        if (inventoryCount)
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            untracked = await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.fact_account fa
                WHERE fa.IsDeleted = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)
                  AND NOT EXISTS (SELECT 1 FROM dbo.fact_account_progress p WHERE p.AccountKey = fa.AccountKey)
                """);
        }

        Assert.Equal(summary![summaryField]!.GetValue<int>(), listed + untracked);
    }

    [Fact]
    public async Task KpiFilter_UnknownValue_IsIgnored()
    {
        var client = Viewer();
        var all = await client.GetAsync("/api/account-progress?pageSize=1");
        var unknown = await client.GetAsync("/api/account-progress?kpi=nonsense&pageSize=1");

        Assert.Equal(all.Headers.GetValues("X-Filtered-Count").Single(), unknown.Headers.GetValues("X-Filtered-Count").Single());
    }
}
