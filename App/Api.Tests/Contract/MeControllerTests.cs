using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>Layer 3: /api/me and self-service Reload My Rights (D-14).</summary>
public class MeControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public MeControllerTests(BlueTrackWebApplicationFactory factory)
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
    public async Task GetCurrentUser_ReturnsMatchingIdentifierAndPermissions()
    {
        var client = CreateClientAs("TestUser.Approver");

        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");

        Assert.NotNull(me);
        Assert.Equal("TestUser.Approver", me!.ExternalIdentifier);
        Assert.Contains("Approver", me.RoleNames);
        Assert.Contains("ApproveExceptions", me.PermissionNames);
    }

    [Fact]
    public async Task GetCurrentUser_UnmappedIdentity_HasNoRolesOrPermissions()
    {
        var client = CreateClientAs("TestUser.DoesNotExist");

        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");

        Assert.NotNull(me);
        Assert.Empty(me!.RoleNames);
        Assert.Empty(me.PermissionNames);
    }

    [Fact]
    public async Task GetCurrentUser_Anonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReloadRights_ReturnsCurrentRoleNamesAndPermissionNames()
    {
        var client = CreateClientAs("TestUser.Admin");

        var response = await client.PostAsync("/api/me/reload-rights", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rights = await response.Content.ReadFromJsonAsync<RightsResponse>();
        Assert.NotNull(rights);
        Assert.Contains("Admin", rights!.RoleNames);
    }

    private sealed class MeResponse
    {
        public string ExternalIdentifier { get; set; } = "";
        public List<string> RoleNames { get; set; } = [];
        public List<string> PermissionNames { get; set; } = [];
    }

    private sealed class RightsResponse
    {
        public List<string> RoleNames { get; set; } = [];
        public List<string> PermissionNames { get; set; } = [];
    }
}
