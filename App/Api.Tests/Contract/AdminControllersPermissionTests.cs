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
/// (07_BlueTrack_WebInterface_Seed.sql's bootstrap Admin role); none of
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
            new object[] { "/api/admin/identity-providers" },
            new object[] { "/api/admin/secrets-store" },
            new object[] { "/api/admin/deployment" },
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

    private sealed class MeResponse
    {
        public int UserKey { get; set; }
    }
}
