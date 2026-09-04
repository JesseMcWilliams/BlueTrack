using System.Net;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3 (Design_Testing_Strategy.md): permission-policy enforcement per
/// endpoint, exercised end to end (real HTTP, real controllers, real
/// PermissionClaimsTransformation/authorization policies) against the
/// DevFakeAuth role matrix (Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql).
/// </summary>
public class PermissionEnforcementTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public PermissionEnforcementTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientAs(string? testUsername)
    {
        var client = _factory.CreateClient();
        if (testUsername is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, testUsername);
        }

        return client;
    }

    [Fact]
    public async Task GetList_Anonymous_IsUnauthorized()
    {
        var client = CreateClientAs(null);

        var response = await client.GetAsync("/api/risk-exceptions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetList_AnyAuthenticatedRole_Succeeds()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/risk-exceptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetActive_Viewer_IsForbidden()
    {
        // D-78: a Viewer must never reach an ApproveExceptions-gated
        // endpoint, exercised here at the API layer (the frontend's own
        // permission-aware UI, layer 4, confirms the control isn't even
        // rendered -- this confirms the server-side boundary independently).
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/risk-exceptions/active");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetActive_Approver_Succeeds()
    {
        var client = CreateClientAs("TestUser.Approver");

        var response = await client.GetAsync("/api/risk-exceptions/active");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetActive_Analyst_IsForbidden()
    {
        // Analyst has EditAccountProgress but not ApproveExceptions
        // (Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql) --
        // confirms permissions are checked individually, not just "some
        // elevated role."
        var client = CreateClientAs("TestUser.Analyst");

        var response = await client.GetAsync("/api/risk-exceptions/active");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetActive_Admin_Succeeds()
    {
        var client = CreateClientAs("TestUser.Admin");

        var response = await client.GetAsync("/api/risk-exceptions/active");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetActive_UnmappedIdentity_IsUnauthorized()
    {
        // A username with no identity_group_role_map row at all resolves
        // to zero permissions, not "authenticated with nothing" -- but
        // UserRightsResolver.ResolveAsync still returns an authenticated
        // principal with no permission claims, so the ApproveExceptions
        // policy fails as Forbidden, same as any other under-permissioned
        // authenticated user.
        var client = CreateClientAs("TestUser.DoesNotExist");

        var response = await client.GetAsync("/api/risk-exceptions/active");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
