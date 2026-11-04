using System.Net;
using System.Net.Http.Json;
using System.Text;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Design_Risk-Scoring.md, D-101-105, D-119 Phase B: CSV bulk import for
/// the Target inventory, exercising TargetMatchingService's three real
/// branches (auto-merge on a strong identifier match, route to review on a
/// weak-only match, create new on no match) through the actual HTTP upload
/// path, plus the review-queue resolve action.
/// </summary>
public class RiskScoringImportControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public RiskScoringImportControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private static MultipartFormDataContent BuildCsvUpload(string csv, string fileName = "import.csv")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "file", fileName);
        return content;
    }

    /// <summary>D-124 Phase 2: TargetType is now an FK -- resolved by TypeCode via the target-types endpoint rather than assuming a specific IDENTITY value. The CSV rows in this file keep using the raw TypeCode text ("Server"), unaffected by this -- only the direct single-add JSON payloads below need the resolved key.</summary>
    private static async Task<int> GetTargetTypeKeyAsync(HttpClient client, string typeCode)
    {
        var types = await client.GetFromJsonAsync<List<TargetTypeResponse>>("/api/admin/targets/target-types");
        return Assert.Single(types!, t => t.TypeCode == typeCode).TargetTypeKey;
    }

    [Fact]
    public async Task TargetInventoryImport_NewIdentifier_CreatesNewTarget()
    {
        var client = AdminClient();
        var targetName = $"ContractTestImport_{Guid.NewGuid():N}";
        var hostname = $"host-{Guid.NewGuid():N}";
        var csv = $"TargetType,TargetName,RiskScore,Description,DiscoverySource,Hostname\nServer,{targetName},400,,,{hostname}\n";

        var response = await client.PostAsync("/api/admin/risk-scoring/import/target-inventory", BuildCsvUpload(csv));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportResultResponse>();
        Assert.Equal(1, result!.CreatedCount);
        Assert.Empty(result.Errors);

        var targets = await client.GetFromJsonAsync<List<TargetResponse>>("/api/admin/targets");
        var created = Assert.Single(targets!, t => t.TargetName == targetName);

        await client.DeleteAsync($"/api/admin/targets/{created.TargetKey}");
    }

    // D-316: a re-import that changes a Target's score marks the accounts that reach it for recalculation; the same score doesn't.
    [Fact]
    public async Task TargetInventoryReimport_WithANewScore_MarksTheAccountsThatReachItStale()
    {
        var client = AdminClient();
        var adGuid = Guid.NewGuid().ToString();
        var createResponse = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = await GetTargetTypeKeyAsync(client, "Server"),
            targetName = $"ContractTestReimport_{Guid.NewGuid():N}",
            riskScore = 200,
            identifiers = new[] { new { identifierType = "ADGuid", identifierValue = adGuid } }
        });
        var created = await createResponse.Content.ReadFromJsonAsync<TargetKeyResponse>();
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod) VALUES (@accountKey, @targetKey, 'ManualImport')",
            new { accountKey, targetKey = created!.TargetKey });
        async Task<bool> StaleAsync() => await connection.QuerySingleOrDefaultAsync<bool?>(
            "SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @accountKey", new { accountKey }) ?? false;
        async Task ImportAsync(int score) => (await client.PostAsync("/api/admin/risk-scoring/import/target-inventory",
            BuildCsvUpload($"TargetType,TargetName,RiskScore,Description,DiscoverySource,ADGuid\nServer,Whatever,{score},,,{adGuid}\n"))).EnsureSuccessStatusCode();
        try
        {
            await connection.ExecuteAsync("UPDATE web.account_risk_score SET IsRiskScoreStale = 0 WHERE AccountKey = @accountKey", new { accountKey });
            await ImportAsync(200);
            Assert.False(await StaleAsync());

            await ImportAsync(650);
            Assert.True(await StaleAsync());
            Assert.Equal(650, await connection.QuerySingleAsync<int>("SELECT RiskScore FROM web.dim_target WHERE TargetKey = @key", new { key = created.TargetKey }));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_target_map WHERE TargetKey = @key", new { key = created.TargetKey });
            await client.DeleteAsync($"/api/admin/targets/{created.TargetKey}");
        }
    }

    // D-322: a matched Target takes the row's type and description; a blank description leaves the Target's.
    [Fact]
    public async Task TargetInventoryReimport_UpdatesTypeAndDescription_AndABlankDescriptionKeepsTheTargets()
    {
        var client = AdminClient();
        var adGuid = Guid.NewGuid().ToString();
        var createResponse = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = await GetTargetTypeKeyAsync(client, "Server"),
            targetName = $"ContractTestType_{Guid.NewGuid():N}",
            riskScore = 100,
            description = "Original description",
            identifiers = new[] { new { identifierType = "ADGuid", identifierValue = adGuid } }
        });
        var created = await createResponse.Content.ReadFromJsonAsync<TargetKeyResponse>();
        try
        {
            (await client.PostAsync("/api/admin/risk-scoring/import/target-inventory",
                BuildCsvUpload($"TargetType,TargetName,RiskScore,Description,DiscoverySource,ADGuid\nDatabase,db-renamed,100,,,{adGuid}\n"))).EnsureSuccessStatusCode();
            var target = await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{created!.TargetKey}");
            Assert.Equal(await GetTargetTypeKeyAsync(client, "Database"), target!.TargetTypeKey);
            Assert.Equal("db-renamed", target.TargetName);
            Assert.Equal("Original description", target.Description);

            (await client.PostAsync("/api/admin/risk-scoring/import/target-inventory",
                BuildCsvUpload($"TargetType,TargetName,RiskScore,Description,DiscoverySource,ADGuid\nDatabase,db-renamed,100,New description,,{adGuid}\n"))).EnsureSuccessStatusCode();
            Assert.Equal("New description", (await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{created.TargetKey}"))!.Description);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/targets/{created!.TargetKey}");
        }
    }

    [Fact]
    public async Task TargetInventoryImport_MatchingStrongIdentifier_MergesIntoExistingTarget()
    {
        var client = AdminClient();
        var adGuid = Guid.NewGuid().ToString();
        var originalName = $"ContractTestImport_{Guid.NewGuid():N}";

        var createResponse = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = await GetTargetTypeKeyAsync(client, "Server"),
            targetName = originalName,
            riskScore = 500,
            identifiers = new[] { new { identifierType = "ADGuid", identifierValue = adGuid } }
        });
        var created = await createResponse.Content.ReadFromJsonAsync<TargetKeyResponse>();

        try
        {
            var csv = $"TargetType,TargetName,RiskScore,Description,DiscoverySource,ADGuid\nServer,IgnoredName,999,,,{adGuid}\n";
            var response = await client.PostAsync("/api/admin/risk-scoring/import/target-inventory", BuildCsvUpload(csv));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<ImportResultResponse>();
            Assert.Equal(1, result!.MergedCount);
            Assert.Equal(0, result.CreatedCount);

            var targets = await client.GetFromJsonAsync<List<TargetResponse>>("/api/admin/targets");
            // Still the original Target row (matched, not duplicated); D-316/D-322: the row's risk score and name replace
            // its own, so a re-import updates them.
            Assert.Single(targets!, t => t.TargetKey == created!.TargetKey && t.TargetName == "IgnoredName" && t.RiskScore == 999);
            Assert.DoesNotContain(targets!, t => t.TargetName == originalName);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/targets/{created!.TargetKey}");
        }
    }

    [Fact]
    public async Task TargetInventoryImport_MatchingOnlyOnIpAddress_RoutesToReviewQueue_NotAutoMerged()
    {
        var client = AdminClient();
        var ip = $"10.99.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";
        var originalName = $"ContractTestImport_{Guid.NewGuid():N}";

        var createResponse = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = await GetTargetTypeKeyAsync(client, "Server"),
            targetName = originalName,
            riskScore = 200,
            identifiers = new[] { new { identifierType = "IPAddress", identifierValue = ip } }
        });
        var created = await createResponse.Content.ReadFromJsonAsync<TargetKeyResponse>();

        try
        {
            var newName = $"ContractTestImport_{Guid.NewGuid():N}";
            var csv = $"TargetType,TargetName,RiskScore,Description,DiscoverySource,IPAddress\nServer,{newName},999,,,{ip}\n";
            var response = await client.PostAsync("/api/admin/risk-scoring/import/target-inventory", BuildCsvUpload(csv));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<ImportResultResponse>();
            Assert.Equal(1, result!.PendingReviewCount);
            Assert.Equal(0, result.CreatedCount);
            Assert.Equal(0, result.MergedCount);

            // No new Target was created for the ambiguous row.
            var targets = await client.GetFromJsonAsync<List<TargetResponse>>("/api/admin/targets");
            Assert.DoesNotContain(targets!, t => t.TargetName == newName);

            var pending = await client.GetFromJsonAsync<List<ReviewRowResponse>>("/api/admin/risk-scoring/target-match-review");
            var reviewRow = Assert.Single(pending!, r => r.IdentifierValue == ip);

            var resolveResponse = await client.PostAsJsonAsync($"/api/admin/risk-scoring/target-match-review/{reviewRow.TargetMatchReviewKey}/resolve", new { resolution = "Ignored" });
            Assert.Equal(HttpStatusCode.NoContent, resolveResponse.StatusCode);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/targets/{created!.TargetKey}");
        }
    }

    [Fact]
    public async Task TargetInventoryImport_BadRiskScoreRow_ReportsRowError_DoesNotFailWholeBatch()
    {
        var client = AdminClient();
        var goodName = $"ContractTestImport_{Guid.NewGuid():N}";
        var csv = $"TargetType,TargetName,RiskScore,Description,DiscoverySource,Hostname\nServer,BadRow,not-a-number,,,\nServer,{goodName},300,,,\n";

        var response = await client.PostAsync("/api/admin/risk-scoring/import/target-inventory", BuildCsvUpload(csv));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportResultResponse>();
        Assert.Equal(2, result!.TotalRows);
        Assert.Equal(1, result.CreatedCount);
        Assert.Single(result.Errors);
        Assert.Equal(2, result.Errors[0].RowNumber);

        var targets = await client.GetFromJsonAsync<List<TargetResponse>>("/api/admin/targets");
        var created = Assert.Single(targets!, t => t.TargetName == goodName);
        await client.DeleteAsync($"/api/admin/targets/{created.TargetKey}");
    }

    /// <summary>D-124 Phase 2: TargetType is now resolved against web.dim_target_type's TypeCode -- an unrecognized code reports a clean per-row error rather than failing the whole batch, matching the bad-RiskScore row's own established pattern above.</summary>
    [Fact]
    public async Task TargetInventoryImport_UnrecognizedTargetTypeCode_ReportsRowError_DoesNotFailWholeBatch()
    {
        var client = AdminClient();
        var goodName = $"ContractTestImport_{Guid.NewGuid():N}";
        var csv = $"TargetType,TargetName,RiskScore,Description,DiscoverySource,Hostname\nNotARealType,BadRow,400,,,\nServer,{goodName},300,,,\n";

        var response = await client.PostAsync("/api/admin/risk-scoring/import/target-inventory", BuildCsvUpload(csv));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportResultResponse>();
        Assert.Equal(2, result!.TotalRows);
        Assert.Equal(1, result.CreatedCount);
        Assert.Single(result.Errors);
        Assert.Equal(2, result.Errors[0].RowNumber);
        Assert.Contains("NotARealType", result.Errors[0].Error);

        var targets = await client.GetFromJsonAsync<List<TargetResponse>>("/api/admin/targets");
        var created = Assert.Single(targets!, t => t.TargetName == goodName);
        await client.DeleteAsync($"/api/admin/targets/{created.TargetKey}");
    }

    [Fact]
    public async Task AccessGroupInventoryImport_SameGroupIdentifierTwice_UpsertsRatherThanDuplicates()
    {
        var client = AdminClient();
        var identifier = $"ContractTestImport_{Guid.NewGuid():N}";
        var csv1 = $"GroupName,GroupIdentifier,GroupScope,BaseRiskScore,Description\nOriginal Name,{identifier},Domain,100,\n";
        var csv2 = $"GroupName,GroupIdentifier,GroupScope,BaseRiskScore,Description\nUpdated Name,{identifier},Domain,700,\n";

        await client.PostAsync("/api/admin/risk-scoring/import/access-group-inventory", BuildCsvUpload(csv1));
        var response2 = await client.PostAsync("/api/admin/risk-scoring/import/access-group-inventory", BuildCsvUpload(csv2));
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        var groups = await client.GetFromJsonAsync<List<AccessGroupResponse>>("/api/admin/access-groups");
        var matching = groups!.Where(g => g.GroupIdentifier == identifier).ToList();
        var row = Assert.Single(matching);
        Assert.Equal("Updated Name", row.GroupName);
        Assert.Equal(700, row.BaseRiskScore);

        await client.DeleteAsync($"/api/admin/access-groups/{row.AccessGroupKey}");
    }

    [Fact]
    public async Task GetTargetInventoryTemplate_IncludesFixedColumnsAndSeededIdentifierTypes()
    {
        var client = AdminClient();

        var response = await client.GetAsync("/api/admin/risk-scoring/import/target-inventory/template");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("TargetType", csv);
        Assert.Contains("ADGuid", csv);
        Assert.Contains("IPAddress", csv);
    }

    private sealed class ImportResultResponse
    {
        public int TotalRows { get; set; }
        public int SucceededCount { get; set; }
        public int CreatedCount { get; set; }
        public int MergedCount { get; set; }
        public int PendingReviewCount { get; set; }
        public List<ImportRowErrorResponse> Errors { get; set; } = [];
    }

    private sealed class ImportRowErrorResponse
    {
        public int RowNumber { get; set; }
        public string Error { get; set; } = "";
    }

    private sealed class TargetKeyResponse
    {
        public int TargetKey { get; set; }
    }

    private sealed class TargetResponse
    {
        public int TargetKey { get; set; }
        public string TargetName { get; set; } = "";
        public int RiskScore { get; set; }
        public int TargetTypeKey { get; set; }
        public string? Description { get; set; }
    }

    private sealed class TargetTypeResponse
    {
        public int TargetTypeKey { get; set; }
        public string TypeCode { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    private sealed class ReviewRowResponse
    {
        public int TargetMatchReviewKey { get; set; }
        public string IdentifierValue { get; set; } = "";
    }

    private sealed class AccessGroupResponse
    {
        public int AccessGroupKey { get; set; }
        public string GroupName { get; set; } = "";
        public string GroupIdentifier { get; set; } = "";
        public int BaseRiskScore { get; set; }
    }
}
