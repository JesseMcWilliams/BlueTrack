using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3: permission-gate coverage across every Admin controller not
/// covered by its own dedicated test class. Design_Testing_Strategy.md's
/// own motivating example for this whole layer was exactly this class of
/// bug -- an endpoint missing its intended policy (D-56's ungated
/// reconciliation-review-queue, found during frontend work) -- so a gate
/// check per admin endpoint is worth having even without deep functional
/// coverage of each one. TestUser.Admin holds every confirmed permission
/// (09_BlueTrack_WebSeed.sql's bootstrap Admin role); none of
/// Viewer/Analyst/Approver hold any of these admin-only permissions, so
/// any of them is a valid "should be forbidden" caller.
/// </summary>
public class AdminControllersPermissionTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AdminControllersPermissionTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientAs(string testUsername)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, testUsername);
        return client;
    }

    public static IEnumerable<object[]> GatedGetEndpoints =>
        new[]
        {
            new object[] { "/api/admin/field-metadata" },
            new object[] { "/api/admin/configuration" },
            new object[] { "/api/admin/permissions" },
            new object[] { "/api/admin/roles" },
            new object[] { "/api/admin/group-role-mappings" },
            new object[] { "/api/admin/group-role-mappings/roles" },
            new object[] { "/api/admin/identity-providers" },
            new object[] { "/api/admin/secrets-store" },
            new object[] { "/api/admin/deployment" },
            new object[] { "/api/admin/credentials" },
            new object[] { "/api/admin/credentials/ldap-config" },
            new object[] { "/api/admin/notifications/config" },
            new object[] { "/api/admin/notifications/recipients" },
            new object[] { "/api/admin/notifications/types" },
            new object[] { "/api/admin/targets" },
            new object[] { "/api/admin/targets/identifier-types" },
            new object[] { "/api/admin/access-groups" },
            new object[] { "/api/admin/access-groups/sor-types" },
            new object[] { "/api/admin/risk-score-bands" },
            new object[] { "/api/admin/risk-scoring/target-match-review" },
            new object[] { "/api/admin/risk-scoring/import-mapping-profiles" },
            new object[] { "/api/reports/risk-score" },
            new object[] { "/api/reports/risk-score/999999999/contributors" },
            new object[] { "/api/audit-log" },
            new object[] { "/api/safes" },
            new object[] { "/api/applications/detailed" },
        };

    // Viewer/Analyst/Approver all legitimately hold ViewAuditLog per this
    // test matrix's own design (Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql)
    // -- none of them are a valid "should be forbidden" caller for
    // /api/audit-log specifically. TestUser.DoesNotExist (mapped to no
    // role at all) is the one identity guaranteed to lack every permission.
    public static IEnumerable<object[]> GatedGetEndpointsExceptAuditLog =>
        GatedGetEndpoints.Where(row => (string)row[0] != "/api/audit-log");

    [Theory]
    [MemberData(nameof(GatedGetEndpointsExceptAuditLog))]
    public async Task GatedGetEndpoint_AsViewer_IsForbidden(string path)
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuditLog_AsUnmappedIdentity_IsForbidden()
    {
        var client = CreateClientAs("TestUser.DoesNotExist");

        var response = await client.GetAsync("/api/audit-log");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuditLog_AsViewer_Succeeds_HoldsViewAuditLogInThisMatrix()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/audit-log");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// D-121: Analyst granted full parity with Admin on ManageTargets/
    /// ManageAccessGroups (confirmed directly) -- these two endpoints move
    /// from Admin-only to also reachable by Analyst, distinct from every
    /// other admin-only endpoint in GatedGetEndpoints above, which Analyst
    /// still cannot reach (Viewer/Analyst/Approver hold none of those).
    /// </summary>
    [Theory]
    [InlineData("/api/admin/targets")]
    [InlineData("/api/admin/targets/identifier-types")]
    [InlineData("/api/admin/access-groups")]
    [InlineData("/api/admin/access-groups/sor-types")]
    public async Task TargetsAndAccessGroupsEndpoints_AsAnalyst_Succeed(string path)
    {
        var client = CreateClientAs("TestUser.Analyst");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>D-121: Approver holds no ManageTargets/ManageAccessGroups grant of its own in this test matrix -- still forbidden, unlike Analyst above.</summary>
    [Theory]
    [InlineData("/api/admin/targets")]
    [InlineData("/api/admin/access-groups")]
    public async Task TargetsAndAccessGroupsEndpoints_AsApprover_IsForbidden(string path)
    {
        var client = CreateClientAs("TestUser.Approver");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(GatedGetEndpoints))]
    public async Task GatedGetEndpoint_AsAdmin_Succeeds(string path)
    {
        var client = CreateClientAs("TestUser.Admin");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(GatedGetEndpoints))]
    public async Task GatedGetEndpoint_Anonymous_IsUnauthorized(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApplicationsGetList_AnyAuthenticatedUser_Succeeds_NoCurateApplicationMappingNeeded()
    {
        // Deliberately bare [Authorize] (Risk Exception create form's
        // scoping dropdown needs it for any authenticated user), unlike
        // /detailed which requires CurateApplicationMapping.
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/applications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApplicationsCreate_AsViewer_IsForbidden()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.PostAsJsonAsync("/api/applications", new { applicationName = "Should be rejected" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminUsersReloadRights_AsViewer_IsForbidden()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.PostAsync("/api/admin/users/1/reload-rights", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminUsersReloadRights_AsAdmin_UnknownUserKey_ReturnsNotFound()
    {
        var client = CreateClientAs("TestUser.Admin");

        var response = await client.PostAsync("/api/admin/users/999999/reload-rights", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminUsersReloadRights_AsAdmin_KnownUser_Succeeds()
    {
        var client = CreateClientAs("TestUser.Admin");
        // Force this identity's own app_user row to exist first.
        await client.GetAsync("/api/me");
        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");

        var response = await client.PostAsync($"/api/admin/users/{me!.UserKey}/reload-rights", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeploymentBackup_AsViewer_IsForbidden()
    {
        // D-117: TriggerBackup is a separate policy from the class-level
        // ViewDeploymentInfo -- Viewer holds neither, so this is forbidden
        // regardless of which one the middleware evaluates first.
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.PostAsync("/api/admin/deployment/backup", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed class MeResponse
    {
        public int UserKey { get; set; }
    }
}
