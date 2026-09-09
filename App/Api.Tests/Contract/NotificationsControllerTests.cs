using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-115/D-116: SMTP config, recipients, and notification-type role
/// targeting. All state created here is cleaned up or restored by the test
/// that touched it.
/// </summary>
public class NotificationsControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public NotificationsControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    [Fact]
    public async Task Config_SaveAndGet_RoundTrips_IncludingCredentialAndTlsOverrideFields_ThenRestoresOriginal()
    {
        var client = AdminClient();
        var original = await client.GetFromJsonAsync<NotificationConfigResponse>("/api/admin/notifications/config");

        var credentialCreate = await client.PostAsJsonAsync("/api/admin/credentials", new
        {
            credentialName = $"ContractTestSmtpCred_{Guid.NewGuid():N}",
            backendType = "WindowsDpapi",
            username = "smtp-test-user",
            plaintextPassword = "smtp-test-password"
        });
        var credential = await credentialCreate.Content.ReadFromJsonAsync<CredentialKeyResponse>();

        try
        {
            var saveResponse = await client.PutAsJsonAsync("/api/admin/notifications/config", new
            {
                smtpHost = "smtp.contracttest.local",
                smtpPort = 25,
                enableStartTls = true,
                authMethod = "Basic",
                smtpCredentialKey = credential!.CredentialKey,
                fromAddress = "bluetrack@contracttest.local",
                fromDisplayName = "BlueTrack Test",
                ignoreCrlErrors = true,
                ignoreSslErrors = false
            });
            Assert.Equal(HttpStatusCode.NoContent, saveResponse.StatusCode);

            var reloaded = await client.GetFromJsonAsync<NotificationConfigResponse>("/api/admin/notifications/config");
            Assert.Equal("smtp.contracttest.local", reloaded!.SmtpHost);
            Assert.Equal(credential.CredentialKey, reloaded.SmtpCredentialKey);
            Assert.True(reloaded.IgnoreCrlErrors);
            Assert.False(reloaded.IgnoreSslErrors);
        }
        finally
        {
            await client.PutAsJsonAsync("/api/admin/notifications/config", new
            {
                smtpHost = original!.SmtpHost,
                smtpPort = original.SmtpPort,
                enableStartTls = original.EnableStartTls,
                authMethod = original.AuthMethod,
                smtpCredentialKey = (int?)null,
                fromAddress = original.FromAddress,
                fromDisplayName = original.FromDisplayName,
                ignoreCrlErrors = original.IgnoreCrlErrors,
                ignoreSslErrors = original.IgnoreSslErrors
            });
            await client.DeleteAsync($"/api/admin/credentials/{credential!.CredentialKey}");
        }
    }

    [Fact]
    public async Task Recipient_CreateUpdateDelete_RoundTrips()
    {
        var client = AdminClient();
        var email = $"contracttest_{Guid.NewGuid():N}@example.com";

        var createResponse = await client.PostAsJsonAsync("/api/admin/notifications/recipients", new
        {
            email,
            displayName = "Contract Test Recipient",
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<RecipientKeyResponse>();

        try
        {
            var updateResponse = await client.PutAsJsonAsync($"/api/admin/notifications/recipients/{created!.RecipientKey}", new
            {
                email,
                displayName = "Updated Recipient",
                isActive = false
            });
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

            var list = await client.GetFromJsonAsync<List<RecipientResponse>>("/api/admin/notifications/recipients");
            var row = Assert.Single(list!, r => r.RecipientKey == created.RecipientKey);
            Assert.Equal("Updated Recipient", row.DisplayName);
            Assert.False(row.IsActive);
        }
        finally
        {
            var deleteResponse = await client.DeleteAsync($"/api/admin/notifications/recipients/{created!.RecipientKey}");
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }
    }

    [Fact]
    public async Task NotificationType_SetAndClearTargetRole_RoundTrips()
    {
        // D-116: additive role targeting -- NULL is the default (flat-list-
        // only) behavior, confirmed restorable after setting a real role.
        var client = AdminClient();
        var types = await client.GetFromJsonAsync<List<NotificationTypeResponse>>("/api/admin/notifications/types");
        var devFakeAuthType = Assert.Single(types!, t => t.NotificationTypeName == "DevFakeAuthEnabledTooLong");
        Assert.Null(devFakeAuthType.TargetRoleKey);

        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/admin/roles");
        var adminRole = Assert.Single(roles!, r => r.RoleName == "Admin");

        try
        {
            var setResponse = await client.PutAsJsonAsync(
                $"/api/admin/notifications/types/{devFakeAuthType.NotificationTypeKey}/target-role",
                new { targetRoleKey = adminRole.AppRoleKey });
            Assert.Equal(HttpStatusCode.NoContent, setResponse.StatusCode);

            var afterSet = await client.GetFromJsonAsync<List<NotificationTypeResponse>>("/api/admin/notifications/types");
            var updated = Assert.Single(afterSet!, t => t.NotificationTypeKey == devFakeAuthType.NotificationTypeKey);
            Assert.Equal(adminRole.AppRoleKey, updated.TargetRoleKey);
            Assert.Equal("Admin", updated.TargetRoleName);
        }
        finally
        {
            await client.PutAsJsonAsync(
                $"/api/admin/notifications/types/{devFakeAuthType.NotificationTypeKey}/target-role",
                new { targetRoleKey = (int?)null });
        }
    }

    [Fact]
    public async Task SendTestEmail_NoActiveRecipients_ReturnsBadRequest()
    {
        // Relies on there being no active recipients in BlueTrackTest by
        // default -- if a prior test left one behind this would need
        // updating, but every recipient test above cleans up after itself.
        var client = AdminClient();

        var response = await client.PostAsync("/api/admin/notifications/test", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendTestEmail_SmtpNotConfigured_ReturnsSuccessFalse_NotAServerError()
    {
        // SmtpNotificationSender throws InvalidOperationException when
        // SmtpHost/FromAddress are unset -- the controller catches every
        // exception and reports {success:false}, never a raw 500.
        var client = AdminClient();
        var email = $"contracttest_{Guid.NewGuid():N}@example.com";
        var recipientResponse = await client.PostAsJsonAsync("/api/admin/notifications/recipients", new
        {
            email,
            displayName = "Contract Test Recipient",
            isActive = true
        });
        var recipient = await recipientResponse.Content.ReadFromJsonAsync<RecipientKeyResponse>();

        try
        {
            var response = await client.PostAsync("/api/admin/notifications/test", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<TestEmailResponse>();
            Assert.False(result!.Success);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/notifications/recipients/{recipient!.RecipientKey}");
        }
    }

    private sealed class CredentialKeyResponse
    {
        public int CredentialKey { get; set; }
    }

    private sealed class NotificationConfigResponse
    {
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; }
        public bool EnableStartTls { get; set; }
        public string AuthMethod { get; set; } = "";
        public int? SmtpCredentialKey { get; set; }
        public string? FromAddress { get; set; }
        public string? FromDisplayName { get; set; }
        public bool IgnoreCrlErrors { get; set; }
        public bool IgnoreSslErrors { get; set; }
    }

    private sealed class RecipientKeyResponse
    {
        public int RecipientKey { get; set; }
    }

    private sealed class RecipientResponse
    {
        public int RecipientKey { get; set; }
        public string Email { get; set; } = "";
        public string? DisplayName { get; set; }
        public bool IsActive { get; set; }
    }

    private sealed class NotificationTypeResponse
    {
        public int NotificationTypeKey { get; set; }
        public string NotificationTypeName { get; set; } = "";
        public int? TargetRoleKey { get; set; }
        public string? TargetRoleName { get; set; }
    }

    private sealed class RoleResponse
    {
        public int AppRoleKey { get; set; }
        public string RoleName { get; set; } = "";
    }

    private sealed class TestEmailResponse
    {
        public bool Success { get; set; }
    }
}
