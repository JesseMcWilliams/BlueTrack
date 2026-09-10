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

    /// <summary>
    /// D-121: X-Total-Count carries the unfiltered grand total; the JSON
    /// body shape (a bare array) is unchanged. This report takes no filter
    /// params, so the header always equals the body's own count. D-124
    /// Phase 3: pageSize=500 is passed so the body still reflects the full
    /// set (now capped per page by default), and X-Filtered-Count is
    /// asserted equal to X-Total-Count too, since there's no filter to
    /// narrow it here.
    /// </summary>
    [Fact]
    public async Task GetReport_SetsTotalCountHeader_MatchingBodyCount()
    {
        var client = AdminClient();

        var response = await client.GetAsync("/api/reports/risk-score?pageSize=500");

        Assert.True(response.Headers.TryGetValues("X-Total-Count", out var values));
        var rows = await response.Content.ReadFromJsonAsync<List<RiskScoreReportRowResponse>>();
        var total = int.Parse(values!.Single());
        Assert.Equal(rows!.Count, total);

        Assert.True(response.Headers.TryGetValues("X-Filtered-Count", out var filteredValues));
        Assert.Equal(total, int.Parse(filteredValues!.Single()));
    }

    /// <summary>
    /// D-124 Phase 3: page/pageSize paging. This report has no filter param
    /// to isolate a fresh fixture from whatever else exists in
    /// dbo.fact_account at the same time, so this instead validates
    /// end-to-end correctness against a "ground truth" fetch of the whole
    /// sorted set (a large pageSize=500, comfortably above anything this
    /// test DB ever holds, sorted by accountName so ties are irrelevant --
    /// every AccountName in this codebase's fixtures is GUID-suffixed and
    /// therefore effectively unique): whatever that full list actually is,
    /// page 1/page 2 (pageSize 2) must be its first-two/next-two elements
    /// in order, and disjoint. Three accounts are still created here just
    /// to guarantee the table has at least 3 rows to page across.
    /// </summary>
    [Fact]
    public async Task GetReport_PageAndPageSize_ReturnsDisjointCorrectlyOrderedPagesMatchingGroundTruth()
    {
        var client = AdminClient();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var accountKeys = new List<long>();
        foreach (var letter in new[] { "A", "B", "C" })
        {
            accountKeys.Add(await connection.QuerySingleAsync<long>(
                "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, @AccountName, 0)",
                new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", AccountName = $"PageTest-{suffix}-{letter}" }));
        }

        try
        {
            var fullResponse = await client.GetAsync("/api/reports/risk-score?sort=accountName:asc&pageSize=500");
            var fullList = await fullResponse.Content.ReadFromJsonAsync<List<RiskScoreReportRowResponse>>();

            var page1Response = await client.GetAsync("/api/reports/risk-score?sort=accountName:asc&page=1&pageSize=2");
            var page1 = await page1Response.Content.ReadFromJsonAsync<List<RiskScoreReportRowResponse>>();
            var page2Response = await client.GetAsync("/api/reports/risk-score?sort=accountName:asc&page=2&pageSize=2");
            var page2 = await page2Response.Content.ReadFromJsonAsync<List<RiskScoreReportRowResponse>>();

            var expectedPage1 = fullList!.Take(2).Select(r => r.AccountKey).ToList();
            var expectedPage2 = fullList!.Skip(2).Take(2).Select(r => r.AccountKey).ToList();

            Assert.Equal(expectedPage1, page1!.Select(r => r.AccountKey).ToList());
            Assert.Equal(expectedPage2, page2!.Select(r => r.AccountKey).ToList());
            Assert.Empty(page1.Select(r => r.AccountKey).Intersect(page2.Select(r => r.AccountKey)));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey IN @Keys", new { Keys = accountKeys });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey IN @Keys", new { Keys = accountKeys });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey IN @Keys", new { Keys = accountKeys });
        }
    }

    /// <summary>D-124 Phase 3: a request for an absurd pageSize is capped server-side (PagingParams.MaxPageSize = 500), not honored literally -- proven here by confirming the request succeeds cleanly rather than erroring or hanging, which an uncapped OFFSET/FETCH against a client-supplied value could risk under a real malicious/buggy request.</summary>
    [Fact]
    public async Task GetReport_AbsurdPageSize_IsCappedServerSide_RequestStillSucceeds()
    {
        var client = AdminClient();

        var response = await client.GetAsync("/api/reports/risk-score?pageSize=999999999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetContributors_ForAccountReachingATarget_ReturnsIt()
    {
        var client = AdminClient();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var targetName = $"ContractTest_{Guid.NewGuid():N}";
        var targetKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 600)",
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
