using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlueTrack.Api.Tests.Integration;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-231: the Audit Log Viewer's export (GET /api/audit-log/export, D-220's
/// shared export) -- the grid's columns with the UTC timestamp to the second,
/// the viewer's filters and sort over every page, ViewAuditLog, the Export
/// Max Rows refusal, and the ReportExport audit event. Each test seeds its own
/// events under a unique EntityName, so the rest of the log doesn't matter.
/// </summary>
public class AuditLogExportTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private const string Headers = "Occurred At (UTC),Event Type,By,Entity,Detail";
    private readonly BlueTrackWebApplicationFactory _factory;

    public AuditLogExportTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    /// <summary>Three events under a fresh EntityName: FieldEdit (Jan 2), Logon with no entity key (Jan 3), FieldEdit (Jan 4), all by TestUser.Admin.</summary>
    private async Task<(string Tag, string AdminName)> SeedAsync()
    {
        var tag = "xp_" + Guid.NewGuid().ToString("N")[..10];
        // One request as TestUser.Admin, so its app_user row exists.
        Assert.Equal(HttpStatusCode.OK, (await Client("TestUser.Admin").GetAsync("/api/audit-log?pageSize=1")).StatusCode);
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var admin = await connection.QuerySingleAsync<(int UserKey, string? DisplayName)>(
            "SELECT TOP 1 UserKey, DisplayName FROM web.app_user WHERE ExternalIdentifier LIKE '%TestUser.Admin%' ORDER BY UserKey");
        await connection.ExecuteAsync("""
            INSERT INTO web.audit_event (AuditEventTypeKey, PerformedByUserKey, EntityName, EntityKey, Detail, OccurredAt)
            SELECT t.AuditEventTypeKey, @user, @tag, v.EntityKey, v.Detail, v.OccurredAt
            FROM (VALUES ('FieldEdit', '1',  'first, "quoted"', CAST('2026-01-02T03:04:05.678' AS DATETIME2)),
                         ('Logon',     NULL, 'second',          CAST('2026-01-03T10:00:00' AS DATETIME2)),
                         ('FieldEdit', '3',  'third',           CAST('2026-01-04T23:59:59' AS DATETIME2))) v(EventTypeName, EntityKey, Detail, OccurredAt)
            JOIN web.dim_audit_event_type t ON t.EventTypeName = v.EventTypeName
            """, new { user = admin.UserKey, tag });
        return (tag, admin.DisplayName ?? "");
    }

    private static async Task<List<List<string>>> CsvAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ReportExportTests.ParseCsv(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Csv_HasTheGridsColumns_AndEveryMatchingEvent_InTheViewersSortOrder()
    {
        var (tag, adminName) = await SeedAsync();
        var viewer = Client("TestUser.Viewer");
        var response = await viewer.GetAsync($"/api/audit-log/export?format=csv&entityName={tag}&sort=occurredAt:asc");

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(fileName);
        Assert.Matches(new Regex(@"^BlueTrack_AuditLog_\d{8}-\d{4}\.csv$"), fileName);

        var csv = await CsvAsync(response);
        Assert.Equal(Headers.Split(','), csv[0]);
        Assert.Equal(4, csv.Count);
        Assert.Equal(["2026-01-02 03:04:05", "FieldEdit", adminName, $"{tag} 1", "first, \"quoted\""], csv[1]);
        Assert.Equal(["2026-01-03 10:00:00", "Logon", adminName, tag, "second"], csv[2]);
        Assert.Equal("2026-01-04 23:59:59", csv[3][0]);

        var descending = await CsvAsync(await viewer.GetAsync($"/api/audit-log/export?format=csv&entityName={tag}&sort=occurredAt:desc"));
        Assert.Equal(["third", "second", "first, \"quoted\""], descending.Skip(1).Select(r => r[4]));
    }

    [Fact]
    public async Task TheViewersFilters_AreApplied()
    {
        var (tag, _) = await SeedAsync();
        var viewer = Client("TestUser.Viewer");

        var fieldEdits = await CsvAsync(await viewer.GetAsync($"/api/audit-log/export?format=csv&entityName={tag}&eventType=FieldEdit&sort=occurredAt:asc"));
        Assert.Equal(["first, \"quoted\"", "third"], fieldEdits.Skip(1).Select(r => r[4]));

        // toDate takes in the whole day, as in the viewer.
        var dated = await CsvAsync(await viewer.GetAsync($"/api/audit-log/export?format=csv&entityName={tag}&fromDate=2026-01-03&toDate=2026-01-04"));
        Assert.Equal(["second", "third"], dated.Skip(1).Select(r => r[4]).Order());

        // As many rows as the viewer's own (paged) list counts in X-Filtered-Count.
        var list = await viewer.GetAsync($"/api/audit-log?entityName={tag}&eventType=Logon&page=1&pageSize=1");
        var logons = await CsvAsync(await viewer.GetAsync($"/api/audit-log/export?format=csv&entityName={tag}&eventType=Logon"));
        Assert.Equal(int.Parse(list.Headers.GetValues("X-Filtered-Count").Single()), logons.Count - 1);
    }

    [Fact]
    public async Task Xlsx_HasTheGridsColumns_AndKeepsTheTime()
    {
        var (tag, _) = await SeedAsync();
        var response = await Client("TestUser.Admin").GetAsync($"/api/audit-log/export?format=xlsx&entityName={tag}&sort=occurredAt:asc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheet(1);
        Assert.Equal("Audit Log", sheet.Name);
        Assert.Equal(Headers.Split(','), Enumerable.Range(1, 5).Select(c => sheet.Cell(1, c).GetString()));
        Assert.Equal(4, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 1).DataType);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), sheet.Cell(2, 1).GetDateTime());
        Assert.Equal("yyyy-mm-dd hh:mm:ss", sheet.Cell(2, 1).Style.NumberFormat.Format);
        Assert.Equal($"{tag} 3", sheet.Cell(4, 4).GetString());
    }

    [Fact]
    public async Task WithoutViewAuditLog_IsForbidden_AndAnonymousIsUnauthorized()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.DoesNotExist").GetAsync("/api/audit-log/export?format=csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/audit-log/export?format=csv")).StatusCode);
    }

    [Fact]
    public async Task AnUnknownFormat_IsABadRequest()
    {
        var response = await Client("TestUser.Viewer").GetAsync("/api/audit-log/export?format=pdf");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MoreEventsThanExportMaxRows_IsRefusedWithTheCount()
    {
        var (tag, _) = await SeedAsync();
        var admin = Client("TestUser.Admin");
        var original = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        var body = JsonNode.Parse(original.ToJsonString())!.AsObject();
        body["exportMaxRows"] = 2;

        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/configuration", body)).StatusCode);

            var response = await admin.GetAsync($"/api/audit-log/export?format=csv&entityName={tag}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var detail = (await response.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>();
            Assert.StartsWith("3 rows match this report, more than the export limit of 2 rows.", detail);

            // Narrowed to two events, it fits.
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/audit-log/export?format=csv&entityName={tag}&eventType=FieldEdit")).StatusCode);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/configuration", original);
        }
    }

    [Fact]
    public async Task AnExport_WritesAReportExportAuditEvent_WithTheFiltersAndRowCount()
    {
        var (tag, _) = await SeedAsync();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(AuditEventKey), 0) FROM web.audit_event");

        var response = await Client("TestUser.Viewer").GetAsync($"/api/audit-log/export?format=csv&entityName={tag}&fromDate=2026-01-01&sort=occurredAt:asc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var logged = Assert.Single(await connection.QueryAsync<(string EntityName, string EntityKey, string Detail, string Performer)>("""
            SELECT e.EntityName, e.EntityKey, e.Detail, u.ExternalIdentifier
            FROM web.audit_event e
            JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
            JOIN web.app_user u ON u.UserKey = e.PerformedByUserKey
            WHERE t.EventTypeName = 'ReportExport' AND e.AuditEventKey > @before
            """, new { before }));
        Assert.Equal("report", logged.EntityName);
        Assert.Equal("audit-log", logged.EntityKey);
        Assert.Contains("TestUser.Viewer", logged.Performer);
        Assert.Equal($"Audit Log exported as CSV, 3 rows; entityName={tag}, fromDate=2026-01-01, sort=occurredAt:asc", logged.Detail);
    }
}
