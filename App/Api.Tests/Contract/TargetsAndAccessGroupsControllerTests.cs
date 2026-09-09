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

    /// <summary>D-124 Phase 2: TargetType is now an FK -- resolved by TypeCode via the new target-types endpoint rather than assuming a specific IDENTITY value.</summary>
    private static async Task<int> GetTargetTypeKeyAsync(HttpClient client, string typeCode)
    {
        var types = await client.GetFromJsonAsync<List<TargetTypeResponse>>("/api/admin/targets/target-types");
        return Assert.Single(types!, t => t.TypeCode == typeCode).TargetTypeKey;
    }

    [Fact]
    public async Task Target_CreateUpdateDelete_RoundTrips_WithIdentifiers()
    {
        var client = AdminClient();
        var name = $"ContractTestTarget_{Guid.NewGuid():N}";
        var serverTypeKey = await GetTargetTypeKeyAsync(client, "Server");

        var createResponse = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = serverTypeKey,
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
                targetTypeKey = serverTypeKey,
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

    /// <summary>D-124 Phase 2: web.dim_target_type's seeded catalog -- the corrected "LDAP Directory" display name (was the raw "LdapDirectory" everywhere) and the new distinct "Active Directory" entry.</summary>
    [Fact]
    public async Task Target_TargetTypes_ReturnsSeededCatalog_WithCorrectedLdapDisplayName_AndNewActiveDirectoryEntry()
    {
        var client = AdminClient();

        var types = await client.GetFromJsonAsync<List<TargetTypeResponse>>("/api/admin/targets/target-types");

        Assert.Contains(types!, t => t.TypeCode == "LdapDirectory" && t.DisplayName == "LDAP Directory");
        Assert.Contains(types!, t => t.TypeCode == "ActiveDirectory" && t.DisplayName == "Active Directory");
    }

    /// <summary>D-121: X-Total-Count carries the unfiltered grand total; the JSON body stays a bare array.</summary>
    [Fact]
    public async Task Targets_GetAll_SetsTotalCountHeader()
    {
        var client = AdminClient();
        var name = $"ContractTestTarget_{Guid.NewGuid():N}";
        var serverTypeKey = await GetTargetTypeKeyAsync(client, "Server");
        var createResponse = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = serverTypeKey,
            targetName = name,
            riskScore = 100,
            identifiers = Array.Empty<object>()
        });
        var created = await createResponse.Content.ReadFromJsonAsync<TargetKeyResponse>();

        try
        {
            var beforeCount = await CountAsync(client, "/api/admin/targets");
            var afterResponse = await client.GetAsync("/api/admin/targets");
            var afterBody = await afterResponse.Content.ReadFromJsonAsync<List<TargetResponse>>();

            Assert.True(afterResponse.Headers.TryGetValues("X-Total-Count", out var values));
            var total = int.Parse(values!.Single());
            Assert.Equal(afterBody!.Count, total);
            Assert.True(total >= beforeCount);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/targets/{created!.TargetKey}");
        }
    }

    /// <summary>D-121: TargetType filter narrows the body but X-Total-Count still reports the unfiltered grand total. D-124 Phase 2: the filter is now the FK key (targetTypeKey), not the old raw TargetType string.</summary>
    [Fact]
    public async Task Targets_GetAll_FilterByType_NarrowsBody_ButTotalCountStaysUnfiltered()
    {
        var client = AdminClient();
        var serverName = $"ContractTestTargetServer_{Guid.NewGuid():N}";
        var applicationName = $"ContractTestTargetApp_{Guid.NewGuid():N}";
        var serverTypeKey = await GetTargetTypeKeyAsync(client, "Server");
        var applicationTypeKey = await GetTargetTypeKeyAsync(client, "Application");
        var serverCreate = await client.PostAsJsonAsync("/api/admin/targets", new { targetTypeKey = serverTypeKey, targetName = serverName, riskScore = 100, identifiers = Array.Empty<object>() });
        var appCreate = await client.PostAsJsonAsync("/api/admin/targets", new { targetTypeKey = applicationTypeKey, targetName = applicationName, riskScore = 100, identifiers = Array.Empty<object>() });
        var serverKey = (await serverCreate.Content.ReadFromJsonAsync<TargetKeyResponse>())!.TargetKey;
        var appKey = (await appCreate.Content.ReadFromJsonAsync<TargetKeyResponse>())!.TargetKey;

        try
        {
            var unfilteredResponse = await client.GetAsync("/api/admin/targets");
            var unfilteredTotal = int.Parse(unfilteredResponse.Headers.GetValues("X-Total-Count").Single());

            var filteredResponse = await client.GetAsync($"/api/admin/targets?targetTypeKey={serverTypeKey}");
            var filteredBody = await filteredResponse.Content.ReadFromJsonAsync<List<TargetResponse>>();
            var filteredTotal = int.Parse(filteredResponse.Headers.GetValues("X-Total-Count").Single());

            Assert.Contains(filteredBody!, t => t.TargetKey == serverKey);
            Assert.DoesNotContain(filteredBody, t => t.TargetKey == appKey);
            Assert.Equal(unfilteredTotal, filteredTotal); // total ignores the filter, per D-121's design
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/targets/{serverKey}");
            await client.DeleteAsync($"/api/admin/targets/{appKey}");
        }
    }

    private static async Task<int> CountAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        return int.Parse(response.Headers.GetValues("X-Total-Count").Single());
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

    /// <summary>D-121: SorTypeKey/SorAddress round-trip, additive alongside GroupScope/FoundOnTargetKey (D-119), and DiscoverySource -- already selected server-side -- comes back too.</summary>
    [Fact]
    public async Task AccessGroup_SorFields_And_DiscoverySource_RoundTrip()
    {
        var client = AdminClient();
        var identifier = $"CN=ContractTestSorGroup_{Guid.NewGuid():N}";
        var sorTypes = await client.GetFromJsonAsync<List<SorTypeResponse>>("/api/admin/access-groups/sor-types");
        var domainType = Assert.Single(sorTypes!, t => t.SorTypeName == "Domain");

        var createResponse = await client.PostAsJsonAsync("/api/admin/access-groups", new
        {
            groupName = "Contract Test SOR Group",
            groupIdentifier = identifier,
            groupScope = "Domain",
            sorTypeKey = domainType.SorTypeKey,
            sorAddress = "company.com",
            discoverySource = "AD Discovery",
            baseRiskScore = 250
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<AccessGroupKeyResponse>();

        try
        {
            var list = await client.GetFromJsonAsync<List<AccessGroupResponse>>("/api/admin/access-groups");
            var row = Assert.Single(list!, g => g.AccessGroupKey == created!.AccessGroupKey);
            Assert.Equal(domainType.SorTypeKey, row.SorTypeKey);
            Assert.Equal("Domain", row.SorTypeName);
            Assert.Equal("company.com", row.SorAddress);
            Assert.Equal("AD Discovery", row.DiscoverySource);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/access-groups/{created!.AccessGroupKey}");
        }
    }

    [Fact]
    public async Task AccessGroup_SorTypes_ReturnsSeededCatalog()
    {
        var client = AdminClient();

        var types = await client.GetFromJsonAsync<List<SorTypeResponse>>("/api/admin/access-groups/sor-types");

        Assert.Contains(types!, t => t.SorTypeName == "Domain");
        Assert.Contains(types!, t => t.SorTypeName == "Local");
        Assert.Contains(types!, t => t.SorTypeName == "App");
    }

    /// <summary>D-121: X-Total-Count is present, and a GroupScope filter narrows the body without changing the header.</summary>
    [Fact]
    public async Task AccessGroups_GetAll_FilterByScope_NarrowsBody_ButTotalCountStaysUnfiltered()
    {
        var client = AdminClient();
        var domainIdentifier = $"CN=ContractTestDomainGroup_{Guid.NewGuid():N}";
        var localIdentifier = $"CN=ContractTestLocalGroup_{Guid.NewGuid():N}";
        var domainCreate = await client.PostAsJsonAsync("/api/admin/access-groups", new { groupName = "Contract Test Domain Group", groupIdentifier = domainIdentifier, groupScope = "Domain", baseRiskScore = 100 });
        var localCreate = await client.PostAsJsonAsync("/api/admin/access-groups", new { groupName = "Contract Test Local Group", groupIdentifier = localIdentifier, groupScope = "Local", baseRiskScore = 100 });
        var domainKey = (await domainCreate.Content.ReadFromJsonAsync<AccessGroupKeyResponse>())!.AccessGroupKey;
        var localKey = (await localCreate.Content.ReadFromJsonAsync<AccessGroupKeyResponse>())!.AccessGroupKey;

        try
        {
            var unfilteredResponse = await client.GetAsync("/api/admin/access-groups");
            var unfilteredTotal = int.Parse(unfilteredResponse.Headers.GetValues("X-Total-Count").Single());

            var filteredResponse = await client.GetAsync("/api/admin/access-groups?groupScope=Local");
            var filteredBody = await filteredResponse.Content.ReadFromJsonAsync<List<AccessGroupResponse>>();
            var filteredTotal = int.Parse(filteredResponse.Headers.GetValues("X-Total-Count").Single());

            Assert.Contains(filteredBody!, g => g.AccessGroupKey == localKey);
            Assert.DoesNotContain(filteredBody, g => g.AccessGroupKey == domainKey);
            Assert.Equal(unfilteredTotal, filteredTotal);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/access-groups/{domainKey}");
            await client.DeleteAsync($"/api/admin/access-groups/{localKey}");
        }
    }

    private sealed class SorTypeResponse
    {
        public int SorTypeKey { get; set; }
        public string SorTypeName { get; set; } = "";
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

    private sealed class TargetTypeResponse
    {
        public int TargetTypeKey { get; set; }
        public string TypeCode { get; set; } = "";
        public string DisplayName { get; set; } = "";
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
        public int? SorTypeKey { get; set; }
        public string? SorTypeName { get; set; }
        public string? SorAddress { get; set; }
        public string? DiscoverySource { get; set; }
    }
}
