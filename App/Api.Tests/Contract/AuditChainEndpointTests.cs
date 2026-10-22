using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-295: the seal status and Verify on the Audit Log page need ViewAuditLog; a Verify is itself audited.</summary>
public class AuditChainEndpointTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AuditChainEndpointTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task StatusAndVerify_NeedViewAuditLog()
    {
        var noRights = Client("TestUser.DoesNotExist");   // no roles; the seeded test Viewer holds ViewAuditLog
        Assert.Equal(HttpStatusCode.Forbidden, (await noRights.GetAsync("/api/audit-log/chain")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRights.PostAsync("/api/audit-log/chain/verify", null)).StatusCode);
    }

    [Fact]
    public async Task Verify_ReturnsTheResult_RecordsIt_AndIsAudited()
    {
        var auditor = Client("TestUser.Viewer");   // the seeded test Viewer holds ViewAuditLog
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var before = await connection.QuerySingleAsync<long>(
            "SELECT ISNULL(MAX(AuditEventKey), 0) FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey WHERE t.EventTypeName = 'AuditChainVerified'");

        var response = await auditor.PostAsync("/api/audit-log/chain/verify", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(result["intact"]);
        Assert.True(result["checked"]!.GetValue<int>() > 0);

        var status = (await auditor.GetFromJsonAsync<JsonObject>("/api/audit-log/chain"))!;
        Assert.NotNull(status["lastVerifiedAt"]);
        Assert.Equal(result["intact"]!.GetValue<bool>(), status["lastVerifyIntact"]!.GetValue<bool>());

        Assert.True(await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
            WHERE t.EventTypeName = 'AuditChainVerified' AND e.AuditEventKey > @before AND e.Detail LIKE '%sealed events%'
            """, new { before }) >= 1);
    }
}
