using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Design_Risk_Exception_Tracking.md's create/extend-review/revoke
/// workflow, against real BlueTrackTest and the synthetic accounts from
/// Database/Test/02_BlueTrack_Test_SyntheticAccountData.sql.
/// </summary>
public class RiskExceptionRepositoryTests
{
    private static RiskExceptionRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task CreateAsync_AccountScoped_IsReadableByGetByKeyAsync()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var approverKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = CreateRepository();

        var request = new CreateRiskExceptionRequest
        {
            AccountKey = accountKey,
            Justification = "Integration test justification",
            ReviewDate = DateTime.UtcNow.Date.AddDays(30)
        };
        var exceptionKey = await repository.CreateAsync(request, approverKey);

        var detail = await repository.GetByKeyAsync(exceptionKey);
        Assert.NotNull(detail);
        Assert.Equal(accountKey, detail!.AccountKey);
        Assert.Null(detail.ApplicationKey);
        Assert.Equal("Active", detail.StatusName);
        Assert.Equal("Integration test justification", detail.Justification);
        Assert.StartsWith("EXC-", detail.ExceptionID);
    }

    [Fact]
    public async Task GetByKeyAsync_UnknownKey_ReturnsNull()
    {
        var repository = CreateRepository();

        var detail = await repository.GetByKeyAsync(-1);

        Assert.Null(detail);
    }

    [Fact]
    public async Task ExtendReviewAsync_UpdatesReviewDateOnly()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var approverKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = CreateRepository();
        var exceptionKey = await repository.CreateAsync(new CreateRiskExceptionRequest
        {
            AccountKey = accountKey,
            Justification = "For extend-review test",
            ReviewDate = DateTime.UtcNow.Date.AddDays(10)
        }, approverKey);
        var newReviewDate = DateTime.UtcNow.Date.AddDays(90);

        await repository.ExtendReviewAsync(exceptionKey, newReviewDate);

        var detail = await repository.GetByKeyAsync(exceptionKey);
        Assert.Equal(newReviewDate, detail!.ReviewDate);
        Assert.Equal("Active", detail.StatusName);
    }

    [Fact]
    public async Task RevokeAsync_SetsStatusToRevoked()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var approverKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = CreateRepository();
        var exceptionKey = await repository.CreateAsync(new CreateRiskExceptionRequest
        {
            AccountKey = accountKey,
            Justification = "For revoke test",
            ReviewDate = DateTime.UtcNow.Date.AddDays(10)
        }, approverKey);

        await repository.RevokeAsync(exceptionKey);

        var detail = await repository.GetByKeyAsync(exceptionKey);
        Assert.Equal("Revoked", detail!.StatusName);
    }

    [Fact]
    public async Task GetActiveAsync_ExcludesRevokedExceptions()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var approverKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = CreateRepository();
        var activeKey = await repository.CreateAsync(new CreateRiskExceptionRequest
        {
            AccountKey = accountKey,
            Justification = "Stays active",
            ReviewDate = DateTime.UtcNow.Date.AddDays(10)
        }, approverKey);
        var revokedKey = await repository.CreateAsync(new CreateRiskExceptionRequest
        {
            AccountKey = accountKey,
            Justification = "Will be revoked",
            ReviewDate = DateTime.UtcNow.Date.AddDays(10)
        }, approverKey);
        await repository.RevokeAsync(revokedKey);

        var active = await repository.GetActiveAsync();

        Assert.Contains(active, e => e.ExceptionKey == activeKey);
        Assert.DoesNotContain(active, e => e.ExceptionKey == revokedKey);
    }

    [Fact]
    public async Task GetOverdueReviewAsync_OnlyReturnsActiveAndPastReviewDate()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var approverKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = CreateRepository();
        var overdueKey = await repository.CreateAsync(new CreateRiskExceptionRequest
        {
            AccountKey = accountKey,
            Justification = "Overdue",
            ReviewDate = DateTime.UtcNow.Date.AddDays(-5)
        }, approverKey);
        var futureKey = await repository.CreateAsync(new CreateRiskExceptionRequest
        {
            AccountKey = accountKey,
            Justification = "Not due yet",
            ReviewDate = DateTime.UtcNow.Date.AddDays(30)
        }, approverKey);

        var overdue = await repository.GetOverdueReviewAsync();

        Assert.Contains(overdue, e => e.ExceptionKey == overdueKey);
        Assert.DoesNotContain(overdue, e => e.ExceptionKey == futureKey);
    }

    [Fact]
    public async Task GetListAsync_ScopeTypeFilter_DistinguishesAccountFromApplication()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var approverKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var repository = CreateRepository();
        var accountScopedKey = await repository.CreateAsync(new CreateRiskExceptionRequest
        {
            AccountKey = accountKey,
            Justification = "Account scoped",
            ReviewDate = DateTime.UtcNow.Date.AddDays(10)
        }, approverKey);

        var accountResults = await repository.GetListAsync(scopeType: "Account");
        var applicationResults = await repository.GetListAsync(scopeType: "Application");

        Assert.Contains(accountResults, e => e.ExceptionKey == accountScopedKey);
        Assert.DoesNotContain(applicationResults, e => e.ExceptionKey == accountScopedKey);
    }

    [Fact]
    public async Task GetListAsync_SqlInjectionAttemptAsSortField_IsIgnoredNotExecuted()
    {
        var repository = CreateRepository();

        var results = await repository.GetListAsync(
            sortBy: [("ExceptionID; DROP TABLE web.risk_exception; --", false)]);

        Assert.NotNull(results);
    }
}
