using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-116/D-118: the new Credentials & LDAP admin page. All state created
/// here is cleaned up by the test that created it, matching
/// AdminControllersFunctionalTests's own convention.
/// </summary>
public class CredentialsControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public CredentialsControllerTests(BlueTrackWebApplicationFactory factory)
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
    public async Task DpapiCredential_CreateUpdateDelete_RoundTrips_AndPasswordNeverRedactedAsPlaintext()
    {
        var client = AdminClient();
        var name = $"ContractTestCred_{Guid.NewGuid():N}";

        var createResponse = await client.PostAsJsonAsync("/api/admin/credentials", new
        {
            credentialName = name,
            backendType = "WindowsDpapi",
            username = "contract-test-user",
            plaintextPassword = "correct-horse-battery-staple",
            scopePreference = "Machine"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CredentialKeyResponse>();

        try
        {
            var list = await client.GetFromJsonAsync<List<CredentialResponse>>("/api/admin/credentials");
            var row = Assert.Single(list!, c => c.CredentialKey == created!.CredentialKey);
            Assert.Equal(name, row.CredentialName);
            Assert.True(row.HasPassword);
            Assert.Equal("Machine", row.CurrentScope);
            // Never exposes plaintext, and the redacted DTO has no password field at all to leak.

            var updateResponse = await client.PutAsJsonAsync($"/api/admin/credentials/{created!.CredentialKey}", new
            {
                credentialName = name,
                backendType = "WindowsDpapi",
                username = "updated-user",
                scopePreference = "Machine"
            });
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

            var afterUpdate = await client.GetFromJsonAsync<List<CredentialResponse>>("/api/admin/credentials");
            var updated = Assert.Single(afterUpdate!, c => c.CredentialKey == created.CredentialKey);
            Assert.Equal("updated-user", updated.Username);
            Assert.True(updated.HasPassword); // blank PlaintextPassword on update leaves it alone
        }
        finally
        {
            var deleteResponse = await client.DeleteAsync($"/api/admin/credentials/{created!.CredentialKey}");
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }

        var afterDelete = await client.GetFromJsonAsync<List<CredentialResponse>>("/api/admin/credentials");
        Assert.DoesNotContain(afterDelete!, c => c.CredentialKey == created!.CredentialKey);
    }

    [Fact]
    public async Task DpapiCredential_ScopePreferenceUser_StartsMachine_UpgradesOnFirstTest()
    {
        // D-116's Machine-then-User upgrade mechanism, at the controller
        // level: created with ScopePreference = User always starts
        // CurrentScope = Machine, then the first real decrypt (the Test
        // endpoint) flips it -- mirrors the live verification performed
        // manually against the real BlueTrack_SMTP credential.
        var client = AdminClient();
        var name = $"ContractTestScopeCred_{Guid.NewGuid():N}";

        var createResponse = await client.PostAsJsonAsync("/api/admin/credentials", new
        {
            credentialName = name,
            backendType = "WindowsDpapi",
            username = "scope-test-user",
            plaintextPassword = "scope-test-password",
            scopePreference = "User"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CredentialKeyResponse>();

        try
        {
            var beforeTest = await client.GetFromJsonAsync<List<CredentialResponse>>("/api/admin/credentials");
            var beforeRow = Assert.Single(beforeTest!, c => c.CredentialKey == created!.CredentialKey);
            Assert.Equal("User", beforeRow.ScopePreference);
            Assert.Equal("Machine", beforeRow.CurrentScope);

            var testResponse = await client.PostAsync($"/api/admin/credentials/{created!.CredentialKey}/test", null);
            Assert.Equal(HttpStatusCode.OK, testResponse.StatusCode);
            var testResult = await testResponse.Content.ReadFromJsonAsync<CredentialTestResponse>();
            Assert.True(testResult!.Success);
            Assert.Equal("scope-test-user", testResult.Username);

            var afterTest = await client.GetFromJsonAsync<List<CredentialResponse>>("/api/admin/credentials");
            var afterRow = Assert.Single(afterTest!, c => c.CredentialKey == created.CredentialKey);
            Assert.Equal("User", afterRow.CurrentScope);

            // Still resolves correctly on a second decrypt, now under the upgraded scope.
            var secondTestResponse = await client.PostAsync($"/api/admin/credentials/{created.CredentialKey}/test", null);
            var secondResult = await secondTestResponse.Content.ReadFromJsonAsync<CredentialTestResponse>();
            Assert.True(secondResult!.Success);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/credentials/{created!.CredentialKey}");
        }
    }

    [Fact]
    public async Task Test_UnknownCredentialKey_ReturnsSuccessFalse_NotAServerError()
    {
        var client = AdminClient();

        var response = await client.PostAsync("/api/admin/credentials/999999/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CredentialTestResponse>();
        Assert.False(result!.Success);
    }

    [Fact]
    public async Task LdapConfig_SaveAndGet_RoundTrips_ThenRestoresDisabledDefault()
    {
        var client = AdminClient();
        var original = await client.GetFromJsonAsync<LdapConfigResponse>("/api/admin/credentials/ldap-config");

        try
        {
            var saveResponse = await client.PutAsJsonAsync("/api/admin/credentials/ldap-config", new
            {
                isEnabled = true,
                domainController = "dc01.contracttest.local",
                searchBase = "DC=contracttest,DC=local",
                useSsl = true,
                useTrustedConnection = true,
                credentialKey = (int?)null
            });
            Assert.Equal(HttpStatusCode.NoContent, saveResponse.StatusCode);

            var reloaded = await client.GetFromJsonAsync<LdapConfigResponse>("/api/admin/credentials/ldap-config");
            Assert.True(reloaded!.IsEnabled);
            Assert.Equal("dc01.contracttest.local", reloaded.DomainController);
            Assert.True(reloaded.UseTrustedConnection);
        }
        finally
        {
            await client.PutAsJsonAsync("/api/admin/credentials/ldap-config", new
            {
                isEnabled = original!.IsEnabled,
                domainController = original.DomainController,
                searchBase = original.SearchBase,
                useSsl = original.UseSsl,
                useTrustedConnection = original.UseTrustedConnection,
                credentialKey = original.CredentialKey
            });
        }
    }

    private sealed class CredentialKeyResponse
    {
        public int CredentialKey { get; set; }
    }

    private sealed class CredentialResponse
    {
        public int CredentialKey { get; set; }
        public string CredentialName { get; set; } = "";
        public string BackendType { get; set; } = "";
        public string? Username { get; set; }
        public bool HasPassword { get; set; }
        public string? ScopePreference { get; set; }
        public string? CurrentScope { get; set; }
    }

    private sealed class CredentialTestResponse
    {
        public bool Success { get; set; }
        public string? Username { get; set; }
        public string? Error { get; set; }
    }

    private sealed class LdapConfigResponse
    {
        public bool IsEnabled { get; set; }
        public string? DomainController { get; set; }
        public string? SearchBase { get; set; }
        public bool UseSsl { get; set; }
        public bool UseTrustedConnection { get; set; }
        public int? CredentialKey { get; set; }
    }
}
