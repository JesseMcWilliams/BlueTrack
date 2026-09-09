using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Design_Risk_Scoring.md, D-101-105, D-119 Phase E: the new Risk Score
/// report (ViewRiskReport) and the Account Progress "Edit Override" action
/// (EditAccountProgress), exercised over real HTTP against the synthetic
/// accounts seeded by Database/Test/02_BlueTrack_Test_SyntheticAccountData.sql.
/// Permission-gate coverage for the two GET endpoints lives in
/// AdminControllersPermissionTests' shared matrix; this class covers the
/// actual behavior.
/// </summary>
public class RiskScoreReportControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public RiskScoreReportControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    [Fact]
    public async Task GetContributors_ForAccountReachingATarget_ReturnsIt()
    {
        var client = AdminClient();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var targetName = $"ContractTest_{Guid.NewGuid():N}";
        var targetKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetType, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ('Server', @Name, 600)",
            new { Name = targetName });
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'ContractTest Risk Report Account', 0)",
            new { SourceAccountId = $"ContractTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod) VALUES (@AccountKey, @TargetKey, 'ManualImport')",
            new { AccountKey = accountKey, TargetKey = targetKey });

        try
        {
            var contributors = await client.GetFromJsonAsync<List<ContributorResponse>>($"/api/reports/risk-score/{accountKey}/contributors");
            var contributor = Assert.Single(contributors!);
            Assert.Equal("Target", contributor.EntityType);
            Assert.Equal((long)targetKey, contributor.EntityKey);
            Assert.Equal(targetName, contributor.EntityName);
            Assert.Equal(600, contributor.RiskValue);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_target_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey });
        }
    }

    [Fact]
    public async Task RecalculateNow_ClearsStalenessOnAStaleAccount()
    {
        var client = AdminClient();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'ContractTest Recalculate Account', 0)",
            new { SourceAccountId = $"ContractTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_risk_score (AccountKey, IsRiskScoreStale) VALUES (@AccountKey, 1)", new { AccountKey = accountKey });

        try
        {
            var response = await client.PostAsync("/api/reports/risk-score/recalculate", null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            var isStale = await connection.QuerySingleAsync<bool>(
                "SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            Assert.False(isStale);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        }
    }

    [Fact]
    public async Task SetRiskScoreOverride_WithoutReason_ReturnsBadRequest()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var client = AdminClient();

        var response = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}/risk-score-override", new
        {
            overrideRiskScore = 700
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetRiskScoreOverride_WithReason_ThenClear_RoundTrips()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var client = AdminClient();

        try
        {
            var setResponse = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}/risk-score-override", new
            {
                overrideRiskScore = 850,
                reason = "Contract test override"
            });
            Assert.Equal(HttpStatusCode.NoContent, setResponse.StatusCode);

            var report = await client.GetFromJsonAsync<List<RiskScoreReportRowResponse>>("/api/reports/risk-score");
            var row = Assert.Single(report!, r => r.AccountKey == accountKey);
            Assert.Equal(850, row.OverrideRiskScore);
            Assert.Equal(850, row.EffectiveRiskScore);

            // Clearing back to null needs no Reason.
            var clearResponse = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}/risk-score-override", new
            {
                overrideRiskScore = (int?)null
            });
            Assert.Equal(HttpStatusCode.NoContent, clearResponse.StatusCode);

            var afterClear = await client.GetFromJsonAsync<List<RiskScoreReportRowResponse>>("/api/reports/risk-score");
            var clearedRow = Assert.Single(afterClear!, r => r.AccountKey == accountKey);
            Assert.Null(clearedRow.OverrideRiskScore);
        }
        finally
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "UPDATE web.account_risk_score SET OverrideRiskScore = NULL, OverrideReason = NULL, OverrideSetBy = NULL, OverrideSetDate = NULL WHERE AccountKey = @AccountKey",
                new { AccountKey = accountKey });
        }
    }

    private sealed class ContributorResponse
    {
        public string EntityType { get; set; } = "";
        public long EntityKey { get; set; }
        public string EntityName { get; set; } = "";
        public int RiskValue { get; set; }
    }

    private sealed class RiskScoreReportRowResponse
    {
        public long AccountKey { get; set; }
        public int? ComputedRiskScore { get; set; }
        public int? OverrideRiskScore { get; set; }
        public int? EffectiveRiskScore { get; set; }
    }
}
