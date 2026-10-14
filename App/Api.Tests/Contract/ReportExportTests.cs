using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlueTrack.Api.Tests.Integration;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-220: every report's CSV/.xlsx export -- content type, file name and the
/// grid's header row; the report's own permission; the page's filters and
/// sort over every page; the Export Max Rows refusal; and the ReportExport
/// audit event.
/// </summary>
public class ReportExportTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ReportExportTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    // Path, file-name stem, the grid's headers (null: built from the data), and a signed-in user without the report's permission.
    public static TheoryData<string, string, string?, string> Reports => new()
    {
        { "/api/reports/overdue-at-risk/export", "OverdueAtRisk", "Account,Stage,Status,Risk Level,Owner,Target Remediation Date", "TestUser.DoesNotExist" },
        { "/api/reports/stage-status-summary/export", "StageStatusSummary", null, "TestUser.DoesNotExist" },
        { "/api/reports/reconciliation-review-queue/export", "ReconciliationReview", "Self-Hosted Account,Privilege Cloud Account,Match Method,Confidence,Matched Date,Notes", "TestUser.Viewer" },
        { "/api/reports/possible-matches/export", "PossibleAccountMatches", "Other system's account,CyberArk account,Why,Found", "TestUser.Viewer" },
        { "/api/reports/unresolved-entitlement-members/export", "UnresolvedEntitlementMembers", "Safe,Member Type,Name,Member ID,Resolution,Membership Expiration,Snapshot Date", "TestUser.DoesNotExist" },
        { "/api/reports/risk-score/export", "RiskScore", "Account,Computed,Override,Effective,Risk Band,Stale?,Last Calculated", "TestUser.Viewer" },
        { "/api/reports/risk-exception-sod/export", "RiskExceptionSoD", "Exception ID,Account,User,Linked On", "TestUser.Viewer" },
        { "/api/reports/discovered-accounts/export", "DiscoveredAccounts", "Account,Domain,Account Class,Owner,Matched By,Risk Score,Risk Band,First Discovered,Last Seen", "TestUser.Viewer" },
        { "/api/reports/decom-safes/export", "SafesFlaggedForDeletion", "Safe,Source,Application,Accounts not yet deleted,Deleted accounts", "TestUser.DoesNotExist" },
        { "/api/reports/decom-accounts/export", "AccountsFlaggedForDeletion", "Username,Address,Account Name,Safe,Source,Flagged by,Note", "TestUser.DoesNotExist" },
        { "/api/reports/kpi-summary/export", "KpiSummary", "KPI,Count,Percentage", "TestUser.DoesNotExist" },
    };

    /// <summary>RFC 4180 records (after the BOM): fields split on commas outside quotes, "" unescaped.</summary>
    internal static List<List<string>> ParseCsv(byte[] bytes)
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                record.Add(field.ToString()); field.Clear();
                records.Add(record); record = [];
                i++;
            }
            else field.Append(ch);
        }
        return records;
    }

    private static void AssertFileName(HttpResponseMessage response, string stem, string extension)
    {
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(fileName);
        Assert.Matches(new Regex($@"^BlueTrack_{stem}_\d{{8}}-\d{{4}}\.{extension}$"), fileName);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    private static void AssertHeaders(string? expected, IReadOnlyList<string> actual)
    {
        if (expected is not null)
        {
            Assert.Equal(expected.Split(','), actual);
            return;
        }
        Assert.Equal("Stage", actual[0]);
        Assert.Equal("Total", actual[^1]);
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Csv_IsUtf8CsvWithTheGridsHeaderRow_AndAFileName(string path, string stem, string? headers, string _)
    {
        var response = await Client("TestUser.Admin").GetAsync($"{path}?format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        AssertFileName(response, stem, "csv");
        AssertHeaders(headers, ParseCsv(await response.Content.ReadAsByteArrayAsync())[0]);
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Xlsx_IsAWorkbookWithTheGridsHeaderRow_AndAFileName(string path, string stem, string? headers, string _)
    {
        var response = await Client("TestUser.Admin").GetAsync($"{path}?format=xlsx");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        AssertFileName(response, stem, "xlsx");
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheet(1);
        var lastColumn = sheet.Row(1).LastCellUsed()!.Address.ColumnNumber;
        AssertHeaders(headers, Enumerable.Range(1, lastColumn).Select(c => sheet.Cell(1, c).GetString()).ToList());
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task WithoutTheReportsOwnPermission_IsForbidden(string path, string _, string? __, string userWithout)
    {
        var response = await Client(userWithout).GetAsync($"{path}?format=csv");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/api/reports/overdue-at-risk/export?format=csv");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?format=pdf")]
    public async Task AnUnknownFormat_IsABadRequest(string query)
    {
        var response = await Client("TestUser.Viewer").GetAsync($"/api/reports/kpi-summary/export{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RiskScore_ExportsEveryPage_InThePagesSortOrder()
    {
        var admin = Client("TestUser.Admin");
        var page = await admin.GetAsync("/api/reports/risk-score?sort=accountName:desc&page=1&pageSize=1");
        var filtered = int.Parse(page.Headers.GetValues("X-Filtered-Count").Single());
        Assert.InRange(filtered, 1, 500);   // BlueTrackTest's synthetic data; keeps the page comparison below to one page

        var exported = ParseCsv(await (await admin.GetAsync("/api/reports/risk-score/export?format=csv&sort=accountName:desc")).Content.ReadAsByteArrayAsync())
            .Skip(1).Select(r => r[0]).ToList();
        Assert.Equal(filtered, exported.Count);   // every page, not the one asked for above

        var onePage = (await admin.GetFromJsonAsync<JsonArray>("/api/reports/risk-score?sort=accountName:desc&page=1&pageSize=500"))!
            .Select(r => r!["accountName"]!.GetValue<string>()).ToList();
        Assert.Equal(onePage, exported);

        var ascending = ParseCsv(await (await admin.GetAsync("/api/reports/risk-score/export?format=csv&sort=accountName:asc")).Content.ReadAsByteArrayAsync())
            .Skip(1).Select(r => r[0]).ToList();
        // Names only, so accounts with the same name (in any order) still line up; the
        // database's collation ignores case, so the comparison does too.
        Assert.Equal(Enumerable.Reverse(exported), ascending, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DecomAccounts_NotDeletedOnly_LeavesOutDeletedAccounts()
    {
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var admin = Client("TestUser.Admin");
        var original = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DECLARE @src INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY');
            INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@src, @safe, @safe);
            DECLARE @safeKey INT = (SELECT SafeKey FROM dim_safe WHERE SafeUrlId = @safe);
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, Address, SafeKey, IsDeleted)
            VALUES (@src, @tag + '-1', @tag + '-live', 'svc_' + @tag, 'host-' + @tag, @safeKey, 0),
                   (@src, @tag + '-2', @tag + '-gone', 'old_' + @tag, 'host-' + @tag, @safeKey, 1);
            """, new { tag, safe = $"XPD{tag}_Old" });

        try
        {
            var body = JsonNode.Parse(original.ToJsonString())!.AsObject();
            body["safeDecomMode"] = "Prefix"; body["safeDecomValue"] = $"XPD{tag}_";
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/configuration", body)).StatusCode);

            async Task<List<string>> NamesAsync(string query) =>
                ParseCsv(await (await admin.GetAsync($"/api/reports/decom-accounts/export?format=csv{query}")).Content.ReadAsByteArrayAsync())
                    .Skip(1).Select(r => r[2]).Where(n => n.StartsWith(tag)).OrderBy(n => n).ToList();

            Assert.Equal([$"{tag}-gone", $"{tag}-live"], await NamesAsync(""));
            Assert.Equal([$"{tag}-live"], await NamesAsync("&notDeletedOnly=true"));
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/configuration", original);
            await connection.ExecuteAsync("""
                DELETE FROM fact_account WHERE SourceAccountId LIKE @tag + '-%';
                DELETE FROM dim_safe WHERE SafeUrlId = @safe;
                """, new { tag, safe = $"XPD{tag}_Old" });
        }
    }

    [Fact]
    public async Task MoreRowsThanExportMaxRows_IsRefusedWithTheCount_NotCutShort()
    {
        var admin = Client("TestUser.Admin");
        var original = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        Assert.InRange(original["exportMaxRows"]!.GetValue<int>(), 1, 1000000);

        JsonObject With(int max)
        {
            var body = JsonNode.Parse(original.ToJsonString())!.AsObject();
            body["exportMaxRows"] = max;
            return body;
        }

        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/configuration", With(0))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/configuration", With(1000001))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/configuration", With(1))).StatusCode);
            Assert.Equal(1, (await admin.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!["exportMaxRows"]!.GetValue<int>());

            // Every stage is a row, so the funnel always has more than one.
            var response = await admin.GetAsync("/api/reports/stage-status-summary/export?format=xlsx");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var detail = problem["detail"]!.GetValue<string>();
            Assert.Matches(@"^\d[\d,]* rows match this report, more than the export limit of 1 rows\. Narrow the report's filters", detail);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/configuration", original);
        }
    }

    [Fact]
    public async Task AnExport_WritesAReportExportAuditEvent_WithWhoWhatAndHowMany()
    {
        var admin = Client("TestUser.Admin");
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(AuditEventKey), 0) FROM web.audit_event");

        var response = await admin.GetAsync("/api/reports/risk-score/export?format=xlsx&sort=accountName:desc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var rows = (workbook.Worksheet(1).LastRowUsed()?.RowNumber() ?? 1) - 1;

        var events = (await connection.QueryAsync<(string EntityName, string EntityKey, string Detail, string Performer)>("""
            SELECT e.EntityName, e.EntityKey, e.Detail, u.ExternalIdentifier
            FROM web.audit_event e
            JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
            JOIN web.app_user u ON u.UserKey = e.PerformedByUserKey
            WHERE t.EventTypeName = 'ReportExport' AND e.AuditEventKey > @before
            """, new { before })).ToList();
        var logged = Assert.Single(events);
        Assert.Equal("report", logged.EntityName);
        Assert.Equal("risk-score", logged.EntityKey);
        Assert.Contains("TestUser.Admin", logged.Performer);
        Assert.Equal($"Risk Score exported as Excel, {rows} rows; sort=accountName:desc", logged.Detail);
    }

    [Fact]
    public async Task ARefusedExport_IsNotAuditLogged()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(AuditEventKey), 0) FROM web.audit_event");

        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/reports/risk-score/export?format=csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client("TestUser.Admin").GetAsync("/api/reports/risk-score/export?format=txt")).StatusCode);

        Assert.Equal(0, await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
            WHERE t.EventTypeName = 'ReportExport' AND e.AuditEventKey > @before
            """, new { before }));
    }
}
