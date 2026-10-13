using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-212 (Data Sources phase 4a): source systems for account inventory feeds,
/// the feed's validation, and real runs of its import. Each test uses its own
/// new source system, and removes it with its accounts and feed afterwards.
/// </summary>
public class AccountInventoryFeedTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountInventoryFeedTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private sealed record SourceResponse(int SourceSystemKey, string SourceSystemCode, string SourceSystemName);
    private sealed record KeyResponse(int DataFeedKey);
    private sealed record RunResponse(long DataFeedRunKey, string Outcome, int? TotalRows, int? ErrorRows, string? Summary, string? ErrorMessage);
    private sealed record StatusResponse(bool IsRunning);
    private sealed record AccountRow(string SourceAccountId, string AccountName, string? Address, string? UserName, bool IsDeleted, bool IsDeletedInSource, string? StageName, string? OwnerName);

    private static object Feed(string name, string folder, int? sourceSystemKey, string feedType = "AccountInventory") => new
    {
        displayName = name, feedType, folderPath = folder, fileNamePattern = "accounts.csv", isEnabled = false, displayOrder = 960, sourceSystemKey
    };

    private static async Task<SourceResponse> CreateSourceAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var response = await client.PostAsJsonAsync("/api/admin/data-feeds/source-systems",
            new { sourceSystemCode = $"ct_{suffix}", sourceSystemName = $"Contract test source {suffix}" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SourceResponse>())!;
    }

    private static async Task<List<AccountRow>> AccountsAsync(int sourceSystemKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return (await connection.QueryAsync<AccountRow>("""
            SELECT fa.SourceAccountId, fa.AccountName, fa.Address, fa.UserName, fa.IsDeleted, fa.IsDeletedInSource, s.StageName, p.OwnerName
            FROM dbo.fact_account fa
            LEFT JOIN dbo.fact_account_progress p ON p.AccountKey = fa.AccountKey
            LEFT JOIN dbo.dim_blueprint_stage s ON s.StageKey = p.CurrentStageKey
            WHERE fa.SourceSystemKey = @sourceSystemKey
            ORDER BY fa.SourceAccountId
            """, new { sourceSystemKey })).ToList();
    }

    private static async Task RemoveSourceAsync(int sourceSystemKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM web.data_feed WHERE SourceSystemKey = @sourceSystemKey;
            DELETE p FROM dbo.fact_account_progress p JOIN dbo.fact_account fa ON fa.AccountKey = p.AccountKey WHERE fa.SourceSystemKey = @sourceSystemKey;
            DELETE FROM dbo.fact_account WHERE SourceSystemKey = @sourceSystemKey;
            DELETE FROM dbo.dim_source_system WHERE SourceSystemKey = @sourceSystemKey;
            """, new { sourceSystemKey });
    }

    private static async Task<RunResponse> RunAndWaitAsync(HttpClient client, int key)
    {
        var response = await client.PostAsJsonAsync("/api/admin/data-feeds/run", new { dataFeedKeys = new[] { key } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        for (var i = 0; i < 100; i++)
        {
            var status = await client.GetFromJsonAsync<StatusResponse>("/api/admin/data-feeds/status");
            if (!status!.IsRunning) return (await client.GetFromJsonAsync<List<RunResponse>>($"/api/admin/data-feeds/{key}/runs"))![0];
            await Task.Delay(200);
        }
        Assert.Fail("The data feed run didn't finish within 20 seconds.");
        return null!;
    }

    [Fact]
    public async Task SourceSystems_ValidateAndListOnlyFeedSources()
    {
        var client = AdminClient();

        var bad = await client.PostAsJsonAsync("/api/admin/data-feeds/source-systems", new { sourceSystemCode = "has space", sourceSystemName = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var reserved = await client.PostAsJsonAsync("/api/admin/data-feeds/source-systems", new { sourceSystemCode = "privcloud", sourceSystemName = "Anything" });
        Assert.Equal(HttpStatusCode.Conflict, reserved.StatusCode);

        var source = await CreateSourceAsync(client);
        try
        {
            var options = (await client.GetFromJsonAsync<JsonObject>("/api/admin/data-feeds/options"))!;
            var codes = options["sourceSystems"]!.AsArray().Select(s => s!["sourceSystemCode"]!.GetValue<string>()).ToList();
            Assert.Contains(source.SourceSystemCode, codes);
            Assert.DoesNotContain("PRIVCLOUD", codes);
            Assert.DoesNotContain("DISCOVERY", codes);
            Assert.True(options["feedTypes"]!.AsArray().Single(t => t!["value"]!.GetValue<string>() == "AccountInventory")!["usesSourceSystem"]!.GetValue<bool>());
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
        }
    }

    [Fact]
    public async Task Feed_RequiresItsOwnSourceSystem()
    {
        var client = AdminClient();
        var source = await CreateSourceAsync(client);
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        try
        {
            var noSource = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, null));
            Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);
            Assert.Contains("Source system is required", (await noSource.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());

            var wrongType = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey, "SafeAssignments"));
            Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

            var first = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var second = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
            Assert.Contains("already imports accounts for this source system", (await second.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Runs_CreateUpdateDeleteAndRestoreAccounts_AndAnEmptyFileChangesNothing()
    {
        var client = AdminClient();
        var source = await CreateSourceAsync(client);
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        var file = Path.Combine(folder, "accounts.csv");
        try
        {
            var created = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            var key = (await created.Content.ReadFromJsonAsync<KeyResponse>())!.DataFeedKey;

            // First run: two accounts, a row missing its Address, a duplicate, and an unknown platform (imported, reported).
            await File.WriteAllTextAsync(file,
                "UserName,Address,AccountName,PlatformName,OwnerName\r\n" +
                "Root,Host1.Example.com,,,Alice\r\n" +
                "svc_app,host2.example.com,App service,NoSuchPlatform_CT,\r\n" +
                "nobody,,,,\r\n" +
                "root,host1.example.com,,,\r\n");
            var run1 = await RunAndWaitAsync(client, key);
            Assert.Equal("CompletedWithErrors", run1.Outcome);
            Assert.Equal(3, run1.ErrorRows);
            Assert.StartsWith("2 created, 0 updated, 0 restored, 0 marked deleted", run1.Summary);

            var accounts = await AccountsAsync(source.SourceSystemKey);
            Assert.Equal(["root@host1.example.com", "svc_app@host2.example.com"], accounts.Select(a => a.SourceAccountId));
            var root = accounts[0];
            Assert.Equal("Root@Host1.Example.com", root.AccountName);   // default name keeps the file's case
            Assert.Equal("Discovered", root.StageName);
            Assert.Equal("Alice", root.OwnerName);

            // A feed account has no safe; D-212's auto-advance leaves it at Discovered (rolled back: other accounts stay as they are).
            await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await connection.ExecuteAsync("EXEC dbo.usp_Load_AccountProgressAutoAdvance", transaction: transaction);
                var stage = await connection.QuerySingleAsync<string>("""
                    SELECT s.StageName FROM dbo.fact_account fa
                    JOIN dbo.fact_account_progress p ON p.AccountKey = fa.AccountKey
                    JOIN dbo.dim_blueprint_stage s ON s.StageKey = p.CurrentStageKey
                    WHERE fa.SourceSystemKey = @SourceSystemKey AND fa.SourceAccountId = 'root@host1.example.com'
                    """, new { source.SourceSystemKey }, transaction);
                Assert.Equal("Discovered", stage);
                await transaction.RollbackAsync();
            }

            // Second run: root is gone (marked deleted), svc_app renamed; owner in the file doesn't overwrite progress.
            await File.WriteAllTextAsync(file, "UserName,Address,AccountName\r\nsvc_app,host2.example.com,App service (renamed)\r\n");
            var run2 = await RunAndWaitAsync(client, key);
            Assert.Equal("Succeeded", run2.Outcome);
            Assert.StartsWith("0 created, 1 updated, 0 restored, 1 marked deleted", run2.Summary);
            accounts = await AccountsAsync(source.SourceSystemKey);
            Assert.True(accounts[0].IsDeleted && accounts[0].IsDeletedInSource);
            Assert.Equal("App service (renamed)", accounts[1].AccountName);

            // Third run: root is back, restored with its old progress row.
            await File.WriteAllTextAsync(file, "UserName,Address,AccountName\r\nroot,host1.example.com,\r\nsvc_app,host2.example.com,App service (renamed)\r\n");
            var run3 = await RunAndWaitAsync(client, key);
            Assert.StartsWith("0 created, 0 updated, 1 restored, 0 marked deleted, 1 unchanged", run3.Summary);
            accounts = await AccountsAsync(source.SourceSystemKey);
            Assert.False(accounts[0].IsDeleted);
            Assert.Equal("Alice", accounts[0].OwnerName);

            // A file with no valid rows fails the run and changes nothing.
            await File.WriteAllTextAsync(file, "UserName,Address\r\n");
            var run4 = await RunAndWaitAsync(client, key);
            Assert.Equal("Failed", run4.Outcome);
            Assert.Contains("no valid account rows", run4.ErrorMessage);
            Assert.All(await AccountsAsync(source.SourceSystemKey), a => Assert.False(a.IsDeleted));
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
            Directory.Delete(folder, recursive: true);
        }
    }
}
