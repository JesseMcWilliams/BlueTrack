using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-318: the Accounts export, and reading it back with changed values. D-323: clearing blanks, overrides, Excel.</summary>
public class AccountProgressExportImportTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static MultipartFormDataContent Upload(string csv, string? reason = null, bool clearBlanks = false) =>
        UploadBytes(Encoding.UTF8.GetBytes(csv), "accounts.csv", "text/csv", reason, clearBlanks);

    private static MultipartFormDataContent UploadBytes(byte[] bytes, string fileName, string contentType, string? reason = null, bool clearBlanks = false)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        if (reason is not null) content.Add(new StringContent(reason), "reason");
        if (clearBlanks) content.Add(new StringContent("true"), "clearBlanks");
        return content;
    }

    private static async Task<string?> OwnerOfAsync(long accountKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<string?>("SELECT OwnerName FROM dbo.fact_account_progress WHERE AccountKey = @accountKey", new { accountKey });
    }

    private static async Task<int?> OverrideOfAsync(long accountKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<int?>("SELECT OverrideRiskScore FROM web.account_risk_score WHERE AccountKey = @accountKey", new { accountKey });
    }

    private async Task<JsonObject> ImportAsync(MultipartFormDataContent upload)
    {
        var response = await Client("TestUser.Admin").PostAsync("/api/account-progress/import", upload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static List<(int Row, string Reason)> Problems(JsonObject result) =>
        result["problems"]!.AsArray().Select(p => (p!["rowNumber"]!.GetValue<int>(), p["reason"]!.GetValue<string>())).ToList();

    [Fact]
    public async Task Export_HasTheImportsHeaders_AndTheFilteredAccounts()
    {
        var response = await Client("TestUser.Viewer").GetAsync("/api/account-progress/export?format=csv&search=TestAccount03");
        response.EnsureSuccessStatusCode();
        var lines = (await response.Content.ReadAsStringAsync()).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Account Key,Username,Address,Account Name,Safe,Stage,Status,Risk Level,Account Type,Source of Record,Application,Owner,Business Unit,"
            + "Target Remediation Date,Actual Completion Date,Notes,Risk Score Override,Override Reason", lines[0]);
        Assert.Contains(lines.Skip(1), l => l.Contains("TestAccount03"));
    }

    // An account of its own: the four seeded test accounts are edited by tests that run alongside this one.
    private static async Task<long> CreateAccountAsync(string? owner = null)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @id, 'ContractTest Import Account', 0)",
            new { id = $"ContractTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync("""
            INSERT INTO dbo.fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey, OwnerName)
            SELECT @accountKey, (SELECT TOP (1) StageKey FROM dbo.dim_blueprint_stage ORDER BY StageOrder),
                   (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'In Progress'), @owner
            """, new { accountKey, owner });
        return accountKey;
    }

    private static async Task DeleteAccountAsync(long accountKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @accountKey;
            DELETE FROM web.account_progress_lock WHERE AccountKey = @accountKey;
            DELETE FROM dbo.fact_account_progress WHERE AccountKey = @accountKey;
            DELETE FROM web.account_risk_score WHERE AccountKey = @accountKey;
            DELETE FROM fact_account WHERE AccountKey = @accountKey;
            """, new { accountKey });
    }

    [Fact]
    public async Task Import_AppliesTheNonBlankCells_ReportsTheRowsItCouldNot_AndLeavesBlanksAlone()
    {
        var accountKey = await CreateAccountAsync();
        var owner = $"Imported {Guid.NewGuid():N}"[..20];
        try
        {
            var csv = "Account Key,Username,Owner,Stage\n"
                + $"{accountKey},ignored,{owner},\n"
                + $"{accountKey},ignored,,No Such Stage\n"
                + "999999999,ignored,Someone,\n"
                + "not-a-number,,Someone,\n";
            var result = await ImportAsync(Upload(csv));

            Assert.Equal(4, result["rows"]!.GetValue<int>());
            Assert.Equal(1, result["updated"]!.GetValue<int>());
            var problems = Problems(result);
            Assert.Contains(problems, p => p.Row == 3 && p.Reason.Contains("isn't one of the choices"));
            Assert.Contains(problems, p => p.Row == 4 && p.Reason == "No account has this key.");
            Assert.Contains(problems, p => p.Row == 5 && p.Reason.Contains("isn't a number"));
            Assert.Equal(owner, await OwnerOfAsync(accountKey));
        }
        finally
        {
            await DeleteAccountAsync(accountKey);
        }
    }

    // D-323: with "clear blanks", a blank cell clears its field (never Stage or Status); without it, it changes nothing.
    [Fact]
    public async Task ClearBlanks_ClearsAFieldWhoseCellIsBlank_ButNeverStageOrStatus()
    {
        var accountKey = await CreateAccountAsync(owner: "Keep Me");
        try
        {
            var csv = $"Account Key,Owner,Stage,Status\n{accountKey},,,\n";
            await ImportAsync(Upload(csv));
            Assert.Equal("Keep Me", await OwnerOfAsync(accountKey));

            var result = await ImportAsync(Upload(csv, clearBlanks: true));
            Assert.Equal(1, result["updated"]!.GetValue<int>());
            Assert.Empty(Problems(result));
            Assert.Null(await OwnerOfAsync(accountKey));
        }
        finally
        {
            await DeleteAccountAsync(accountKey);
        }
    }

    // D-323: the override columns: a score with a reason sets it, a score without one is refused, and a blank clears it with "clear blanks".
    [Fact]
    public async Task OverrideColumns_SetTheOverrideWithItsReason_AndClearBlanksClearsIt()
    {
        var accountKey = await CreateAccountAsync();
        try
        {
            var result = await ImportAsync(Upload($"Account Key,Risk Score Override,Override Reason\n{accountKey},640,Agreed with the owner\n"));
            Assert.Equal(1, result["updated"]!.GetValue<int>());
            Assert.Equal(640, await OverrideOfAsync(accountKey));

            result = await ImportAsync(Upload($"Account Key,Risk Score Override,Override Reason\n{accountKey},700,\n"));
            Assert.Contains(Problems(result), p => p.Reason.Contains("needs a reason"));
            Assert.Equal(640, await OverrideOfAsync(accountKey));

            await ImportAsync(Upload($"Account Key,Risk Score Override,Override Reason\n{accountKey},,\n", clearBlanks: true));
            Assert.Null(await OverrideOfAsync(accountKey));
        }
        finally
        {
            await DeleteAccountAsync(accountKey);
        }
    }

    // D-323: an Excel workbook reads the same as a CSV; a file that isn't one is refused, not a 500.
    [Fact]
    public async Task Import_ReadsAnExcelWorkbook_AndRefusesAFileThatIsNotOne()
    {
        var accountKey = await CreateAccountAsync();
        try
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("Accounts");
            sheet.Cell(1, 1).Value = "Account Key";
            sheet.Cell(1, 2).Value = "Owner";
            sheet.Cell(1, 3).Value = "Target Remediation Date";
            sheet.Cell(2, 1).Value = accountKey;
            sheet.Cell(2, 2).Value = "Excel Owner";
            sheet.Cell(2, 3).Value = new DateTime(2027, 3, 31);
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            var result = await ImportAsync(UploadBytes(stream.ToArray(), "accounts.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
            Assert.Equal(1, result["updated"]!.GetValue<int>());
            Assert.Equal("Excel Owner", await OwnerOfAsync(accountKey));
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            Assert.Equal(new DateTime(2027, 3, 31), await connection.QuerySingleAsync<DateTime>(
                "SELECT TargetRemediationDate FROM dbo.fact_account_progress WHERE AccountKey = @accountKey", new { accountKey }));

            var refused = await Client("TestUser.Admin").PostAsync("/api/account-progress/import",
                UploadBytes(Encoding.UTF8.GetBytes("not a workbook"), "accounts.xlsx", "application/octet-stream"));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        finally
        {
            await DeleteAccountAsync(accountKey);
        }
    }

    [Fact]
    public async Task Import_WithoutTheKeyColumn_IsRefused()
    {
        var response = await Client("TestUser.Admin").PostAsync("/api/account-progress/import", Upload("Username,Owner\nx,y\n"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Account Key", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewer_CanExport_ButNotImport()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var response = await Client("TestUser.Viewer").PostAsync("/api/account-progress/import", Upload($"Account Key,Owner\n{accountKey},x\n"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
