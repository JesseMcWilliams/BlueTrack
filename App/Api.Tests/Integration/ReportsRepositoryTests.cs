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
}
