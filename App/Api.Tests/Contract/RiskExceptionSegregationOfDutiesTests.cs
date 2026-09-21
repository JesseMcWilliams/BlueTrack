using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Data;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Segregation of duties on Risk Exception approval: the person who
/// approved an exception (web.risk_exception.ApprovedBy) can't also be the
/// one who links it to an account via AccountProgressController.Update,
/// while the admin-configurable EnforceRiskExceptionSegregationOfDuties
/// toggle is on. Uses TestAccount01 (seeded at Discovered/Not Started,
/// untouched by AccountProgressEditingTests' own TestAccount03/04 fixtures)
/// so this class can freely mutate its stage/status/ExceptionKey -- every
/// mutation is still restored in a finally block (including the config
/// toggle, which defaults to off) to leave shared state exactly as this
/// class found it for whichever test runs next.
/// </summary>
public class RiskExceptionSegregationOfDutiesTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public RiskExceptionSegregationOfDutiesTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientAs(string testUsername)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, testUsername);
        return client;
    }

    [Fact]
    public async Task Update_LinkOwnApprovedException_WithEnforcementOn_ReturnsBadRequest()
    {
        var adminClient = CreateClientAs("TestUser.Admin");
        var approverClient = CreateClientAs("TestUser.Approver");
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        await ReleaseAnyLockAsync(accountKey);
        var before = await GetDetailAsync(approverClient, accountKey);

        await SetEnforceSoDAsync(adminClient, true);
        try
        {
            var exceptionKey = await CreateExceptionAsync(approverClient, accountKey);
            await approverClient.PostAsync($"/api/account-progress/{accountKey}/lock", null);

            var response = await approverClient.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
            {
                currentStageKey = before.CurrentStageKey,
                currentStatusKey = await LookupStatusKeyAsync("Risk Accepted / Excluded"),
                exceptionKey
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await SetEnforceSoDAsync(adminClient, false);
            // Rejected before UpdateAsync/ReleaseAsync ever run, so the lock is still held.
            await ReleaseAnyLockAsync(accountKey);
        }
    }

    [Fact]
    public async Task Update_LinkOwnApprovedException_WithEnforcementOff_Succeeds()
    {
        var adminClient = CreateClientAs("TestUser.Admin");
        var approverClient = CreateClientAs("TestUser.Approver");
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        await ReleaseAnyLockAsync(accountKey);
        var before = await GetDetailAsync(approverClient, accountKey);

        await SetEnforceSoDAsync(adminClient, false);
        try
        {
            var exceptionKey = await CreateExceptionAsync(approverClient, accountKey);
            await approverClient.PostAsync($"/api/account-progress/{accountKey}/lock", null);

            var response = await approverClient.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
            {
                currentStageKey = before.CurrentStageKey,
                currentStatusKey = await LookupStatusKeyAsync("Risk Accepted / Excluded"),
                exceptionKey
            });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally
        {
            await RestoreAsync(approverClient, accountKey, before);
        }
    }

    [Fact]
    public async Task Update_LinkExceptionApprovedByDifferentUser_WithEnforcementOn_Succeeds()
    {
        var adminClient = CreateClientAs("TestUser.Admin");
        var approverClient = CreateClientAs("TestUser.Approver");
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        await ReleaseAnyLockAsync(accountKey);
        var before = await GetDetailAsync(approverClient, accountKey);

        await SetEnforceSoDAsync(adminClient, true);
        try
        {
            // Approved by Approver, linked by Admin -- a different person, so this must be allowed even with enforcement on.
            var exceptionKey = await CreateExceptionAsync(approverClient, accountKey);
            await adminClient.PostAsync($"/api/account-progress/{accountKey}/lock", null);

            var response = await adminClient.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
            {
                currentStageKey = before.CurrentStageKey,
                currentStatusKey = await LookupStatusKeyAsync("Risk Accepted / Excluded"),
                exceptionKey
            });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally
        {
            await SetEnforceSoDAsync(adminClient, false);
            await RestoreAsync(adminClient, accountKey, before);
        }
    }

    private static async Task ReleaseAnyLockAsync(long accountKey)
    {
        var repository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        await repository.ForceReleaseAsync(accountKey);
    }

    private static async Task<AccountProgressDetailResponse> GetDetailAsync(HttpClient client, long accountKey)
    {
        var response = await client.GetAsync($"/api/account-progress/{accountKey}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountProgressDetailResponse>())!;
    }

    private static async Task RestoreAsync(HttpClient client, long accountKey, AccountProgressDetailResponse before)
    {
        await ReleaseAnyLockAsync(accountKey);
        await client.PostAsync($"/api/account-progress/{accountKey}/lock", null);
        await client.PutAsJsonAsync($"/api/account-progress/{accountKey}", new
        {
            currentStageKey = before.CurrentStageKey,
            currentStatusKey = before.CurrentStatusKey,
            ownerName = before.OwnerName,
            businessUnit = before.BusinessUnit,
            notes = before.Notes
        });
        await ReleaseAnyLockAsync(accountKey);
    }

    private static async Task<int> CreateExceptionAsync(HttpClient client, long accountKey)
    {
        var response = await client.PostAsJsonAsync("/api/risk-exceptions", new
        {
            accountKey,
            justification = "Segregation-of-duties contract test exception",
            reviewDate = DateTime.UtcNow.Date.AddDays(30)
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CreatedExceptionResponse>();
        return body!.ExceptionKey;
    }

    private static async Task SetEnforceSoDAsync(HttpClient adminClient, bool enforce)
    {
        var current = await adminClient.GetFromJsonAsync<GlobalApplicationConfigResponse>("/api/admin/configuration");
        var response = await adminClient.PutAsJsonAsync("/api/admin/configuration", new
        {
            idleTimeoutMinutes = current!.IdleTimeoutMinutes,
            breadcrumbPosition = current.BreadcrumbPosition,
            exceptionIdPattern = current.ExceptionIdPattern,
            lockTimeoutMinutes = current.LockTimeoutMinutes,
            retentionDays = current.RetentionDays,
            logReadEvents = current.LogReadEvents,
            backupFolder = current.BackupFolder,
            activeRiskAlgorithm = current.ActiveRiskAlgorithm,
            enforceRiskExceptionSegregationOfDuties = enforce
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<int> LookupStatusKeyAsync(string statusName)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = @StatusName", new { StatusName = statusName });
    }

    private sealed class CreatedExceptionResponse
    {
        public int ExceptionKey { get; set; }
    }

    private sealed class GlobalApplicationConfigResponse
    {
        public int IdleTimeoutMinutes { get; set; }
        public string BreadcrumbPosition { get; set; } = "";
        public string ExceptionIdPattern { get; set; } = "";
        public int LockTimeoutMinutes { get; set; }
        public int? RetentionDays { get; set; }
        public bool LogReadEvents { get; set; }
        public string? BackupFolder { get; set; }
        public string ActiveRiskAlgorithm { get; set; } = "";
        public bool EnforceRiskExceptionSegregationOfDuties { get; set; }
    }

    private sealed class AccountProgressDetailResponse
    {
        public int CurrentStageKey { get; set; }
        public int CurrentStatusKey { get; set; }
        public string? OwnerName { get; set; }
        public string? BusinessUnit { get; set; }
        public string? Notes { get; set; }
    }
}
