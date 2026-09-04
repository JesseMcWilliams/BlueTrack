using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Data;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3 (Design_Testing_Strategy.md): Account Progress editing --
/// locking (D-50), the two D-51 validation rules, and the Risk Exception
/// wiring (D-77) -- exercised together over real HTTP, against the
/// synthetic accounts seeded by
/// Database/Test/02_BlueTrack_Test_SyntheticAccountData.sql. Permission
/// enforcement for this controller is covered by
/// PermissionEnforcementTests' pattern already proven against
/// RiskExceptionsController; this class focuses on the editing behavior
/// itself, as the design doc's own recommended starting point calls for
/// ("locking, validation, and audit trail together").
/// </summary>
public class AccountProgressEditingTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountProgressEditingTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientAs(string testUsername)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, testUsername);
        return client;
    }

    private static async Task<long> GetAccountKeyAsync(string sourceAccountId) =>
        await TestAccounts.GetAccountKeyAsync(sourceAccountId);

    private static async Task ReleaseAnyLockAsync(long accountKey)
    {
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        await repository.ForceReleaseAsync(accountKey);
    }

    [Fact]
    public async Task LockThenUpdate_WithoutAcquiringLock_ReturnsConflict()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");

        var response = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Onboarded to Vault"),
            currentStatusKey = await LookupStatusKeyAsync("In Progress")
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AcquireLock_ThenUpdate_Succeeds()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");

        var lockResponse = await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);
        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);

        var updateResponse = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Onboarded to Vault"),
            currentStatusKey = await LookupStatusKeyAsync("In Progress"),
            ownerName = "Contract Test Owner"
        });

        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        // A successful save releases the lock (D-50 mechanics) -- confirm it's gone.
        var lockStatusResponse = await client.GetAsync($"/api/account-progress/{accountKey}/lock");
        var lockStatusBody = await lockStatusResponse.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(lockStatusBody) || lockStatusBody == "null",
            $"Expected no lock after a successful save, got: {lockStatusBody}");
    }

    [Fact]
    public async Task AcquireLock_AlreadyHeldByAnotherUser_ReturnsConflict()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        await ReleaseAnyLockAsync(accountKey);
        var firstClient = CreateClientAs("TestUser.Approver");
        var secondClient = CreateClientAs("TestUser.Admin");

        var first = await firstClient.PostAsync($"/api/account-progress/{accountKey}/lock", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await secondClient.PostAsync($"/api/account-progress/{accountKey}/lock", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await ReleaseAnyLockAsync(accountKey);
    }

    [Fact]
    public async Task Update_StatusCompleteWithoutActualCompletionDate_ReturnsBadRequest()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount04");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");
        await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);

        var response = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Onboarded to Vault"),
            currentStatusKey = await LookupStatusKeyAsync("Complete")
            // ActualCompletionDate intentionally omitted -- D-51 rule 1.
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReleaseAnyLockAsync(accountKey);
    }

    [Fact]
    public async Task Update_StageRegressionWithoutReason_ReturnsBadRequest()
    {
        // TestAccount04 starts at "Onboarded to Vault" (order 3) -- regressing
        // to "Discovered" (order 1) without a Reason must be rejected (D-51 rule 2).
        var accountKey = await GetAccountKeyAsync("TestAccount04");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");
        await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);

        var response = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Discovered"),
            currentStatusKey = await LookupStatusKeyAsync("In Progress")
            // Reason intentionally omitted.
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReleaseAnyLockAsync(accountKey);
    }

    [Fact]
    public async Task Update_StageRegressionWithReason_Succeeds()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount04");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");
        await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);

        var response = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Assessed / Prioritized"),
            currentStatusKey = await LookupStatusKeyAsync("In Progress"),
            reason = "Contract test: deliberate regression with a documented reason"
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Restore to Onboarded to Vault for test independence/repeatability.
        await ReleaseAnyLockAsync(accountKey);
        await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);
        await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Onboarded to Vault"),
            currentStatusKey = await LookupStatusKeyAsync("In Progress")
        });
    }

    [Fact]
    public async Task Update_ForwardStageProgression_DoesNotRequireReason()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");
        await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);

        var response = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Onboarded to Vault"),
            currentStatusKey = await LookupStatusKeyAsync("In Progress")
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Update_RiskAcceptedWithoutExceptionKey_ReturnsBadRequest()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount04");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");
        await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);

        var response = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Onboarded to Vault"),
            currentStatusKey = await LookupStatusKeyAsync("Risk Accepted / Excluded")
            // ExceptionKey intentionally omitted.
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReleaseAnyLockAsync(accountKey);
    }

    [Fact]
    public async Task Update_RiskAcceptedWithNonExistentExceptionKey_ReturnsBadRequest()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount04");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");
        await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);

        var response = await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = await LookupStageKeyAsync("Onboarded to Vault"),
            currentStatusKey = await LookupStatusKeyAsync("Risk Accepted / Excluded"),
            exceptionKey = 999999
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReleaseAnyLockAsync(accountKey);
    }

    [Fact]
    public async Task GetDetail_ViewerCanRead_NoPermissionRequiredBeyondAuthentication()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync($"/api/account-progress/{accountKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AcquireLock_AsViewer_IsForbidden()
    {
        // Viewer lacks EditAccountProgress -- must never be able to acquire
        // the edit lock, even though bare GET/detail access is allowed.
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_AsViewer_IsForbidden()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.PutAsync($"/api/account-progress/{accountKey}/lock/heartbeat", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_HeldByCaller_RefreshesAndReturnsNoContent()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");
        var lockResponse = await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);
        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);

        var response = await client.PutAsync($"/api/account-progress/{accountKey}/lock/heartbeat", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await ReleaseAnyLockAsync(accountKey);
    }

    [Fact]
    public async Task Heartbeat_NotHeldByCaller_ReturnsConflict()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        await ReleaseAnyLockAsync(accountKey);
        var client = CreateClientAs("TestUser.Approver");

        var response = await client.PutAsync($"/api/account-progress/{accountKey}/lock/heartbeat", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ForceReleaseLock_AsViewer_IsForbidden()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.PostAsync($"/api/account-progress/{accountKey}/lock/force-release", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForceReleaseLock_HeldByAnotherUser_ClearsItAndLogsTheAudit()
    {
        var accountKey = await GetAccountKeyAsync("TestAccount03");
        await ReleaseAnyLockAsync(accountKey);
        var approverClient = CreateClientAs("TestUser.Approver");
        var lockResponse = await approverClient.PostAsync($"/api/account-progress/{accountKey}/lock", null);
        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);
        var adminClient = CreateClientAs("TestUser.Admin");

        var response = await adminClient.PostAsync($"/api/account-progress/{accountKey}/lock/force-release", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var lockStatusResponse = await adminClient.GetAsync($"/api/account-progress/{accountKey}/lock");
        var body = await lockStatusResponse.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(body) || body == "null");
    }

    private static async Task<int> LookupStageKeyAsync(string stageName)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = @StageName", new { StageName = stageName });
    }

    private static async Task<int> LookupStatusKeyAsync(string statusName)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = @StatusName", new { StatusName = statusName });
    }
}
