using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-221: GET /api/reports/trends, its filter options and its export -- shape, input checks, policy, audit.</summary>
public class TrendsReportTests : IClassFixture<BlueTrackWebApplicationFactory>, IAsyncLifetime
{
    private readonly BlueTrackWebApplicationFactory _factory;
    private TrendsTestData.Keys _keys = null!;

    public TrendsReportTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync() => _keys = await TrendsTestData.SeedAsync();

    public Task DisposeAsync() => TrendsTestData.RemoveAsync();

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private const string Range = "from=2026-02-01&to=2026-02-14";

    [Fact]
    public async Task Trends_ReturnsThePeriodsAndSeries_ForAViewer()
    {
        var response = await Client("TestUser.Viewer").GetAsync($"/api/reports/trends?{Range}&granularity=week&safeKey={_keys.SafeAKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("week", (string?)body["granularity"]);
        Assert.Equal("2026-02-01", (string?)body["from"]);
        Assert.Equal(5, body["stages"]!.AsArray().Count);
        Assert.Equal("Not set", (string?)body["riskLevels"]!.AsArray()[^1]!["name"]);
        var periods = body["periods"]!.AsArray();
        Assert.Equal(2, periods.Count);
        Assert.Equal("2026-02-02", (string?)periods[0]!["periodStart"]);
        Assert.Equal("2026-02-04", (string?)periods[0]!["snapshotDate"]);
        Assert.Equal(2, (int)periods[0]!["totalAccounts"]!);
        Assert.Equal(50.0, (double)periods[0]!["percentComplete"]!);
    }

    [Theory]
    [InlineData("from=2026-02-14&to=2026-02-01")]                    // from after to
    [InlineData("from=2024-01-01&to=2026-02-01")]                    // more than two years at Day
    [InlineData("from=2020-01-01&to=2026-02-01&granularity=week")]   // more than five years at Week
    [InlineData("granularity=month")]
    [InlineData("from=not-a-date")]
    [InlineData("safeKey=0")]
    [InlineData("applicationKey=-3")]
    public async Task Trends_RejectsBadInput(string query)
    {
        var viewer = Client("TestUser.Viewer");

        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync($"/api/reports/trends?{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync($"/api/reports/trends/export?format=csv&{query}")).StatusCode);
    }

    [Fact]
    public async Task Trends_DefaultsToTheLast90Days_ByDay()
    {
        var response = await Client("TestUser.Viewer").GetAsync("/api/reports/trends");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var from = DateOnly.Parse((string)body["from"]!);
        var to = DateOnly.Parse((string)body["to"]!);
        Assert.Equal(89, to.DayNumber - from.DayNumber);
        Assert.Equal("day", (string?)body["granularity"]);
    }

    [Theory]
    [InlineData("/api/reports/trends")]
    [InlineData("/api/reports/trends/filter-options")]
    [InlineData("/api/reports/trends/export?format=csv")]
    public async Task Anonymous_IsUnauthorized(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/reports/trends")]
    [InlineData("/api/reports/trends/filter-options")]
    [InlineData("/api/reports/trends/export?format=csv")]
    public async Task WithoutViewDashboard_IsForbidden(string path)
    {
        var response = await Client("TestUser.DoesNotExist").GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FilterOptions_ListApplicationsAndSafes()
    {
        var response = await Client("TestUser.Viewer").GetAsync("/api/reports/trends/filter-options");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains(body["applications"]!.AsArray(), a => (int)a!["applicationKey"]! == _keys.ApplicationKey);
        var safe = Assert.Single(body["safes"]!.AsArray(), s => (int)s!["safeKey"]! == _keys.SafeAKey);
        Assert.Equal(_keys.ApplicationKey, (int)safe!["applicationKey"]!);
    }

    private static string[] ExpectedHeaders =>
    [
        "Period", "Snapshot Date",
        "Stage: Discovered", "Stage: Assessed / Prioritized", "Stage: Onboarded to Vault", "Stage: Managed / Rotation Enabled", "Stage: Verified / Compliant",
        "Total Accounts", "In Scope", "Compliant", "% Complete",
        "Risk: Low", "Risk: Medium", "Risk: High", "Risk: Critical", "Risk: Not set"
    ];

    [Fact]
    public async Task Csv_HasOneRowPerPeriod_WithEverySeries()
    {
        var response = await Client("TestUser.Viewer").GetAsync($"/api/reports/trends/export?format=csv&{Range}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^BlueTrack_Trends_\d{8}-\d{4}\.csv$", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var lines = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ExpectedHeaders, lines[0].Split(','));
        Assert.Equal(4, lines.Length);
        Assert.Equal("2026-02-04,2026-02-04,1,0,1,0,1,3,2,1,50,1,1,1,0,0", lines[2]);
        Assert.Equal("2026-02-10,2026-02-10,0,0,0,0,3,3,3,2,66.7,1,1,0,1,0", lines[3]);
    }

    [Fact]
    public async Task Xlsx_HasTheSameHeaders_AndAuditsTheExportWithItsFilters()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(AuditEventKey), 0) FROM web.audit_event");

        var response = await Client("TestUser.Admin").GetAsync($"/api/reports/trends/export?format=xlsx&{Range}&granularity=week&applicationKey={_keys.ApplicationKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheet(1);
        Assert.Equal(ExpectedHeaders, sheet.Row(1).CellsUsed().Select(c => c.GetString()));
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());

        var logged = Assert.Single(await connection.QueryAsync<(string EntityKey, string Detail)>("""
            SELECT e.EntityKey, e.Detail
            FROM web.audit_event e
            JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
            WHERE t.EventTypeName = 'ReportExport' AND e.AuditEventKey > @before
            """, new { before }));
        Assert.Equal("trends", logged.EntityKey);
        Assert.Equal($"Trends exported as Excel, 2 rows; from=2026-02-01, to=2026-02-14, granularity=week, applicationKey={_keys.ApplicationKey}", logged.Detail);
    }
}
