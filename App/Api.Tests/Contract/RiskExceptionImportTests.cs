using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-183: the Risk Exceptions CSV import (exceptions approved in another
/// tool). Every exception and application a test creates is deleted by
/// that test, and any account it links is put back as it was.
/// </summary>
public class RiskExceptionImportTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private const string Header = "SourceTool,SourceExceptionId,SourceUrl,AccountUserName,AccountAddress,ApplicationCode,Justification,ApprovedByName,ApprovalDate,ReviewDate,Status,ExternalTicketReference,LinkToAccountProgress";

    private readonly BlueTrackWebApplicationFactory _factory;

    public RiskExceptionImportTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static MultipartFormDataContent Upload(params string[] rows)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Header + "\r\n" + string.Join("\r\n", rows) + "\r\n"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "exceptions.csv");
        return content;
    }

    private sealed record RowError(int RowNumber, string Error);
    private sealed record CreatedId(int RowNumber, string ExceptionId, string SourceExceptionId);
    private sealed record ImportResult(int TotalRows, int CreatedCount, int LinkedCount, List<CreatedId> Created, List<RowError> Errors);

    private static async Task CleanUpAsync(string sourceTool, string? applicationCode = null)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM web.risk_exception WHERE SourceTool = @sourceTool", new { sourceTool });
        if (applicationCode is not null)
            await connection.ExecuteAsync("DELETE FROM web.dim_application WHERE ApplicationCode = @applicationCode", new { applicationCode });
    }

    [Fact]
    public async Task Import_CreatesExceptionsWithNewIds_KeepsTheSource_AndReportsRowErrors()
    {
        var tool = $"CT-{Guid.NewGuid():N}"[..14];
        var appCode = $"CTREX{Guid.NewGuid():N}"[..16];
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync("INSERT INTO web.dim_application (ApplicationCode, ApplicationName) VALUES (@appCode, @appCode)", new { appCode });
        }
        var client = Client("TestUser.Approver");

        try
        {
            var response = await client.PostAsync("/api/risk-exceptions/import", Upload(
                $"{tool},A-1,https://grc.example.com/A-1,TestAccount04,,,Legacy app,Pat Approver,2026-01-15,2027-01-15,,TKT-1,No",
                $"{tool},A-2,,,,{appCode},App-wide,Pat Approver,2026-02-01,2027-02-01,Expired,,",
                $"{tool},A-1,,TestAccount04,,,Repeat in file,Pat Approver,2026-01-15,2027-01-15,,,",
                $"{tool},A-3,,TestAccount04,,{appCode},Both scopes,Pat Approver,2026-01-15,2027-01-15,,,",
                $"{tool},A-4,,TestAccount04,,,Bad date,Pat Approver,15/01/2026,2027-01-15,,,",
                $"{tool},A-5,javascript:alert(1),TestAccount04,,,Bad link,Pat Approver,2026-01-15,2027-01-15,,,",
                $"{tool},A-6,,NoSuchUser,,,Unknown account,Pat Approver,2026-01-15,2027-01-15,,,",
                $"{tool},A-7,,,,{appCode},Link an app,Pat Approver,2026-01-15,2027-01-15,,,Yes"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<ImportResult>())!;

            Assert.Equal(8, result.TotalRows);
            Assert.Equal(2, result.CreatedCount);
            Assert.Equal(0, result.LinkedCount);
            Assert.Equal([4, 5, 6, 7, 8, 9], result.Errors.Select(e => e.RowNumber));
            Assert.Contains("already imported", result.Errors[0].Error);
            Assert.Contains("exactly one scope", result.Errors[1].Error);
            Assert.Contains("yyyy-MM-dd", result.Errors[2].Error);
            Assert.Contains("http", result.Errors[3].Error);
            Assert.Contains("No account with username 'NoSuchUser'", result.Errors[4].Error);
            Assert.Contains("account exceptions only", result.Errors[5].Error);

            // Each gets a BlueTrack ID, not the source's.
            var first = result.Created.Single(c => c.SourceExceptionId == "A-1");
            Assert.NotEqual("A-1", first.ExceptionId);

            var list = await client.GetFromJsonAsync<JsonArray>("/api/risk-exceptions?pageSize=500");
            var row = list!.Single(e => e!["exceptionID"]!.GetValue<string>() == first.ExceptionId)!;
            Assert.Equal("Pat Approver", row["approvedByName"]!.GetValue<string>());
            Assert.Equal(tool, row["sourceTool"]!.GetValue<string>());
            Assert.Equal("https://grc.example.com/A-1", row["sourceUrl"]!.GetValue<string>());
            Assert.Equal("Active", row["statusName"]!.GetValue<string>());
            var second = list.Single(e => e!["exceptionID"]!.GetValue<string>() == result.Created.Single(c => c.SourceExceptionId == "A-2").ExceptionId)!;
            Assert.Equal("Expired", second["statusName"]!.GetValue<string>());

            var detail = await client.GetFromJsonAsync<JsonObject>($"/api/risk-exceptions/{row["exceptionKey"]!.GetValue<int>()}");
            Assert.Null(detail!["approvedBy"]);
            Assert.Equal("Pat Approver", detail["approvedByName"]!.GetValue<string>());
            Assert.NotNull(detail["importedByName"]);

            // Importing the same source exception again is an error, naming the existing ID.
            var again = (await (await client.PostAsync("/api/risk-exceptions/import", Upload(
                $"{tool},A-1,,TestAccount04,,,Again,Pat Approver,2026-01-15,2027-01-15,,,"))).Content.ReadFromJsonAsync<ImportResult>())!;
            Assert.Equal(0, again.CreatedCount);
            Assert.Contains($"already imported, as {first.ExceptionId}", Assert.Single(again.Errors).Error);
        }
        finally
        {
            await CleanUpAsync(tool, appCode);
        }
    }

    [Fact]
    public async Task Import_LinkToAccountProgress_SetsRiskAccepted_WithTheNewException()
    {
        var tool = $"CT-{Guid.NewGuid():N}"[..14];
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount04");
        await new AccountProgressLockRepository(new TestDbConnectionFactory()).ForceReleaseAsync(accountKey);
        var progress = new AccountProgressRepository(new TestDbConnectionFactory());
        var before = (await progress.GetDetailAsync(accountKey))!;

        try
        {
            var result = (await (await Client("TestUser.Approver").PostAsync("/api/risk-exceptions/import", Upload(
                $"{tool},L-1,,TestAccount04,,,Accepted risk,Pat Approver,2026-01-15,2027-01-15,Active,,Yes"))).Content.ReadFromJsonAsync<ImportResult>())!;

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.LinkedCount);
            var after = (await progress.GetDetailAsync(accountKey))!;
            Assert.Equal(await progress.GetStatusKeyAsync("Risk Accepted / Excluded"), after.CurrentStatusKey);
            Assert.NotNull(after.ExceptionKey);
            Assert.Equal(before.OwnerName, after.OwnerName);
        }
        finally
        {
            await progress.UpdateAsync(accountKey, new SaveAccountProgressRequest
            {
                CurrentStageKey = before.CurrentStageKey, CurrentStatusKey = before.CurrentStatusKey, RiskLevelKey = before.RiskLevelKey,
                AccountTypeKey = before.AccountTypeKey, SORKey = before.SORKey, OwnerName = before.OwnerName, BusinessUnit = before.BusinessUnit,
                TargetRemediationDate = before.TargetRemediationDate, ActualCompletionDate = before.ActualCompletionDate, Notes = before.Notes
            }, before.ExceptionKey);
            await CleanUpAsync(tool);
        }
    }

    [Fact]
    public async Task Import_WithoutApproveExceptions_IsForbidden()
    {
        var client = Client("TestUser.Analyst");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/risk-exceptions/import", Upload())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/risk-exceptions/import/template")).StatusCode);
    }

    [Fact]
    public async Task Template_HasEveryColumn()
    {
        var text = await Client("TestUser.Approver").GetStringAsync("/api/risk-exceptions/import/template");
        Assert.Equal(Header, text.TrimEnd());
    }
}
