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
