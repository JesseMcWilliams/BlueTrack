using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Design_Risk_Scoring.md, D-101-105, Phase A: Targets and Access Groups
/// admin CRUD. All state created here is cleaned up by the test that
/// created it.
/// </summary>
public class TargetsAndAccessGroupsControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public TargetsAndAccessGroupsControllerTests(BlueTrackWebApplicationFactory factory)
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
    public async Task Target_CreateUpdateDelete_RoundTrips_WithIdentifiers()
    {
        var client = AdminClient();
        var name = $"ContractTestTarget_{Guid.NewGuid():N}";

        var createResponse = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetType = "Server",
            targetName = name,
            riskScore = 500,
            description = "Created by a contract test",
            identifiers = new[] { new { identifierType = "Hostname", identifierValue = $"host-{Guid.NewGuid():N}" } }
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<TargetKeyResponse>();

        try
        {
            var list = await client.GetFromJsonAsync<List<TargetResponse>>("/api/admin/targets");
            var row = Assert.Single(list!, t => t.TargetKey == created!.TargetKey);
            Assert.Equal(500, row.RiskScore);
            Assert.Single(row.Identifiers);

            var updateResponse = await client.PutAsJsonAsync($"/api/admin/targets/{created!.TargetKey}", new
            {
                targetType = "Server",
                targetName = name,
                riskScore = 750,
                identifiers = Array.Empty<object>()
            });
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

            var afterUpdate = await client.GetFromJsonAsync<List<TargetResponse>>("/api/admin/targets");
            var updated = Assert.Single(afterUpdate!, t => t.TargetKey == created.TargetKey);
            Assert.Equal(750, updated.RiskScore);
            Assert.Empty(updated.Identifiers); // replace-all-on-save -- update sent zero identifiers
        }
        finally
        {
            var deleteResponse = await client.DeleteAsync($"/api/admin/targets/{created!.TargetKey}");
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }
    }

    [Fact]
    public async Task Target_IdentifierTypes_ReturnsSeededCatalog()
    {
        var client = AdminClient();

        var types = await client.GetFromJsonAsync<List<IdentifierTypeResponse>>("/api/admin/targets/identifier-types");

        Assert.Contains(types!, t => t.IdentifierType == "ADGuid");
        Assert.Contains(types!, t => t.IdentifierType == "IPAddress" && t.RequiresReview);
    }

    [Fact]
    public async Task AccessGroup_CreateUpdateDelete_RoundTrips()
    {
        var client = AdminClient();
        var identifier = $"CN=ContractTestGroup_{Guid.NewGuid():N}";

        var createResponse = await client.PostAsJsonAsync("/api/admin/access-groups", new
        {
            groupName = "Contract Test Group",
            groupIdentifier = identifier,
            groupScope = "Domain",
            baseRiskScore = 300
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<AccessGroupKeyResponse>();

        try
        {
            var list = await client.GetFromJsonAsync<List<AccessGroupResponse>>("/api/admin/access-groups");
            var row = Assert.Single(list!, g => g.AccessGroupKey == created!.AccessGroupKey);
            Assert.Equal(300, row.BaseRiskScore);
            Assert.True(row.IsRiskScoreStale); // new group always starts stale -- nothing has computed it yet

            var updateResponse = await client.PutAsJsonAsync($"/api/admin/access-groups/{created!.AccessGroupKey}", new
            {
                groupName = "Updated Group Name",
                groupIdentifier = identifier,
                groupScope = "Domain",
                baseRiskScore = 600
            });
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

            var afterUpdate = await client.GetFromJsonAsync<List<AccessGroupResponse>>("/api/admin/access-groups");
            var updated = Assert.Single(afterUpdate!, g => g.AccessGroupKey == created.AccessGroupKey);
            Assert.Equal("Updated Group Name", updated.GroupName);
            Assert.Equal(600, updated.BaseRiskScore);
        }
        finally
        {
            var deleteResponse = await client.DeleteAsync($"/api/admin/access-groups/{created!.AccessGroupKey}");
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }
    }

    private sealed class TargetKeyResponse
    {
        public int TargetKey { get; set; }
    }

    private sealed class TargetResponse
    {
        public int TargetKey { get; set; }
        public int RiskScore { get; set; }
        public List<TargetIdentifierResponse> Identifiers { get; set; } = [];
    }

    private sealed class TargetIdentifierResponse
    {
        public string IdentifierType { get; set; } = "";
        public string IdentifierValue { get; set; } = "";
    }

    private sealed class IdentifierTypeResponse
    {
        public string IdentifierType { get; set; } = "";
        public bool RequiresReview { get; set; }
    }

    private sealed class AccessGroupKeyResponse
    {
        public int AccessGroupKey { get; set; }
    }

    private sealed class AccessGroupResponse
    {
        public int AccessGroupKey { get; set; }
        public string GroupName { get; set; } = "";
        public int BaseRiskScore { get; set; }
        public bool IsRiskScoreStale { get; set; }
    }
}
