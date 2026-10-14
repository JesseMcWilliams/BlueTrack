using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-234: Admin > SIEM Forwarding (ManageApplicationConfiguration): permissions, validation, saving (audited as
/// FieldEdit, turning forwarding on starts after the existing rows), and the Test button (audited as
/// ConnectionTest) against an in-process collector on 127.0.0.1. Settings are put back afterwards.
/// </summary>
[Collection("SiemForwarding")]
public sealed class SiemForwardingControllerTests : IClassFixture<BlueTrackWebApplicationFactory>, IAsyncLifetime
{
    private const string Base = "/api/admin/siem-forwarding";
    private readonly BlueTrackWebApplicationFactory _factory;
    private List<(int Key, bool IsForwarded, int CefSeverity)> _typeSettings = [];

    public SiemForwardingControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        _typeSettings = (await connection.QueryAsync<(int, bool, int)>(
            "SELECT AuditEventTypeKey, IsForwarded, CAST(CefSeverity AS INT) FROM web.siem_event_type_setting")).AsList();
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            UPDATE web.siem_forwarding_config SET IsEnabled = 0, Host = NULL, Port = 6514, Transport = 'Tls', Framing = 'OctetCounting',
                TlsTrustMode = 'System', TlsFingerprint = NULL, TlsCaCertificate = NULL, TlsCheckRevocation = 1, TimeoutSeconds = 10,
                MaxMessageBytes = 8192, SyslogHostName = NULL, AppName = 'BlueTrack', Facility = 20
            WHERE SiemForwardingConfigKey = 1;
            UPDATE web.siem_forwarding_state SET Watermark = NULL, LeaseOwner = NULL, LeaseExpiresAt = NULL, LastError = NULL, LastErrorAt = NULL,
                ConsecutiveFailures = 0 WHERE SiemForwardingStateKey = 1;
            """);
        foreach (var (key, isForwarded, severity) in _typeSettings)
        {
            await connection.ExecuteAsync("UPDATE web.siem_event_type_setting SET IsForwarded = @isForwarded, CefSeverity = @severity WHERE AuditEventTypeKey = @key",
                new { key, isForwarded, severity });
        }
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static object Settings(bool enabled = false, string host = "127.0.0.1", int port = 6514, string transport = "Tcp", object[]? eventTypes = null) => new
    {
        isEnabled = enabled,
        host,
        port,
        transport,
        framing = "OctetCounting",
        facility = 20,
        appName = "BlueTrack",
        syslogHostName = "bt-contract",
        timeoutSeconds = 5,
        maxMessageBytes = 8192,
        tlsTrustMode = "System",
        tlsCheckRevocation = true,
        eventTypes
    };

    private static async Task<List<string>> FieldEditChangesAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return (await connection.QueryAsync<string>("""
            SELECT fc.FieldName + '=' + ISNULL(fc.NewValue, '') FROM web.audit_field_change fc
            WHERE fc.AuditEventKey = (SELECT MAX(e.AuditEventKey) FROM web.audit_event e
                JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
                WHERE t.EventTypeName = 'FieldEdit' AND e.EntityName = 'siem_forwarding_config')
            """)).AsList();
    }

    [Theory]
    [InlineData("GET", Base)]
    [InlineData("PUT", Base)]
    [InlineData("POST", Base + "/test")]
    public async Task WithoutManageApplicationConfiguration_IsForbidden(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET") request.Content = JsonContent.Create(Settings());
        var response = await Client("TestUser.Viewer").SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsSettingsEveryEventTypeAndStatus()
    {
        var body = (await Client("TestUser.Admin").GetFromJsonAsync<JsonObject>(Base))!;
        Assert.NotNull(body["settings"]);
        Assert.NotNull(body["status"]);
        var types = body["eventTypes"]!.AsArray();
        var breakGlass = types.Single(t => t!["eventTypeName"]!.GetValue<string>() == "BreakGlassLogon")!;
        Assert.Equal(8, breakGlass["cefSeverity"]!.GetValue<int>());
        Assert.True(breakGlass["isForwarded"]!.GetValue<bool>());
        Assert.Contains(types, t => t!["eventTypeName"]!.GetValue<string>() == "RecordViewed");
    }

    [Theory]
    [InlineData("""{"isEnabled":true,"host":"","port":6514,"transport":"Tls","framing":"OctetCounting","facility":20,"timeoutSeconds":10,"maxMessageBytes":8192}""", "host name or IP")]
    [InlineData("""{"isEnabled":false,"host":"x","port":6514,"transport":"Udp","framing":"OctetCounting","facility":20,"timeoutSeconds":10,"maxMessageBytes":8192}""", "Transport must be")]
    [InlineData("""{"isEnabled":false,"host":"x","port":6514,"transport":"Tls","framing":"OctetCounting","facility":20,"timeoutSeconds":10,"maxMessageBytes":8192,"tlsTrustMode":"IgnoreAll"}""", "TLS trust must be")]
    [InlineData("""{"isEnabled":false,"host":"x","port":6514,"transport":"Tls","framing":"OctetCounting","facility":20,"timeoutSeconds":10,"maxMessageBytes":8192,"eventTypes":[{"auditEventTypeKey":999999,"isForwarded":true,"cefSeverity":5}]}""", "Unknown audit event type")]
    public async Task InvalidSettings_Are400(string json, string expected)
    {
        var response = await Client("TestUser.Admin").PutAsync(Base, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await response.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Save_IsAudited_AndTurningItOnStartsAfterTheExistingRows()
    {
        var admin = Client("TestUser.Admin");
        var port = SyslogTestListener.FreePort();
        var types = (await admin.GetFromJsonAsync<JsonObject>(Base))!["eventTypes"]!.AsArray();
        var recordViewed = types.Single(t => t!["eventTypeName"]!.GetValue<string>() == "RecordViewed")!["auditEventTypeKey"]!.GetValue<int>();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Base, Settings(port: port,
            eventTypes: [new { auditEventTypeKey = recordViewed, isForwarded = false, cefSeverity = 2 }]))).StatusCode);
        var changes = await FieldEditChangesAsync();
        Assert.Contains($"Port={port}", changes);
        Assert.Contains("RecordViewed.IsForwarded=False", changes);
        Assert.Contains("RecordViewed.CefSeverity=2", changes);

        long maxKey;
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync("UPDATE web.siem_forwarding_state SET Watermark = NULL, EnabledFromAuditEventKey = NULL");
            maxKey = await connection.QuerySingleAsync<long>("SELECT MAX(AuditEventKey) FROM web.audit_event");
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Base, Settings(enabled: true, port: port))).StatusCode);
        Assert.Contains("IsEnabled=True", await FieldEditChangesAsync());
        var status = (await admin.GetFromJsonAsync<JsonObject>(Base))!["status"]!;
        Assert.NotNull(status["watermark"]);
        Assert.True(status["enabledFromAuditEventKey"]!.GetValue<long>() >= maxKey);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Base, Settings(enabled: false, port: port))).StatusCode);
        var after = (await admin.GetFromJsonAsync<JsonObject>(Base))!;
        Assert.False(after["settings"]!["isEnabled"]!.GetValue<bool>());
        Assert.False(after["eventTypes"]!.AsArray().Single(t => t!["auditEventTypeKey"]!.GetValue<int>() == recordViewed)!["isForwarded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TestButton_SendsOneCefEvent_AndIsAuditedEitherWay()
    {
        var admin = Client("TestUser.Admin");
        await using (var listener = new SyslogTestListener())
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Base, Settings(port: listener.Port))).StatusCode);
            var ok = (await (await admin.PostAsync($"{Base}/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.True(ok["success"]!.GetValue<bool>());
            var message = Assert.Single(await listener.WaitForAsync(1));
            Assert.StartsWith("<166>1 ", message);   // local4, CEF 1 -> Informational
            Assert.Contains(" bt-contract BlueTrack - SiemForwardingTest - CEF:0|BlueTrack|BlueTrack|", message);
            Assert.Contains("|SiemForwardingTest|SIEM forwarding test event|1|", message);
            Assert.Contains("sent to", await ConnectionTestAudit.LatestDetailAsync("siem_forwarding_config", $"127.0.0.1:{listener.Port}"));
        }

        var closedPort = SyslogTestListener.FreePort();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Base, Settings(port: closedPort))).StatusCode);
        var failed = (await (await admin.PostAsync($"{Base}/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(failed["success"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(failed["error"]!.GetValue<string>()));
        Assert.Contains("failed", await ConnectionTestAudit.LatestDetailAsync("siem_forwarding_config", $"127.0.0.1:{closedPort}"));
    }
}
