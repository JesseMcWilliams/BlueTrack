using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-318: the Accounts export, and reading it back with changed values.</summary>
public class AccountProgressExportImportTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static MultipartFormDataContent Upload(string csv, string? reason = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "accounts.csv");
        if (reason is not null) content.Add(new StringContent(reason), "reason");
        return content;
    }

    private static async Task<string?> OwnerOfAsync(long accountKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<string?>("SELECT OwnerName FROM dbo.fact_account_progress WHERE AccountKey = @accountKey", new { accountKey });
    }

    [Fact]
    public async Task Export_HasTheImportsHeaders_AndTheFilteredAccounts()
    {
        var response = await Client("TestUser.Viewer").GetAsync("/api/account-progress/export?format=csv&search=TestAccount03");
        response.EnsureSuccessStatusCode();
        var lines = (await response.Content.ReadAsStringAsync()).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Account Key,Username,Address,Account Name,Safe,Stage,Status,Risk Level,Account Type,Source of Record,Application,Owner,Business Unit,Target Remediation Date,Actual Completion Date,Notes", lines[0]);
        Assert.Contains(lines.Skip(1), l => l.Contains("TestAccount03"));
    }

    // An account of its own: the four seeded test accounts are edited by tests that run alongside this one.
    private static async Task<long> CreateAccountAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @id, 'ContractTest Import Account', 0)",
            new { id = $"ContractTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync("""
            INSERT INTO dbo.fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
            SELECT @accountKey, (SELECT TOP (1) StageKey FROM dbo.dim_blueprint_stage ORDER BY StageOrder),
                   (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'In Progress')
            """, new { accountKey });
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
            var response = await Client("TestUser.Admin").PostAsync("/api/account-progress/import", Upload(csv));
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.Equal(4, result!["rows"]!.GetValue<int>());
            Assert.Equal(1, result["updated"]!.GetValue<int>());
            var problems = result["problems"]!.AsArray().Select(p => (p!["rowNumber"]!.GetValue<int>(), p["reason"]!.GetValue<string>())).ToList();
            Assert.Contains(problems, p => p.Item1 == 3 && p.Item2.Contains("isn't one of the choices"));
            Assert.Contains(problems, p => p.Item1 == 4 && p.Item2 == "No account has this key.");
            Assert.Contains(problems, p => p.Item1 == 5 && p.Item2.Contains("isn't a number"));
            Assert.Equal(owner, await OwnerOfAsync(accountKey));
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
