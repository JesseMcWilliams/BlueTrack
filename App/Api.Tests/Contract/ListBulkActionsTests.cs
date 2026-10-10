using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-190: bulk actions on the Targets, Access Groups and Risk Exceptions
/// lists. Each test creates its own rows and removes them.
/// </summary>
public class ListBulkActionsTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ListBulkActionsTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record Skipped(int Key, string? Name, string Reason);
    private sealed record BulkResult(int Requested, int Changed, List<Skipped> Skipped);

    private static async Task<string?> LastAuditReasonAsync(string entityName, int key)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT TOP 1 Reason FROM web.audit_event WHERE EntityName = @entityName AND EntityKey = @key ORDER BY AuditEventKey DESC",
            new { entityName, key = key.ToString() });
    }

    private static async Task<HttpStatusCode> PostStatusAsync(HttpClient client, string url, object body) =>
        (await client.PostAsJsonAsync(url, body)).StatusCode;

    [Fact]
    public async Task Targets_BulkEditChangesOnlyChosenFields_BulkDeleteNeedsAReason()
    {
        var client = Client("TestUser.Admin");
        var types = await client.GetFromJsonAsync<JsonArray>("/api/admin/targets/target-types");
        var serverTypeKey = types!.Single(t => t!["typeCode"]!.GetValue<string>() == "Server")!["targetTypeKey"]!.GetValue<int>();
        var keys = new List<int>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        foreach (var n in new[] { 1, 2 })
        {
            var created = await (await client.PostAsJsonAsync("/api/admin/targets", new
            {
                targetTypeKey = serverTypeKey, targetName = $"BulkTarget{tag}-{n}", riskScore = 100, description = "keep me",
                identifiers = new[] { new { identifierType = "Hostname", identifierValue = $"bulk-{tag}-{n}" } }
            })).Content.ReadFromJsonAsync<JsonObject>();
            keys.Add(created!["targetKey"]!.GetValue<int>());
        }

        try
        {
            var edit = await (await client.PostAsJsonAsync("/api/admin/targets/bulk-edit", new { keys, fields = new[] { "RiskScore" }, riskScore = 750 }))
                .Content.ReadFromJsonAsync<BulkResult>();
            Assert.Equal(2, edit!.Changed);
            foreach (var key in keys)
            {
                var target = await client.GetFromJsonAsync<JsonObject>($"/api/admin/targets/{key}");
                Assert.Equal(750, target!["riskScore"]!.GetValue<int>());
                Assert.Equal("keep me", target["description"]!.GetValue<string>());
                Assert.Single(target["identifiers"]!.AsArray()); // identifiers kept
            }

            Assert.Equal(HttpStatusCode.BadRequest, await PostStatusAsync(client, "/api/admin/targets/bulk-edit", new { keys, fields = new[] { "TargetName" } }));
            Assert.Equal(HttpStatusCode.BadRequest, await PostStatusAsync(client, "/api/admin/targets/bulk-delete", new { keys, reason = " " }));

            var deleted = await (await client.PostAsJsonAsync("/api/admin/targets/bulk-delete", new { keys, reason = "Decommissioned servers" }))
                .Content.ReadFromJsonAsync<BulkResult>();
            Assert.Equal(2, deleted!.Changed);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/targets/{keys[0]}")).StatusCode);
            Assert.Equal("Decommissioned servers", await LastAuditReasonAsync("dim_target", keys[0]));
        }
        finally
        {
            foreach (var key in keys) await client.DeleteAsync($"/api/admin/targets/{key}");
        }
    }

    [Fact]
    public async Task AccessGroups_BulkEdit_SkipsLocalScopeWithoutATarget_AndBulkDeletes()
    {
        var client = Client("TestUser.Admin");
        var keys = new List<int>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        foreach (var n in new[] { 1, 2 })
        {
            var created = await (await client.PostAsJsonAsync("/api/admin/access-groups", new
            {
                groupName = $"BulkGroup{tag}-{n}", groupIdentifier = $"bulk-{tag}-{n}", groupScope = "Domain", baseRiskScore = 100
            })).Content.ReadFromJsonAsync<JsonObject>();
            keys.Add(created!["accessGroupKey"]!.GetValue<int>());
        }

        try
        {
            var edit = await (await client.PostAsJsonAsync("/api/admin/access-groups/bulk-edit", new { keys, fields = new[] { "BaseRiskScore", "Description" }, baseRiskScore = 400, description = "bulk" }))
                .Content.ReadFromJsonAsync<BulkResult>();
            Assert.Equal(2, edit!.Changed);
            var group = await client.GetFromJsonAsync<JsonObject>($"/api/admin/access-groups/{keys[0]}");
            Assert.Equal(400, group!["baseRiskScore"]!.GetValue<int>());
            Assert.Equal("bulk", group["description"]!.GetValue<string>());

            var local = await (await client.PostAsJsonAsync("/api/admin/access-groups/bulk-edit", new { keys, fields = new[] { "GroupScope" }, groupScope = "Local" }))
                .Content.ReadFromJsonAsync<BulkResult>();
            Assert.Equal(0, local!.Changed);
            Assert.All(local.Skipped, s => Assert.Contains("Local scope needs", s.Reason));

            var deleted = await (await client.PostAsJsonAsync("/api/admin/access-groups/bulk-delete", new { keys, reason = "Groups retired" }))
                .Content.ReadFromJsonAsync<BulkResult>();
            Assert.Equal(2, deleted!.Changed);
            Assert.Equal("Groups retired", await LastAuditReasonAsync("dim_access_group", keys[1]));
        }
        finally
        {
            foreach (var key in keys) await client.DeleteAsync($"/api/admin/access-groups/{key}");
        }
    }

    [Fact]
    public async Task RiskExceptions_BulkExtendAndRevoke_SkipNonActive()
    {
        var client = Client("TestUser.Approver");
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var keys = new List<int>();
        foreach (var n in new[] { 1, 2 })
        {
            var created = await (await client.PostAsJsonAsync("/api/risk-exceptions", new
            {
                accountKey, justification = $"Bulk test {n}", reviewDate = DateTime.UtcNow.Date.AddDays(30)
            })).Content.ReadFromJsonAsync<JsonObject>();
            keys.Add(created!["exceptionKey"]!.GetValue<int>());
        }
        var newDate = DateTime.UtcNow.Date.AddDays(200);

        var extended = await (await client.PostAsJsonAsync("/api/risk-exceptions/bulk-extend-review", new { keys, newReviewDate = newDate }))
            .Content.ReadFromJsonAsync<BulkResult>();
        Assert.Equal(2, extended!.Changed);
        var detail = await client.GetFromJsonAsync<JsonObject>($"/api/risk-exceptions/{keys[0]}");
        Assert.Equal(newDate, detail!["reviewDate"]!.GetValue<DateTime>().Date);

        Assert.Equal(HttpStatusCode.BadRequest, await PostStatusAsync(client, "/api/risk-exceptions/bulk-revoke", new { keys, reason = "" }));
        var revoked = await (await client.PostAsJsonAsync("/api/risk-exceptions/bulk-revoke", new { keys = new[] { keys[0] }, reason = "Risk remediated" }))
            .Content.ReadFromJsonAsync<BulkResult>();
        Assert.Equal(1, revoked!.Changed);
        Assert.Equal("Risk remediated", await LastAuditReasonAsync("risk_exception", keys[0]));

        // The revoked one is skipped by both actions now; the other still changes.
        var again = await (await client.PostAsJsonAsync("/api/risk-exceptions/bulk-revoke", new { keys, reason = "Again" }))
            .Content.ReadFromJsonAsync<BulkResult>();
        Assert.Equal(1, again!.Changed);
        Assert.Contains("already Revoked", Assert.Single(again.Skipped).Reason);
        var extendRevoked = await (await client.PostAsJsonAsync("/api/risk-exceptions/bulk-extend-review", new { keys = new[] { keys[0] }, newReviewDate = newDate }))
            .Content.ReadFromJsonAsync<BulkResult>();
        Assert.Contains("only Active", Assert.Single(extendRevoked!.Skipped).Reason);
    }

    [Fact]
    public async Task Keys_ReturnMatchingKeys_AndBulkActionsNeedTheListsPermission()
    {
        var admin = Client("TestUser.Admin");
        var targetKeys = await admin.GetFromJsonAsync<JsonObject>("/api/admin/targets/keys");
        Assert.True(targetKeys!["maxItems"]!.GetValue<int>() >= 1);
        var approver = Client("TestUser.Approver");
        var activeKeys = await approver.GetFromJsonAsync<JsonObject>("/api/risk-exceptions/keys?status=Active");
        Assert.True(activeKeys!["matchingCount"]!.GetValue<int>() >= 0);

        var viewer = Client("TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(viewer, "/api/admin/targets/bulk-delete", new { keys = new[] { 1 }, reason = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(viewer, "/api/admin/access-groups/bulk-edit", new { keys = new[] { 1 }, fields = new[] { "Description" } }));
        Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(Client("TestUser.Analyst"), "/api/risk-exceptions/bulk-revoke", new { keys = new[] { 1 }, reason = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/risk-exceptions/keys")).StatusCode);
    }
}
