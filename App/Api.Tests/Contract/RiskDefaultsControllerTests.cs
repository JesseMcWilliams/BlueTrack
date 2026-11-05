using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-349: Admin > Risk Score, Defaults and Findings (ManageRiskScoreBands); the form defaults for anyone signed in.</summary>
[Collection("DnaImport")]   // saving a finding's value marks every score stale; the findings are rebuilt from the DNA rows
public class RiskDefaultsControllerTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task TheFormDefaults_AreReadableByAnyone_TheRestNeedsManageRiskScoreBands()
    {
        var viewer = Client("TestUser.Viewer");
        var form = JsonNode.Parse(await viewer.GetStringAsync("/api/risk-score/defaults"))!;
        Assert.NotNull(form["defaultTargetRiskScore"]);
        Assert.NotNull(form["defaultAccessGroupRiskScore"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/risk-score/defaults")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/risk-score/findings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/admin/risk-score/defaults", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/admin/risk-score/findings/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Saving_IsValidated_Audited_AndAChangedFindingValueMarksScoresToRecalculate()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var original = await connection.QuerySingleAsync<(int, int, int)>(
            "SELECT TOP (1) DefaultAddressTargetRiskScore, DefaultTargetRiskScore, DefaultAccessGroupRiskScore FROM web.app_config");
        var originalValue = await connection.QuerySingleAsync<int>("SELECT RiskValue FROM web.risk_finding_type WHERE FindingType = 'DnaPassTheHash'");
        var since = await connection.QuerySingleAsync<DateTime>("SELECT SYSUTCDATETIME()");
        var admin = Client("TestUser.Admin");
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/risk-score/defaults",
                new { defaultAddressTargetRiskScore = 1001, defaultTargetRiskScore = 0, defaultAccessGroupRiskScore = 0, findingTypes = Array.Empty<object>() })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/risk-score/defaults",
                new { defaultAddressTargetRiskScore = 0, defaultTargetRiskScore = 0, defaultAccessGroupRiskScore = 0, findingTypes = new[] { new { findingType = "Nope", riskValue = 1, isEnabled = true } } })).StatusCode);

            await connection.ExecuteAsync("UPDATE web.account_risk_score SET IsRiskScoreStale = 0");
            var response = await admin.PutAsJsonAsync("/api/admin/risk-score/defaults", new
            {
                defaultAddressTargetRiskScore = 100, defaultTargetRiskScore = 200, defaultAccessGroupRiskScore = 50,
                findingTypes = new[] { new { findingType = "DnaPassTheHash", riskValue = originalValue == 450 ? 451 : 450, isEnabled = true } }
            });
            response.EnsureSuccessStatusCode();
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.True((bool)body["rescore"]!);
            Assert.Equal(200, (int)body["defaults"]!["defaultTargetRiskScore"]!);
            Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.account_risk_score WHERE IsRiskScoreStale = 0"));

            var form = JsonNode.Parse(await Client("TestUser.Viewer").GetStringAsync("/api/risk-score/defaults"))!;
            Assert.Equal((200, 50), ((int)form["defaultTargetRiskScore"]!, (int)form["defaultAccessGroupRiskScore"]!));

            var detail = await connection.QuerySingleAsync<string>("""
                SELECT TOP (1) e.Detail FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
                WHERE t.EventTypeName = 'RiskDefaultsChanged' AND e.OccurredAt >= @since ORDER BY e.AuditEventKey DESC
                """, new { since });
            Assert.Contains("New target: ", detail);
            Assert.Contains("Pass-the-Hash vulnerable: ", detail);
            Assert.Contains("marked to recalculate", detail);

            var findings = JsonNode.Parse(await admin.GetStringAsync("/api/admin/risk-score/findings"))!;
            Assert.NotNull(findings["total"]);
            Assert.NotNull(findings["findings"]!.AsArray());
        }
        finally
        {
            await connection.ExecuteAsync("""
                UPDATE web.app_config SET DefaultAddressTargetRiskScore = @a, DefaultTargetRiskScore = @t, DefaultAccessGroupRiskScore = @g;
                UPDATE web.risk_finding_type SET RiskValue = @originalValue WHERE FindingType = 'DnaPassTheHash';
                """, new { a = original.Item1, t = original.Item2, g = original.Item3, originalValue });
        }
    }
}
