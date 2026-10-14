using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task NotificationTypes_ListTheD222Types_OffByDefault_AndEnabledRoundTrips()
    {
        var client = AdminClient();
        var types = await client.GetFromJsonAsync<List<NotificationTypeResponse>>("/api/admin/notifications/types");
        foreach (var name in new[] { "NightlyImportFailed", "OverdueRiskExceptions", "WeeklySummary" })
        {
            Assert.False(Assert.Single(types!, t => t.NotificationTypeName == name).IsEnabled);
        }

        // Toggle a type that's on by default, so nothing new can send meanwhile.
        var devFakeAuth = Assert.Single(types!, t => t.NotificationTypeName == "DevFakeAuthEnabledTooLong");
        Assert.True(devFakeAuth.IsEnabled);
        try
        {
            var response = await client.PutAsJsonAsync($"/api/admin/notifications/types/{devFakeAuth.NotificationTypeKey}/enabled", new { isEnabled = false });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var after = await client.GetFromJsonAsync<List<NotificationTypeResponse>>("/api/admin/notifications/types");
            Assert.False(Assert.Single(after!, t => t.NotificationTypeKey == devFakeAuth.NotificationTypeKey).IsEnabled);
        }
        finally
        {
            await client.PutAsJsonAsync($"/api/admin/notifications/types/{devFakeAuth.NotificationTypeKey}/enabled", new { isEnabled = true });
        }

        var missing = await client.PutAsJsonAsync("/api/admin/notifications/types/999999/enabled", new { isEnabled = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Schedule_SaveRoundTrips_AndRejectsABadDayOrTime()
    {
        var client = AdminClient();
        var original = await client.GetFromJsonAsync<ScheduleResponse>("/api/admin/notifications/schedule");
        Assert.NotNull(original);
        try
        {
            var save = await client.PutAsJsonAsync("/api/admin/notifications/schedule",
                new { dailyDigestTime = "06:15", weeklySummaryDay = "Fri", weeklySummaryTime = "16:45" });
            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
            var saved = await client.GetFromJsonAsync<ScheduleResponse>("/api/admin/notifications/schedule");
            Assert.Equal(("06:15", "Fri", "16:45"), (saved!.DailyDigestTime, saved.WeeklySummaryDay, saved.WeeklySummaryTime));

            var bad = await client.PutAsJsonAsync("/api/admin/notifications/schedule",
                new { dailyDigestTime = "6am", weeklySummaryDay = "Friday", weeklySummaryTime = "16:45" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally
        {
            await client.PutAsJsonAsync("/api/admin/notifications/schedule", original);
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

    // ---- D-223: OAuth2 (XOAUTH2) sign-in modes ---------------------------------

    [Fact]
    public async Task Config_OAuthModes_SaveAndGet_RoundTripTheScript20Settings_ThenRestore()
    {
        var client = AdminClient();
        var original = await client.GetFromJsonAsync<NotificationConfigResponse>("/api/admin/notifications/config");
        var credentialKey = await CreateCredentialAsync(client, $"secret-{Guid.NewGuid():N}");
        try
        {
            var m365 = await client.PutAsJsonAsync("/api/admin/notifications/config", OAuthConfig("Microsoft365", credentialKey, new
            {
                oAuthTenantId = "contoso.onmicrosoft.com",
                oAuthClientId = "11112222-bbbb-3333-cccc-4444dddd5555",
                oAuthClientCredentialType = "Certificate",
                oAuthUsername = "alerts@contracttest.local"
            }));
            Assert.Equal(HttpStatusCode.NoContent, m365.StatusCode);
            var saved = await client.GetFromJsonAsync<NotificationConfigResponse>("/api/admin/notifications/config");
            Assert.Equal(("Microsoft365", "contoso.onmicrosoft.com", "11112222-bbbb-3333-cccc-4444dddd5555", "Certificate", "alerts@contracttest.local"),
                (saved!.AuthMethod, saved.OAuthTenantId, saved.OAuthClientId, saved.OAuthClientCredentialType, saved.OAuthUsername));
            Assert.Equal(credentialKey, saved.SmtpCredentialKey);

            var generic = await client.PutAsJsonAsync("/api/admin/notifications/config", OAuthConfig("GenericOAuth2", credentialKey, new
            {
                oAuthClientId = "bluetrack",
                oAuthTokenUrl = "https://idp.contracttest.local/oauth2/token",
                oAuthScope = "smtp.send",
                oAuthAudience = "https://smtp.contracttest.local",
                oAuthClientAuthStyle = "Body"
            }));
            Assert.Equal(HttpStatusCode.NoContent, generic.StatusCode);
            saved = await client.GetFromJsonAsync<NotificationConfigResponse>("/api/admin/notifications/config");
            Assert.Equal(("GenericOAuth2", "https://idp.contracttest.local/oauth2/token", "smtp.send", "https://smtp.contracttest.local", "Body"),
                (saved!.AuthMethod, saved.OAuthTokenUrl, saved.OAuthScope, saved.OAuthAudience, saved.OAuthClientAuthStyle));
            Assert.Null(saved.OAuthTenantId);

            var google = await client.PutAsJsonAsync("/api/admin/notifications/config", OAuthConfig("GoogleWorkspace", credentialKey, new { }));
            Assert.Equal(HttpStatusCode.NoContent, google.StatusCode);
            Assert.Equal("GoogleWorkspace", (await client.GetFromJsonAsync<NotificationConfigResponse>("/api/admin/notifications/config"))!.AuthMethod);
        }
        finally
        {
            await RestoreConfigAsync(client, original!);
            await client.DeleteAsync($"/api/admin/credentials/{credentialKey}");
        }
    }

    [Theory]
    [InlineData("OAuth2", "{}", "AuthMethod must be one of")]
    [InlineData("Microsoft365", """{"oAuthClientId":"id","oAuthClientCredentialType":"Secret"}""", "Tenant ID is required")]
    [InlineData("Microsoft365", """{"oAuthTenantId":"contoso/evil?x=","oAuthClientId":"id","oAuthClientCredentialType":"Secret"}""", "GUID or domain name")]
    [InlineData("Microsoft365", """{"oAuthTenantId":"contoso.com","oAuthClientCredentialType":"Secret"}""", "Client ID is required")]
    [InlineData("GenericOAuth2", """{"oAuthClientId":"id","oAuthClientAuthStyle":"Basic"}""", "Token URL is required")]
    [InlineData("GenericOAuth2", """{"oAuthTokenUrl":"http://idp.example.com/token","oAuthClientId":"id","oAuthClientAuthStyle":"Basic"}""", "https://")]
    [InlineData("GenericOAuth2", """{"oAuthTokenUrl":"https://idp.example.com/token","oAuthClientId":"id"}""", "Basic (Authorization header) or Body")]
    public async Task Config_OAuthModes_MissingOrBadFields_Are400s_WithTheReason(string authMethod, string extraJson, string expected)
    {
        var client = AdminClient();
        var before = await client.GetStringAsync("/api/admin/notifications/config");
        var body = new Dictionary<string, object?>
        {
            ["smtpHost"] = "smtp.contracttest.local",
            ["smtpPort"] = 587,
            ["enableStartTls"] = true,
            ["authMethod"] = authMethod,
            ["smtpCredentialKey"] = 1,
            ["fromAddress"] = "bluetrack@contracttest.local"
        };
        foreach (var property in System.Text.Json.JsonDocument.Parse(extraJson).RootElement.EnumerateObject())
        {
            body[property.Name] = property.Value.GetString();
        }

        var response = await client.PutAsJsonAsync("/api/admin/notifications/config", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await client.GetStringAsync("/api/admin/notifications/config"));
    }

    [Fact]
    public async Task Config_OAuthMode_WithoutStartTls_Is400()
    {
        var client = AdminClient();
        var response = await client.PutAsJsonAsync("/api/admin/notifications/config", new
        {
            smtpHost = "smtp.contracttest.local", smtpPort = 25, enableStartTls = false, authMethod = "GoogleWorkspace",
            smtpCredentialKey = 1, fromAddress = "bluetrack@contracttest.local"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("STARTTLS must be on", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendTestEmail_GenericOAuth2_TokenRefused_ReportsTheErrorCode_AndTheSecretIsNeverInTheResponseConfigOrAudit()
    {
        // A fake token endpoint: no real IdP is called. It echoes the client
        // secret back in its error, the worst case a provider could do.
        var secret = $"ThisIsMy_FAKE_Password6!{Guid.NewGuid():N}";
        var tokenEndpoint = new RefusingTokenEndpoint(secret);
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(BlueTrack.Api.Notifications.OAuth.SmtpOAuthTokenService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => tokenEndpoint)));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");

        var original = await client.GetFromJsonAsync<NotificationConfigResponse>("/api/admin/notifications/config");
        var credentialKey = await CreateCredentialAsync(client, secret);
        var recipientResponse = await client.PostAsJsonAsync("/api/admin/notifications/recipients",
            new { email = $"contracttest_{Guid.NewGuid():N}@example.com", displayName = "OAuth test", isActive = true });
        var recipient = await recipientResponse.Content.ReadFromJsonAsync<RecipientKeyResponse>();
        try
        {
            var save = await client.PutAsJsonAsync("/api/admin/notifications/config", OAuthConfig("GenericOAuth2", credentialKey, new
            {
                oAuthClientId = "bluetrack",
                oAuthTokenUrl = "https://idp.contracttest.local/oauth2/token",
                oAuthClientAuthStyle = "Body"
            }));
            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

            var response = await client.PostAsync("/api/admin/notifications/test", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            var result = System.Text.Json.JsonSerializer.Deserialize<TestEmailResponse>(raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.False(result!.Success);
            Assert.Contains("Token endpoint returned invalid_client", result.Error);
            Assert.DoesNotContain(secret, raw);

            // The secret reached the token endpoint (so the masking above was tested for real), and nowhere else.
            Assert.Equal(1, tokenEndpoint.Calls);
            Assert.Contains(Uri.EscapeDataString(secret), tokenEndpoint.LastBody);
            Assert.DoesNotContain(secret, await client.GetStringAsync("/api/admin/notifications/config"));
            Assert.DoesNotContain(secret, await client.GetStringAsync("/api/admin/credentials"));
            var testAudit = await ConnectionTestAudit.LatestDetailAsync("notification_config", "GenericOAuth2");
            Assert.Contains("Test email (GenericOAuth2) failed: Token endpoint returned invalid_client", testAudit);
            Assert.Equal(0, await CountAuditRowsContainingAsync(secret));
        }
        finally
        {
            await RestoreConfigAsync(client, original!);
            await client.DeleteAsync($"/api/admin/notifications/recipients/{recipient!.RecipientKey}");
            await client.DeleteAsync($"/api/admin/credentials/{credentialKey}");
        }
    }

    private static object OAuthConfig(string authMethod, int credentialKey, object oauthFields)
    {
        var body = new Dictionary<string, object?>
        {
            ["smtpHost"] = "smtp.contracttest.local",
            ["smtpPort"] = 587,
            ["enableStartTls"] = true,
            ["authMethod"] = authMethod,
            ["smtpCredentialKey"] = credentialKey,
            ["fromAddress"] = "bluetrack@contracttest.local",
            ["fromDisplayName"] = "BlueTrack Test"
        };
        foreach (var property in oauthFields.GetType().GetProperties())
        {
            body[property.Name] = property.GetValue(oauthFields);
        }
        return body;
    }

    private static async Task<int> CreateCredentialAsync(HttpClient client, string secret)
    {
        var response = await client.PostAsJsonAsync("/api/admin/credentials", new
        {
            credentialName = $"ContractTestOAuthCred_{Guid.NewGuid():N}",
            backendType = "WindowsDpapi",
            username = "oauth-client",
            plaintextPassword = secret
        });
        Assert.True(response.IsSuccessStatusCode, $"Credential create failed: {response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<CredentialKeyResponse>())!.CredentialKey;
    }

    private static Task RestoreConfigAsync(HttpClient client, NotificationConfigResponse original) =>
        client.PutAsJsonAsync("/api/admin/notifications/config", new
        {
            smtpHost = original.SmtpHost,
            smtpPort = original.SmtpPort,
            enableStartTls = original.EnableStartTls,
            authMethod = original.AuthMethod,
            smtpCredentialKey = (int?)null,
            fromAddress = original.FromAddress,
            fromDisplayName = original.FromDisplayName,
            ignoreCrlErrors = original.IgnoreCrlErrors,
            ignoreSslErrors = original.IgnoreSslErrors
        });

    private static async Task<int> CountAuditRowsContainingAsync(string text)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(BlueTrack.Api.Tests.Integration.TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(connection, """
            SELECT (SELECT COUNT(*) FROM web.audit_event WHERE Detail LIKE '%' + @Text + '%' OR Reason LIKE '%' + @Text + '%')
                 + (SELECT COUNT(*) FROM web.audit_field_change WHERE OldValue LIKE '%' + @Text + '%' OR NewValue LIKE '%' + @Text + '%')
            """, new { Text = text });
    }

    /// <summary>Refuses every token request with invalid_client, echoing the secret in the description.</summary>
    private sealed class RefusingTokenEndpoint(string secret) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent($$"""{"error":"invalid_client","error_description":"Client authentication failed for secret {{secret}}"}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
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
        public string? OAuthTenantId { get; set; }
        public string? OAuthClientId { get; set; }
        public string? OAuthClientCredentialType { get; set; }
        public string? OAuthTokenUrl { get; set; }
        public string? OAuthScope { get; set; }
        public string? OAuthAudience { get; set; }
        public string? OAuthClientAuthStyle { get; set; }
        public string? OAuthUsername { get; set; }
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
        public bool IsEnabled { get; set; }
    }

    private sealed class ScheduleResponse
    {
        public string DailyDigestTime { get; set; } = "";
        public string WeeklySummaryDay { get; set; } = "";
        public string WeeklySummaryTime { get; set; } = "";
    }

    private sealed class RoleResponse
    {
        public int AppRoleKey { get; set; }
        public string RoleName { get; set; } = "";
    }

    private sealed class TestEmailResponse
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
}
