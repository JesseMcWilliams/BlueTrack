using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-325: the DNA account-list import. The workbook here is made up, in the shape of a real one: an Overview sheet, finding
/// sheets with an "Account Name" header (one with a title row above it), a raw database sheet, and a hard-coded credentials
/// sheet, whose rows and any credential column must never be stored.
/// </summary>
[Collection("DnaImport")]   // each import replaces the stored DNA accounts, so these run one at a time
public class DnaImportTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private const string SecretValue = "ThisIsMy_FAKE_Password6!";

    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static byte[] Workbook()
    {
        using var workbook = new XLWorkbook();
        var overview = workbook.AddWorksheet("Overview");
        overview.Cell(1, 1).Value = "Name";
        overview.Cell(1, 2).Value = "Count (Entries)";

        // A domain account BlueTrack tracks (dna_linked01 on corp.example, D-346: by user name and its domain), and one it
        // doesn't. A stray credential column is ignored.
        var domain = workbook.AddWorksheet("NC-DA");
        string[] domainHeader = ["Machine Name", "Account Name", "Account Display Name", "Account Category", "Password Never Expires", "Password Age", "Password Last Set", "Hard-Coded Credential", "Account Type"];
        for (var c = 0; c < domainHeader.Length; c++) domain.Cell(1, c + 1).Value = domainHeader[c];
        domain.Cell(2, 1).Value = "SRV01"; domain.Cell(2, 2).Value = @"CORP\dna_linked01"; domain.Cell(2, 3).Value = "Test 1"; domain.Cell(2, 4).Value = "Privileged";
        domain.Cell(2, 5).Value = "Yes"; domain.Cell(2, 6).Value = 400; domain.Cell(2, 7).Value = new DateTime(2025, 1, 15); domain.Cell(2, 8).Value = SecretValue;
        domain.Cell(2, 9).Value = "Domain: corp.example"; domain.Cell(3, 9).Value = "Domain: corp.example";
        domain.Cell(3, 1).Value = "SRV02"; domain.Cell(3, 2).Value = "dna_untracked"; domain.Cell(3, 5).Value = "No"; domain.Cell(3, 6).Value = 120; domain.Cell(3, 7).Value = "Never";

        // A database finding whose header sits under a title row.
        var database = workbook.AddWorksheet("DB-P-SRV");
        database.Cell(1, 1).Value = "Parsed database scan";
        string[] dbHeader = ["Machine Name", "Instance Name", "Account Name", "Database Name", "Account State"];
        for (var c = 0; c < dbHeader.Length; c++) database.Cell(2, c + 1).Value = dbHeader[c];
        database.Cell(3, 1).Value = "DBSRV"; database.Cell(3, 2).Value = "MSSQLSERVER"; database.Cell(3, 3).Value = "sa"; database.Cell(3, 4).Value = "master"; database.Cell(3, 5).Value = "Enabled";

        // Never read: the raw database scan, and the hard-coded credentials.
        var raw = workbook.AddWorksheet("DB-R");
        raw.Cell(1, 1).Value = "Account Name"; raw.Cell(2, 1).Value = "raw_should_not_import";
        var hcc = workbook.AddWorksheet("HCC");
        hcc.Cell(1, 1).Value = "Machine Name"; hcc.Cell(1, 2).Value = "Account Name"; hcc.Cell(1, 3).Value = "Hard-Coded Credential";
        hcc.Cell(2, 1).Value = "APP01"; hcc.Cell(2, 2).Value = "hcc_should_not_import"; hcc.Cell(2, 3).Value = $"Line: 3 - \"Server=db;User Id=sa;Password={SecretValue};\"";   // D-345: unmasked, so the import masks it

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", fileName);
        return content;
    }

    [Fact]
    public async Task Import_StoresTheFindingSheetsAccounts_NeverTheCredentials_AndMatchesTrackedAccounts()
    {
        await using var setup = new SqlConnection(TestDatabase.ConnectionString);
        var linkedKey = await setup.QuerySingleAsync<long>("""
            DELETE p FROM dbo.fact_account_progress p JOIN dbo.fact_account fa ON fa.AccountKey = p.AccountKey WHERE fa.SourceAccountId = 'dna-import-linked01';
            DELETE FROM dbo.fact_account WHERE SourceAccountId = 'dna-import-linked01';
            INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, Address, IsDeleted, CreatedDate)
            OUTPUT inserted.AccountKey
            SELECT SourceSystemKey, 'dna-import-linked01', 'dna_linked01', 'dna_linked01', 'corp.example', 0, CAST(SYSUTCDATETIME() AS DATE)
            FROM dbo.dim_source_system WHERE SourceSystemCode = 'SELFHOSTED';
            """);
        try
        {
        var response = await Client("TestUser.Admin").PostAsync("/api/admin/dna/import", Upload(Workbook(), "dna-report.xlsx"));
        response.EnsureSuccessStatusCode();
        var latest = (await response.Content.ReadFromJsonAsync<JsonObject>())!["latest"]!;
        Assert.Equal(3, latest["accountCount"]!.GetValue<int>());
        Assert.Equal("NC-DA=2; DB-P-SRV=1", latest["sheetCounts"]!.GetValue<string>());
        Assert.Equal(1, latest["trackedCount"]!.GetValue<int>());

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var names = (await connection.QueryAsync<string>("SELECT AccountName FROM web.dna_account")).ToList();
        Assert.DoesNotContain("hcc_should_not_import", names);
        Assert.DoesNotContain("raw_should_not_import", names);
        // The secret appears nowhere: not in a row, not in the audit entry.
        Assert.Equal(0, await connection.QuerySingleAsync<int>("""
            SELECT (SELECT COUNT(*) FROM web.dna_account d
                    WHERE CONCAT(d.MachineName, d.AccountName, d.DisplayName, d.AccountType, d.AccountCategory, d.AccountGroup, d.PrivilegedDomainGroup,
                                 d.InstanceName, d.DatabaseName, d.ComplianceStatus, d.AccountState) LIKE '%' + @secret + '%')
                 + (SELECT COUNT(*) FROM web.audit_event WHERE Detail LIKE '%' + @secret + '%')
            """, new { secret = SecretValue }));

        var tracked = await connection.QuerySingleAsync<(long? Key, int? Age, DateTime? LastSet, bool? NeverExpires)>(
            "SELECT TrackedAccountKey, PasswordAgeDays, PasswordLastSet, PasswordNeverExpires FROM web.dna_account WHERE AccountName = @name", new { name = @"CORP\dna_linked01" });
        Assert.Equal(linkedKey, tracked.Key);
        Assert.Equal((400, new DateTime(2025, 1, 15), true), (tracked.Age, tracked.LastSet, tracked.NeverExpires));
        Assert.Null(await connection.QuerySingleAsync<DateTime?>("SELECT PasswordLastSet FROM web.dna_account WHERE AccountName = 'dna_untracked'"));   // "Never"

        var page = await Client("TestUser.Admin").GetAsync("/api/reports/dna-accounts?tracked=false&sheet=NC-DA");
        var rows = (await page.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Equal(["dna_untracked"], rows.Select(r => r!["accountName"]!.GetValue<string>()));
        Assert.Equal("3", page.Headers.GetValues("X-Total-Count").Single());
        }
        finally
        {
            await setup.ExecuteAsync("""
                UPDATE web.dna_account SET TrackedAccountKey = NULL WHERE TrackedAccountKey = @linkedKey;
                DELETE FROM dbo.fact_account WHERE AccountKey = @linkedKey;
                """, new { linkedKey });
        }
    }

    [Fact]
    public async Task Import_RefusesAFileThatIsNotAWorkbook_OrHasNoAccountSheets()
    {
        var client = Client("TestUser.Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/dna/import", Upload("not a workbook"u8.ToArray(), "dna.xlsx"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/dna/import", Upload(Workbook(), "dna.csv"))).StatusCode);

        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Overview").Cell(1, 1).Value = "Name";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/dna/import", Upload(stream.ToArray(), "dna.xlsx"))).StatusCode);
    }

    [Fact]
    public async Task Viewer_CannotImport_NorReadTheAccountsWithoutViewDiscoveredAccounts()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").PostAsync("/api/admin/dna/import", Upload(Workbook(), "dna.xlsx"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/reports/dna-accounts")).StatusCode);
    }
}
