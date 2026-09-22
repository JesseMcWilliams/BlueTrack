using BlueTrack.Api.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>The three Reports sub-pages confirmed by D-56, against real BlueTrackTest.</summary>
public class ReportsRepositoryTests
{
    [Fact]
    public async Task GetOverdueAtRiskListAsync_OnlyReturnsPastDueAndIncomplete()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.fact_account_progress SET TargetRemediationDate = DATEADD(DAY, -5, CAST(SYSUTCDATETIME() AS DATE)), ActualCompletionDate = NULL WHERE AccountKey = @AccountKey",
                new { AccountKey = accountKey });
        }

        var repository = new ReportsRepository(new TestDbConnectionFactory());
        var results = await repository.GetOverdueAtRiskListAsync();

        Assert.Contains(results, r => r.AccountKey == accountKey);

        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.fact_account_progress SET TargetRemediationDate = NULL WHERE AccountKey = @AccountKey",
                new { AccountKey = accountKey });
        }
    }

    [Fact]
    public async Task GetOverdueAtRiskListAsync_ExcludesAccountsWithoutATargetDate()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");

        var repository = new ReportsRepository(new TestDbConnectionFactory());
        var results = await repository.GetOverdueAtRiskListAsync();

        Assert.DoesNotContain(results, r => r.AccountKey == accountKey);
    }

    [Fact]
    public async Task GetStageStatusFunnelSummaryAsync_IncludesKnownSyntheticStageStatus()
    {
        var repository = new ReportsRepository(new TestDbConnectionFactory());

        var results = await repository.GetStageStatusFunnelSummaryAsync();

        Assert.Contains(results, r => r.StageName == "Onboarded to Vault" && r.StatusName == "In Progress" && r.AccountCount >= 1);
    }

    [Fact]
    public async Task GetReconciliationReviewQueueAsync_DoesNotThrow()
    {
        var repository = new ReportsRepository(new TestDbConnectionFactory());

        var results = await repository.GetReconciliationReviewQueueAsync();

        Assert.NotNull(results);
    }

    [Fact]
    public async Task GetUnresolvedEntitlementMembersAsync_DoesNotThrow()
    {
        var repository = new ReportsRepository(new TestDbConnectionFactory());

        var results = await repository.GetUnresolvedEntitlementMembersAsync();

        Assert.NotNull(results);
    }

    [Fact]
    public async Task GetKpiSummaryAsync_CountsAreNestedAndNonNegative()
    {
        var repository = new ReportsRepository(new TestDbConnectionFactory());

        var summary = await repository.GetKpiSummaryAsync();

        Assert.True(summary.CompliantAccounts >= 0);
        Assert.True(summary.ManagedAccounts >= summary.CompliantAccounts);
        Assert.True(summary.OnboardedAccounts >= summary.ManagedAccounts);
        Assert.True(summary.InScopeAccounts >= summary.OnboardedAccounts);
        Assert.True(summary.TotalAccounts >= summary.InScopeAccounts);
    }

    [Fact]
    public async Task GetKpiSummaryAsync_StageFiveAndComplete_CountsAsManagedAndCompliant()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        var repository = new ReportsRepository(new TestDbConnectionFactory());
        var before = await repository.GetKpiSummaryAsync();

        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync(
                """
                UPDATE dbo.fact_account_progress
                SET CurrentStageKey = (SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = 'Verified / Compliant'),
                    CurrentStatusKey = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'Complete')
                WHERE AccountKey = @AccountKey
                """,
                new { AccountKey = accountKey });
        }

        try
        {
            var after = await repository.GetKpiSummaryAsync();

            Assert.Equal(before.TotalAccounts, after.TotalAccounts);
            Assert.Equal(before.ManagedAccounts + 1, after.ManagedAccounts);
            Assert.Equal(before.CompliantAccounts + 1, after.CompliantAccounts);
        }
        finally
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync(
                """
                UPDATE dbo.fact_account_progress
                SET CurrentStageKey = (SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = 'Discovered'),
                    CurrentStatusKey = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'Not Started')
                WHERE AccountKey = @AccountKey
                """,
                new { AccountKey = accountKey });
        }
    }

    [Fact]
    public async Task GetKpiSummaryAsync_RiskAcceptedExcluded_RemovesAccountFromScope()
    {
        // TestAccount04 seeds as Onboarded to Vault/In Progress (in scope,
        // onboarded, not managed) -- marking it Risk Accepted / Excluded
        // should drop it out of both InScope and Onboarded without
        // affecting TotalAccounts. No real web.risk_exception row is
        // created here since the aggregate query only reads
        // CurrentStatusKey (D-143) -- the FK link is an app-layer
        // invariant (08_BlueTrack_WebSchema.sql), not a DB constraint.
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount04");
        var repository = new ReportsRepository(new TestDbConnectionFactory());
        var before = await repository.GetKpiSummaryAsync();

        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.fact_account_progress SET CurrentStatusKey = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'Risk Accepted / Excluded') WHERE AccountKey = @AccountKey",
                new { AccountKey = accountKey });
        }

        try
        {
            var after = await repository.GetKpiSummaryAsync();

            Assert.Equal(before.TotalAccounts, after.TotalAccounts);
            Assert.Equal(before.InScopeAccounts - 1, after.InScopeAccounts);
            Assert.Equal(before.OnboardedAccounts - 1, after.OnboardedAccounts);
        }
        finally
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync(
                "UPDATE dbo.fact_account_progress SET CurrentStatusKey = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'In Progress') WHERE AccountKey = @AccountKey",
                new { AccountKey = accountKey });
        }
    }
}
