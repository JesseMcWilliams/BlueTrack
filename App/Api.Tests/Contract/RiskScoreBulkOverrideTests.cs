using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-315: the Risk Score report's bulk override, and its "Select all matching" keys.</summary>
public class RiskScoreBulkOverrideTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static async Task<int?> OverrideOfAsync(long accountKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<int?>("SELECT OverrideRiskScore FROM web.account_risk_score WHERE AccountKey = @accountKey", new { accountKey });
    }

    [Fact]
    public async Task SetsThenClearsTheOverride_OnEveryAccount_AuditingEach()
    {
        var first = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        var second = await TestAccounts.GetAccountKeyAsync("TestAccount02");
        var client = Client("TestUser.Admin");
        var reason = $"Bulk test {Guid.NewGuid():N}";   // the test database keeps earlier runs' audit entries
        try
        {
            var response = await client.PostAsJsonAsync("/api/account-progress/risk-score-override/bulk",
                new { accountKeys = new[] { first, second, 999_999_999L }, overrideRiskScore = 777, reason });
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(2, result!["changed"]!.GetValue<int>());
            Assert.Equal([999_999_999L], result["notFound"]!.AsArray().Select(k => k!.GetValue<long>()));
            Assert.Equal(777, await OverrideOfAsync(first));
            Assert.Equal(777, await OverrideOfAsync(second));

            await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
            {
                var audited = await connection.QuerySingleAsync<int>("""
                    SELECT COUNT(*) FROM web.audit_event a
                    WHERE a.EntityName = 'account_risk_score' AND a.EntityKey IN (@a, @b) AND a.Reason = @reason
                    """, new { a = first.ToString(), b = second.ToString(), reason });
                Assert.Equal(2, audited);
            }

            (await client.PostAsJsonAsync("/api/account-progress/risk-score-override/bulk",
                new { accountKeys = new[] { first, second }, overrideRiskScore = (int?)null })).EnsureSuccessStatusCode();
            Assert.Null(await OverrideOfAsync(first));
            Assert.Null(await OverrideOfAsync(second));
        }
        finally
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync("UPDATE web.account_risk_score SET OverrideRiskScore = NULL, OverrideReason = NULL WHERE AccountKey IN (@first, @second)", new { first, second });
        }
    }

    [Theory]
    [InlineData(500, null)]   // setting one needs a reason
    [InlineData(1001, "too high")]
    public async Task RefusesWhatOneAccountWouldRefuse(int score, string? reason)
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        var response = await Client("TestUser.Admin").PostAsJsonAsync("/api/account-progress/risk-score-override/bulk",
            new { accountKeys = new[] { accountKey }, overrideRiskScore = score, reason });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_CannotSetOverrides()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        var response = await Client("TestUser.Viewer").PostAsJsonAsync("/api/account-progress/risk-score-override/bulk",
            new { accountKeys = new[] { accountKey }, overrideRiskScore = (int?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Keys_ListTheReportsAccounts_OrNoneWhenThereAreMoreThanTheLimit()
    {
        var body = await Client("TestUser.Admin").GetFromJsonAsync<JsonObject>("/api/reports/risk-score/keys");

        var matching = body!["matchingCount"]!.GetValue<int>();
        var max = body["maxAccounts"]!.GetValue<int>();
        var keys = body["accountKeys"]!.AsArray();
        Assert.Equal(matching > max ? 0 : matching, keys.Count);
    }
}
