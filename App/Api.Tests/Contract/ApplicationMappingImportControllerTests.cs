using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using BlueTrack.Api.Tests.Integration;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-180: the Application ↔ Safe Mapping page's two CSV imports, through the
/// real HTTP upload path. Each test uses its own uniquely-named applications
/// and safes and removes them afterwards (there's no delete endpoint for
/// applications, so cleanup is direct SQL, as in the account tests).
/// </summary>
public class ApplicationMappingImportControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ApplicationMappingImportControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private static MultipartFormDataContent BuildCsvUpload(string csv)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "file", "import.csv");
        return content;
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task ApplicationsImport_CreatesThenUpdatesThenLeavesUnchanged_AndReportsRowErrors()
    {
        var client = AdminClient();
        var id = Guid.NewGuid().ToString("N")[..8];
        var code = $"CT{id}";
        var name = $"Contract Test App {id}";
        var otherCode = $"CT{id}B";

        try
        {
            // Create. Row 3 reuses row 2's name for a different code -> row error.
            var create = await client.PostAsync("/api/admin/application-mapping/import/applications", BuildCsvUpload(
                $"ApplicationCode,ApplicationName,OwnerName\n{code},{name},Jane Owner\n{otherCode},{name},\n,No Code App,\n"));
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            var created = (await create.Content.ReadFromJsonAsync<ApplicationImportResponse>())!;
            Assert.Equal((3, 1, 0, 0), (created.TotalRows, created.CreatedCount, created.UpdatedCount, created.UnchangedCount));
            Assert.Contains(created.Errors, e => e.RowNumber == 3 && e.Error.Contains("already used"));
            Assert.Contains(created.Errors, e => e.RowNumber == 4 && e.Error.Contains("ApplicationCode is required"));

            // Update: a blank name keeps the current name; a new technical contact is set.
            var update = await client.PostAsync("/api/admin/application-mapping/import/applications", BuildCsvUpload(
                $"ApplicationCode,ApplicationName,TechnicalName\n{code.ToLowerInvariant()},,Tech Person\n"));
            var updated = (await update.Content.ReadFromJsonAsync<ApplicationImportResponse>())!;
            Assert.Equal((1, 0), (updated.UpdatedCount, updated.Errors.Count));

            await using (var connection = await OpenAsync())
            {
                var row = await connection.QuerySingleAsync<(string ApplicationName, string? OwnerName, string? TechnicalName)>(
                    "SELECT ApplicationName, OwnerName, TechnicalName FROM web.dim_application WHERE ApplicationCode = @Code", new { Code = code });
                Assert.Equal((name, "Jane Owner", "Tech Person"), row);
            }

            // Same values again -> unchanged.
            var again = await client.PostAsync("/api/admin/application-mapping/import/applications", BuildCsvUpload(
                $"ApplicationCode,TechnicalName\n{code},Tech Person\n"));
            Assert.Equal(1, (await again.Content.ReadFromJsonAsync<ApplicationImportResponse>())!.UnchangedCount);
        }
        finally
        {
            await using var connection = await OpenAsync();
            await connection.ExecuteAsync("DELETE FROM web.dim_application WHERE ApplicationCode IN (@Code, @OtherCode)", new { Code = code, OtherCode = otherCode });
        }
    }

    [Fact]
    public async Task SafeAssignmentsImport_AssignsEveryMatchingSafe_SourceNarrows_OverwritesAndReportsErrors()
    {
        var client = AdminClient();
        var id = Guid.NewGuid().ToString("N")[..8];
        var safeName = $"CTSafe_{id}";
        var codeA = $"CTA{id}";
        var codeB = $"CTB{id}";
        int[] safeKeys = [];

        await using var connection = await OpenAsync();
        try
        {
            // The same safe name in Privilege Cloud (1) and Self-Hosted (2).
            safeKeys =
            [
                await connection.QuerySingleAsync<int>("INSERT INTO dbo.dim_safe (SourceSystemKey, SafeUrlId, SafeName) OUTPUT inserted.SafeKey VALUES (1, @Url, @Name)", new { Url = $"ct-{id}-pc", Name = safeName }),
                await connection.QuerySingleAsync<int>("INSERT INTO dbo.dim_safe (SourceSystemKey, SafeUrlId, SafeName) OUTPUT inserted.SafeKey VALUES (2, @Url, @Name)", new { Url = $"ct-{id}-sh", Name = safeName })
            ];
            await client.PostAsync("/api/admin/application-mapping/import/applications", BuildCsvUpload(
                $"ApplicationCode,ApplicationName\n{codeA},CT App A {id}\n{codeB},CT App B {id}\n"));
            var keyA = await connection.QuerySingleAsync<int>("SELECT ApplicationKey FROM web.dim_application WHERE ApplicationCode = @Code", new { Code = codeA });
            var keyB = await connection.QuerySingleAsync<int>("SELECT ApplicationKey FROM web.dim_application WHERE ApplicationCode = @Code", new { Code = codeB });

            // No Source: both safes get App A (by code). Error rows: unknown safe, unknown app, bad source.
            var first = await client.PostAsync("/api/admin/application-mapping/import/safe-assignments", BuildCsvUpload(
                $"SafeName,Application,Source\n{safeName},{codeA},\nNoSuchSafe_{id},{codeA},\n{safeName},No Such App,\n{safeName},{codeA},Mainframe\n"));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstResult = (await first.Content.ReadFromJsonAsync<SafeAssignmentImportResponse>())!;
            Assert.Equal((4, 2, 0, 0), (firstResult.TotalRows, firstResult.AssignedCount, firstResult.ChangedCount, firstResult.UnchangedCount));
            Assert.Equal(new[] { 3, 4, 5 }, firstResult.Errors.Select(e => e.RowNumber));

            // Source narrows to Self-Hosted; App B by NAME overwrites only that safe.
            var second = await client.PostAsync("/api/admin/application-mapping/import/safe-assignments", BuildCsvUpload(
                $"SafeName,Application,Source\n{safeName},CT App B {id},SelfHosted\n"));
            var secondResult = (await second.Content.ReadFromJsonAsync<SafeAssignmentImportResponse>())!;
            Assert.Equal((0, 1, 0), (secondResult.AssignedCount, secondResult.ChangedCount, secondResult.UnchangedCount));

            var assignments = (await connection.QueryAsync<(int SourceSystemKey, int? ApplicationKey)>(
                "SELECT SourceSystemKey, ApplicationKey FROM dbo.dim_safe WHERE SafeName = @Name ORDER BY SourceSystemKey", new { Name = safeName })).ToList();
            Assert.Equal(new (int, int?)[] { (1, keyA), (2, keyB) }, assignments);

            // Repeating the Self-Hosted row changes nothing.
            var third = await client.PostAsync("/api/admin/application-mapping/import/safe-assignments", BuildCsvUpload(
                $"SafeName,Application,Source\n{safeName},{codeB},SelfHosted\n"));
            Assert.Equal(1, (await third.Content.ReadFromJsonAsync<SafeAssignmentImportResponse>())!.UnchangedCount);
        }
        finally
        {
            if (safeKeys.Length > 0)
            {
                await connection.ExecuteAsync("DELETE FROM dbo.dim_safe WHERE SafeKey IN @Keys", new { Keys = safeKeys });
            }
            await connection.ExecuteAsync("DELETE FROM web.dim_application WHERE ApplicationCode IN (@A, @B)", new { A = codeA, B = codeB });
        }
    }

    [Fact]
    public async Task Templates_ListTheFixedColumns()
    {
        var client = AdminClient();

        var applications = await client.GetStringAsync("/api/admin/application-mapping/import/applications/template");
        var assignments = await client.GetStringAsync("/api/admin/application-mapping/import/safe-assignments/template");

        Assert.Equal("ApplicationCode,ApplicationName,Description,OwnerName,OwnerEmail,TechnicalName,TechnicalEmail,Notes", applications.Trim());
        Assert.Equal("SafeName,Application,Source", assignments.Trim());
    }

    [Fact]
    public async Task Imports_RequireCurateApplicationMapping()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");

        var response = await client.PostAsync("/api/admin/application-mapping/import/applications", BuildCsvUpload("ApplicationCode,ApplicationName\n"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record RowError(int RowNumber, string Error);
    private sealed record ApplicationImportResponse(int TotalRows, int CreatedCount, int UpdatedCount, int UnchangedCount, List<RowError> Errors);
    private sealed record SafeAssignmentImportResponse(int TotalRows, int AssignedCount, int ChangedCount, int UnchangedCount, List<RowError> Errors);
}
