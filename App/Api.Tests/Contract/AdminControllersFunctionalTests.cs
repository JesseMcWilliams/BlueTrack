using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3: functional CRUD coverage for the Admin pages beyond the
/// permission-gate checks in AdminControllersPermissionTests. All state
/// created here is cleaned up by the test that created it (or, for the
/// singleton app_config row, restored to its original values) so this
/// class can run repeatedly without accumulating test data.
/// </summary>
public class AdminControllersFunctionalTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AdminControllersFunctionalTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient() =>
        WithHeader(_factory.CreateClient(), "TestUser.Admin");

    private static HttpClient WithHeader(HttpClient client, string username)
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, username);
        return client;
    }

    [Fact]
    public async Task Role_CreateUpdateDelete_RoundTrips()
    {
        var client = AdminClient();

        var createResponse = await client.PostAsJsonAsync("/api/admin/roles", new
        {
            roleName = "ContractTestRole",
            description = "Created by a contract test",
            permissionNames = new[] { "ViewDashboard" }
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<RoleKeyResponse>();

        var updateResponse = await client.PutAsJsonAsync($"/api/admin/roles/{created!.RoleKey}", new
        {
            roleName = "ContractTestRole",
            description = "Updated by a contract test",
            permissionNames = new[] { "ViewDashboard", "ViewAuditLog" }
        });
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var roles = await client.GetFromJsonAsync<List<RoleSummaryResponse>>("/api/admin/roles");
        Assert.Contains(roles!, r => r.AppRoleKey == created.RoleKey && r.RoleName == "ContractTestRole");

        var deleteResponse = await client.DeleteAsync($"/api/admin/roles/{created.RoleKey}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var rolesAfterDelete = await client.GetFromJsonAsync<List<RoleSummaryResponse>>("/api/admin/roles");
        Assert.DoesNotContain(rolesAfterDelete!, r => r.AppRoleKey == created.RoleKey);
    }

    [Fact]
    public async Task FieldMetadata_CreateUpdateDelete_RoundTrips()
    {
        var client = AdminClient();

        var createResponse = await client.PostAsJsonAsync("/api/admin/field-metadata", new
        {
            fieldName = "ContractTestField",
            displayLabel = "Contract Test Field",
            fieldType = "text",
            isRequired = false,
            displayOrder = 999
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<FieldMetadataKeyResponse>();

        var updateResponse = await client.PutAsJsonAsync($"/api/admin/field-metadata/{created!.FieldMetadataKey}", new
        {
            fieldName = "ContractTestField",
            displayLabel = "Updated Label",
            fieldType = "text",
            isRequired = true,
            displayOrder = 999
        });
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/admin/field-metadata/{created.FieldMetadataKey}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task GroupRoleMapping_ResolveGroup_ResolvesAWellKnownBuiltInGroup()
    {
        var client = AdminClient();

        var response = await client.PostAsJsonAsync("/api/admin/group-role-mappings/resolve-group", new
        {
            groupName = "BUILTIN\\Users"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ResolveGroupResultResponse>();
        Assert.NotNull(result);
        Assert.StartsWith("S-1-", result!.Sid);
    }

    [Fact]
    public async Task GroupRoleMapping_ResolveGroup_UnknownGroup_ReturnsNotFound()
    {
        var client = AdminClient();

        var response = await client.PostAsJsonAsync("/api/admin/group-role-mappings/resolve-group", new
        {
            groupName = "NoSuchGroup_ContractTest_9f8e7d"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GroupRoleMapping_CreateAndDelete_RoundTrips()
    {
        var client = AdminClient();

        var createResponse = await client.PostAsJsonAsync("/api/admin/group-role-mappings", new
        {
            groupName = "BUILTIN\\Users",
            roleName = "Viewer"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<MappingKeyResponse>();

        var deleteResponse = await client.DeleteAsync($"/api/admin/group-role-mappings/{created!.MappingKey}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task IdentityProvider_CreateUpdateDelete_RoundTrips()
    {
        var client = AdminClient();

        var createResponse = await client.PostAsJsonAsync("/api/admin/identity-providers", new
        {
            providerType = "OIDC",
            displayName = "Contract Test Provider",
            isEnabled = false,
            displayOrder = 999
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ProviderKeyResponse>();

        var updateResponse = await client.PutAsJsonAsync($"/api/admin/identity-providers/{created!.ProviderKey}", new
        {
            providerType = "OIDC",
            displayName = "Contract Test Provider (Updated)",
            isEnabled = false,
            displayOrder = 999
        });
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/admin/identity-providers/{created.ProviderKey}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task GlobalApplicationConfig_Update_PersistsThenRestoresOriginalValues()
    {
        var client = AdminClient();
        var before = await client.GetFromJsonAsync<ConfigResponse>("/api/admin/configuration");
        Assert.NotNull(before);

        var updateResponse = await client.PutAsJsonAsync("/api/admin/configuration", new
        {
            idleTimeoutMinutes = before!.IdleTimeoutMinutes + 1,
            breadcrumbPosition = before.BreadcrumbPosition,
            exceptionIdPattern = before.ExceptionIdPattern,
            lockTimeoutMinutes = before.LockTimeoutMinutes,
            retentionDays = before.RetentionDays,
            logReadEvents = before.LogReadEvents
        });
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var after = await client.GetFromJsonAsync<ConfigResponse>("/api/admin/configuration");
        Assert.Equal(before.IdleTimeoutMinutes + 1, after!.IdleTimeoutMinutes);

        // Restore, since this is a shared singleton row other tests/manual testing also read.
        var restoreResponse = await client.PutAsJsonAsync("/api/admin/configuration", new
        {
            idleTimeoutMinutes = before.IdleTimeoutMinutes,
            breadcrumbPosition = before.BreadcrumbPosition,
            exceptionIdPattern = before.ExceptionIdPattern,
            lockTimeoutMinutes = before.LockTimeoutMinutes,
            retentionDays = before.RetentionDays,
            logReadEvents = before.LogReadEvents
        });
        Assert.Equal(HttpStatusCode.NoContent, restoreResponse.StatusCode);
    }

    [Fact]
    public async Task Application_CreateThenUpdate_RoundTrips()
    {
        var client = AdminClient();
        // web.dim_application.ApplicationName has its own UNIQUE constraint
        // (confirmed directly -- a fixed literal name collided with a
        // leftover row from a previous run, since ApplicationsController
        // has no Delete endpoint to clean up through). Both Code and Name
        // need the same unique suffix, not just Code.
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var createResponse = await client.PostAsJsonAsync("/api/applications", new
        {
            applicationCode = $"CTAPP{suffix}",
            applicationName = $"Contract Test Application {suffix}"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationKeyResponse>();

        var updateResponse = await client.PutAsJsonAsync($"/api/applications/{created!.ApplicationKey}", new
        {
            applicationCode = $"CTAPP{suffix}",
            applicationName = $"Contract Test Application {suffix} (Updated)"
        });
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var detailed = await client.GetFromJsonAsync<List<ApplicationSummaryResponse>>("/api/applications/detailed");
        Assert.Contains(detailed!, a => a.ApplicationKey == created.ApplicationKey && a.ApplicationName == $"Contract Test Application {suffix} (Updated)");

        await DeleteApplicationDirectlyAsync(created.ApplicationKey);
    }

    [Fact]
    public async Task Safe_AssignAndClearApplication_RoundTrips()
    {
        var client = AdminClient();
        var safeKey = await LookupSafeKeyAsync("TestSafe01");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var appCreate = await client.PostAsJsonAsync("/api/applications", new
        {
            applicationCode = $"CTSAFE{suffix}",
            applicationName = $"Safe Assignment Test Application {suffix}"
        });
        appCreate.EnsureSuccessStatusCode();
        var app = await appCreate.Content.ReadFromJsonAsync<ApplicationKeyResponse>();

        var assignResponse = await client.PutAsJsonAsync($"/api/safes/{safeKey}/application", app!.ApplicationKey);
        Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);

        var safes = await client.GetFromJsonAsync<List<SafeSummaryResponse>>("/api/safes");
        Assert.Contains(safes!, s => s.SafeKey == safeKey && s.ApplicationKey == app.ApplicationKey);

        // Clear the assignment so this synthetic fixture is left the way other tests expect it.
        var clearResponse = await client.PutAsJsonAsync($"/api/safes/{safeKey}/application", (int?)null);
        Assert.Equal(HttpStatusCode.NoContent, clearResponse.StatusCode);

        await DeleteApplicationDirectlyAsync(app.ApplicationKey);
    }

    private static async Task<int> LookupSafeKeyAsync(string safeName)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT SafeKey FROM dbo.dim_safe WHERE SafeName = @SafeName", new { SafeName = safeName });
    }

    /// <summary>
    /// ApplicationsController has no Delete endpoint (not built yet) --
    /// this goes around the API directly to clean up test-created rows,
    /// since ApplicationName's UNIQUE constraint means a leftover row
    /// breaks the next run otherwise.
    /// </summary>
    private static async Task DeleteApplicationDirectlyAsync(int applicationKey)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        await Dapper.SqlMapper.ExecuteAsync(connection,
            "DELETE FROM web.dim_application WHERE ApplicationKey = @ApplicationKey", new { ApplicationKey = applicationKey });
    }

    private sealed class ApplicationKeyResponse
    {
        public int ApplicationKey { get; set; }
    }

    private sealed class ApplicationSummaryResponse
    {
        public int ApplicationKey { get; set; }
        public string ApplicationName { get; set; } = "";
    }

    private sealed class SafeSummaryResponse
    {
        public int SafeKey { get; set; }
        public int? ApplicationKey { get; set; }
    }

    private sealed class RoleKeyResponse
    {
        public int RoleKey { get; set; }
    }

    private sealed class RoleSummaryResponse
    {
        public int AppRoleKey { get; set; }
        public string RoleName { get; set; } = "";
    }

    private sealed class FieldMetadataKeyResponse
    {
        public int FieldMetadataKey { get; set; }
    }

    private sealed class ResolveGroupResultResponse
    {
        public string Sid { get; set; } = "";
    }

    private sealed class MappingKeyResponse
    {
        public int MappingKey { get; set; }
    }

    private sealed class ProviderKeyResponse
    {
        public int ProviderKey { get; set; }
    }

    private sealed class ConfigResponse
    {
        public int IdleTimeoutMinutes { get; set; }
        public string BreadcrumbPosition { get; set; } = "";
        public string ExceptionIdPattern { get; set; } = "";
        public int LockTimeoutMinutes { get; set; }
        public int? RetentionDays { get; set; }
        public bool LogReadEvents { get; set; }
    }
}
