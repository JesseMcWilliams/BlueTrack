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

    /// <summary>
    /// D-124 Phase 3: page/pageSize paging -- creates 3 exceptions scoped to
    /// one throwaway fact_account (for a race-free isolated view via the
    /// existing accountKey filter, since GetListAsync's default sort spans
    /// the whole table) with distinct ReviewDate values (the default sort)
    /// and confirms page 1/page 2 are disjoint, correctly-ordered slices.
    /// Neither the throwaway account nor the exceptions are cleaned up
    /// afterward -- RiskExceptionRepository has no Delete method at all
    /// (matching this test class's own established no-cleanup precedent for
    /// exception fixtures, e.g. GetListAsync_ScopeTypeFilter... above), and
    /// an FK'd risk_exception row would block deleting the account anyway.
    /// </summary>
    [Fact]
    public async Task GetListAsync_PageAndPageSize_ReturnsDisjointCorrectlyOrderedPages()
    {
        var repository = CreateRepository();
        var approverKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var accountKey = await Dapper.SqlMapper.QuerySingleAsync<long>(connection,
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, @AccountName, 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", AccountName = $"PageTest RiskException Account {suffix}" });

        var reviewDates = new[] { DateTime.UtcNow.Date.AddDays(10), DateTime.UtcNow.Date.AddDays(20), DateTime.UtcNow.Date.AddDays(30) };
        foreach (var reviewDate in reviewDates)
        {
            await repository.CreateAsync(new CreateRiskExceptionRequest
            {
                AccountKey = accountKey,
                Justification = $"Pagination test {suffix}",
                ReviewDate = reviewDate
            }, approverKey);
        }

        var page1 = await repository.GetListAsync(accountKey: accountKey, page: 1, pageSize: 2);
        var page2 = await repository.GetListAsync(accountKey: accountKey, page: 2, pageSize: 2);

        Assert.Equal(2, page1.Count);
        Assert.Single(page2);
        Assert.Equal(reviewDates[0], page1[0].ReviewDate);
        Assert.Equal(reviewDates[1], page1[1].ReviewDate);
        Assert.Equal(reviewDates[2], page2[0].ReviewDate);
        Assert.Empty(page1.Select(e => e.ExceptionKey).Intersect(page2.Select(e => e.ExceptionKey)));
    }
}
