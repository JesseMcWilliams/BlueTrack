using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using BlueTrack.Api.Data;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-198, the second group from the 2026-10-10 security review: SMTP can't be
/// used to send a stored credential elsewhere, a field's RequiredPermission is
/// enforced, and recalculating risk scores needs ManageTargets.
/// </summary>
public class SecurityHardening2Tests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public SecurityHardening2Tests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static object SmtpConfig(int? credentialKey, bool startTls, string host = "smtp.contract-test.invalid") => new
    {
        smtpHost = host,
        smtpPort = 587,
        enableStartTls = startTls,
        authMethod = credentialKey is null ? "None" : "Basic",
        smtpCredentialKey = credentialKey,
        fromAddress = "bluetrack@contract-test.invalid",
        fromDisplayName = "Contract test",
        ignoreCrlErrors = false,
        ignoreSslErrors = false,
    };

    [Fact]
    public async Task SmtpSettings_GuardTheStoredCredential()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var original = await connection.QuerySingleAsync("SELECT * FROM web.notification_config");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var credentialKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.credential (CredentialName, BackendType, Username) OUTPUT inserted.CredentialKey VALUES (@Name, 'WindowsDpapi', 'smtp-user')",
            new { Name = $"ContractTest SMTP {suffix}" });

        // A role with ManageNotifications but not ManageCredentials, mapped to a test identity.
        var username = $"TestUser.NotifyOnly{suffix}";
        var roleKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.app_role (RoleName, Description) OUTPUT inserted.AppRoleKey VALUES (@Name, 'Contract test')", new { Name = $"ContractTest NotifyOnly {suffix}" });
        await connection.ExecuteAsync("""
            INSERT INTO web.role_permission (RoleKey, PermissionKey)
            SELECT @roleKey, PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageNotifications';
            INSERT INTO web.identity_group_role_map (ProviderKey, IdentityGroupName, AppRoleKey)
            SELECT ProviderKey, @username, @roleKey FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth';
            """, new { roleKey, username });

        try
        {
            var admin = Client("TestUser.Admin");

            // Basic authentication without STARTTLS would send the password in clear.
            var noTls = await admin.PutAsJsonAsync("/api/admin/notifications/config", SmtpConfig(credentialKey, startTls: false));
            Assert.Equal(HttpStatusCode.BadRequest, noTls.StatusCode);

            // ManageNotifications alone can't point a stored credential at a server.
            var notifyOnly = Client(username);
            var withCredential = await notifyOnly.PutAsJsonAsync("/api/admin/notifications/config", SmtpConfig(credentialKey, startTls: true));
            Assert.Equal(HttpStatusCode.Forbidden, withCredential.StatusCode);

            // ...but can still change SMTP settings that send no credential.
            var noCredential = await notifyOnly.PutAsJsonAsync("/api/admin/notifications/config", SmtpConfig(null, startTls: true));
            Assert.Equal(HttpStatusCode.NoContent, noCredential.StatusCode);

            // An admin (who holds ManageCredentials too) may, with STARTTLS on.
            var adminWithCredential = await admin.PutAsJsonAsync("/api/admin/notifications/config", SmtpConfig(credentialKey, startTls: true));
            Assert.Equal(HttpStatusCode.NoContent, adminWithCredential.StatusCode);
        }
        finally
        {
            await connection.ExecuteAsync("""
                UPDATE web.notification_config
                SET SmtpHost = @SmtpHost, SmtpPort = @SmtpPort, EnableStartTls = @EnableStartTls, AuthMethod = @AuthMethod,
                    SmtpCredentialKey = @SmtpCredentialKey, FromAddress = @FromAddress, FromDisplayName = @FromDisplayName,
                    IgnoreCrlErrors = @IgnoreCrlErrors, IgnoreSslErrors = @IgnoreSslErrors, ModifiedBy = @ModifiedBy, ModifiedDate = @ModifiedDate
                WHERE NotificationConfigKey = @NotificationConfigKey
                """, (object)original);
            await connection.ExecuteAsync("""
                DELETE FROM web.identity_group_role_map WHERE AppRoleKey = @roleKey;
                DELETE FROM web.role_permission WHERE RoleKey = @roleKey;
                DELETE FROM web.app_role WHERE AppRoleKey = @roleKey;
                DELETE FROM web.credential WHERE CredentialKey = @credentialKey;
                """, new { roleKey, credentialKey });
        }
    }

    private sealed record Skipped(long AccountKey, string? AccountName, string Reason);
    private sealed record BulkResult(int Requested, int Updated, int Unchanged, List<Skipped> Skipped);

    [Fact]
    public async Task FieldRequiredPermission_IsEnforced()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        await new AccountProgressLockRepository(new TestDbConnectionFactory()).ForceReleaseAsync(accountKey);
        var ownerBefore = await connection.QuerySingleAsync<string?>(
            "SELECT OwnerName FROM fact_account_progress WHERE AccountKey = @accountKey", new { accountKey });
        // The Analyst role doesn't hold ManageRolesAndPermissions.
        await connection.ExecuteAsync("""
            UPDATE web.account_progress_field_metadata
            SET RequiredPermission = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageRolesAndPermissions')
            WHERE FieldName = 'OwnerName'
            """);

        try
        {
            var response = await Client("TestUser.Analyst").PostAsJsonAsync("/api/account-progress/bulk-edit", new
            {
                accountKeys = new[] { accountKey }, fields = new[] { "OwnerName" }, ownerName = $"Owner {Guid.NewGuid():N}"[..20]
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<BulkResult>();
            Assert.Equal(0, result!.Updated);
            Assert.Contains("ManageRolesAndPermissions", Assert.Single(result.Skipped).Reason);
            Assert.Equal(ownerBefore, await connection.QuerySingleAsync<string?>(
                "SELECT OwnerName FROM fact_account_progress WHERE AccountKey = @accountKey", new { accountKey }));
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE web.account_progress_field_metadata SET RequiredPermission = NULL WHERE FieldName = 'OwnerName'");
        }
    }

    [Fact]
    public async Task RecalculatingRiskScores_NeedsManageTargets()
    {
        // Approver doesn't hold ManageTargets; Analyst does.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Approver").PostAsync("/api/reports/risk-score/recalculate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client("TestUser.Analyst").PostAsync("/api/reports/risk-score/recalculate", null)).StatusCode);
    }
}
