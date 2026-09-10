using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Covers GetDetailAsync/UpdateAsync/GetStatusNameAsync/GetStageOrderAsync
/// against the synthetic accounts seeded by
/// Database/Test/02_BlueTrack_Test_SyntheticAccountData.sql -- the D-91
/// auto-advance/locking/validation scenarios these were built for are
/// exercised at the contract-test layer (Contract/AccountProgressEditingTests.cs);
/// this layer confirms the raw repository SQL against real data.
/// </summary>
public class AccountProgressRepositoryTests_Detail
{
    [Fact]
    public async Task GetDetailAsync_KnownAccount_ReturnsExpectedShape()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount04");
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());

        var detail = await repository.GetDetailAsync(accountKey);

        Assert.NotNull(detail);
        Assert.Equal(accountKey, detail!.AccountKey);
        Assert.Equal("TestAccount04", detail.AccountName);
    }

    /// <summary>
    /// D-127: Calculated Risk/Risk Band/Override moved from the Account
    /// Progress list's own inline "Edit Override" (removed) onto this
    /// detail endpoint -- confirms GetDetailAsync joins the same
    /// web.account_risk_score/dim_risk_score_band tables GetSummaryListAsync
    /// already does, so the two pages always agree.
    /// </summary>
    [Fact]
    public async Task GetDetailAsync_ReflectsRiskScoreOverride_AndClearsBackToComputed()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount04");
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());
        var (originalOverride, _) = await repository.GetRiskScoreOverrideAsync(accountKey);

        try
        {
            await repository.SetRiskScoreOverrideAsync(accountKey, 777, "Integration test override", userKey);
            var withOverride = await repository.GetDetailAsync(accountKey);
            Assert.Equal(777, withOverride!.OverrideRiskScore);
            Assert.Equal(777, withOverride.EffectiveRiskScore); // COALESCE(OverrideRiskScore, ComputedRiskScore)

            await repository.SetRiskScoreOverrideAsync(accountKey, null, null, userKey);
            var cleared = await repository.GetDetailAsync(accountKey);
            Assert.Null(cleared!.OverrideRiskScore);
            Assert.Equal(cleared.ComputedRiskScore, cleared.EffectiveRiskScore);
        }
        finally
        {
            await repository.SetRiskScoreOverrideAsync(accountKey, originalOverride, originalOverride is null ? null : "Restored after integration test", userKey);
        }
    }

    /// <summary>
    /// D-131: a per-account "Recalculate" action on the Account Progress
    /// edit screen's Risk Score tab -- usp_RecalculateRiskScoreForAccount
    /// reuses the same usp_CalculateRiskScore dispatcher Phase D's own
    /// algorithm tests already cover directly, so this only needs to prove
    /// the single-account wrapper's own mechanics: it lazily creates
    /// web.account_risk_score if this account never had a row (MERGE, same
    /// "insert if missing" convention SetRiskScoreOverrideAsync already
    /// uses), and it clears IsRiskScoreStale regardless of whether the row
    /// was actually stale beforehand (the user asked to recalculate right
    /// now, not "only if already marked stale").
    /// </summary>
    [Fact]
    public async Task RecalculateForAccountAsync_CreatesRowIfMissing_AndClearsStaleFlag()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount04");
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        // Force the "no row yet" path regardless of what earlier tests in
        // this same class left behind (D-127's override test creates one).
        await Dapper.SqlMapper.ExecuteAsync(connection, "DELETE FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });

        try
        {
            await repository.RecalculateForAccountAsync(accountKey);

            var row = await Dapper.SqlMapper.QuerySingleAsync<(int? ComputedRiskScore, DateTime? RiskScoreCalculatedDate, bool IsRiskScoreStale)>(
                connection, "SELECT ComputedRiskScore, RiskScoreCalculatedDate, IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @AccountKey",
                new { AccountKey = accountKey });

            Assert.NotNull(row.ComputedRiskScore);
            Assert.InRange(row.ComputedRiskScore!.Value, 0, 1000);
            Assert.NotNull(row.RiskScoreCalculatedDate);
            Assert.False(row.IsRiskScoreStale);
        }
        finally
        {
            await Dapper.SqlMapper.ExecuteAsync(connection, "DELETE FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        }
    }

    [Fact]
    public async Task GetDetailAsync_UnknownAccount_ReturnsNull()
    {
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());

        var detail = await repository.GetDetailAsync(-1);

        Assert.Null(detail);
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges_ReadableViaGetDetailAsync()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount04");
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());
        var before = await repository.GetDetailAsync(accountKey);
        Assert.NotNull(before);

        var request = new SaveAccountProgressRequest
        {
            CurrentStageKey = before!.CurrentStageKey,
            CurrentStatusKey = before.CurrentStatusKey,
            OwnerName = "Integration Test Owner",
            BusinessUnit = "QA"
        };
        await repository.UpdateAsync(accountKey, request, exceptionKey: null);

        var after = await repository.GetDetailAsync(accountKey);
        Assert.Equal("Integration Test Owner", after!.OwnerName);
        Assert.Equal("QA", after.BusinessUnit);
    }

    [Theory]
    [InlineData("Complete")]
    [InlineData("Not Started")]
    [InlineData("Risk Accepted / Excluded")]
    public async Task GetStatusNameAsync_ResolvesRealStatusNames(string expectedName)
    {
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());
        var statusKey = await LookupStatusKeyAsync(expectedName);

        var name = await repository.GetStatusNameAsync(statusKey);

        Assert.Equal(expectedName, name);
    }

    [Fact]
    public async Task GetStageOrderAsync_OnboardedIsAfterDiscovered()
    {
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());
        var discoveredKey = await LookupStageKeyAsync("Discovered");
        var onboardedKey = await LookupStageKeyAsync("Onboarded to Vault");

        var discoveredOrder = await repository.GetStageOrderAsync(discoveredKey);
        var onboardedOrder = await repository.GetStageOrderAsync(onboardedKey);

        Assert.True(onboardedOrder > discoveredOrder);
    }

    private static async Task<int> LookupStatusKeyAsync(string statusName)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = @StatusName", new { StatusName = statusName });
    }

    private static async Task<int> LookupStageKeyAsync(string stageName)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = @StageName", new { StageName = stageName });
    }
}
