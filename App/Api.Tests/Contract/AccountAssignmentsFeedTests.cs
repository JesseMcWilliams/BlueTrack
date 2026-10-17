using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-241: the Account assignments feed, the scheduled form of the D-239 CSV
/// import: it's offered as a feed type, its Test checks the columns, and a
/// real run assigns the matching accounts and reports rows it can't match.
/// Each test adds its own account and application and removes them afterwards.
/// </summary>
public class AccountAssignmentsFeedTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountAssignmentsFeedTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private sealed record KeyResponse(int DataFeedKey);
    private sealed record RunResponse(long DataFeedRunKey, string Outcome, int? TotalRows, int? ErrorRows, string? Summary, string? ErrorMessage);
    private sealed record StatusResponse(bool IsRunning);

    private static object Feed(string name, string folder) => new
    {
        displayName = name, feedType = "AccountAssignments", folderPath = folder, fileNamePattern = "assignments.csv", isEnabled = false, displayOrder = 961
    };

    [Fact]
    public async Task FeedType_IsOffered_WithoutMappingProfileOrSourceSystem()
    {
        var options = (await AdminClient().GetFromJsonAsync<JsonObject>("/api/admin/data-feeds/options"))!;
        var type = options["feedTypes"]!.AsArray().Single(t => t!["value"]!.GetValue<string>() == "AccountAssignments")!;
        Assert.False(type["usesMappingProfile"]!.GetValue<bool>());
        Assert.False(type["usesSourceSystem"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Run_AssignsTheMatchingAccount_AndReportsRowErrors()
    {
        var client = AdminClient();
        var id = Guid.NewGuid().ToString("N")[..8];
        var userName = $"ct_user_{id}";
        var address = $"ct-host-{id}.example.com";
        var code = $"CTF{id}";
        var folder = Directory.CreateTempSubdirectory("BlueTrackAssignFeed_").FullName;
        long accountKey = 0;
        int? feedKey = null;

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        try
        {
            accountKey = await connection.QuerySingleAsync<long>(
                "INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, Address, UserName) OUTPUT inserted.AccountKey VALUES (1, @Id, @Name, @Address, @User)",
                new { Id = $"ct-{id}", Name = $"CT feed account {id}", Address = address, User = userName });
            await connection.ExecuteAsync("""
                INSERT INTO dbo.fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
                SELECT @accountKey, (SELECT TOP 1 StageKey FROM dbo.dim_blueprint_stage ORDER BY StageOrder),
                                    (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'Not Started')
                """, new { accountKey });
            await connection.ExecuteAsync("INSERT INTO web.dim_application (ApplicationCode, ApplicationName) VALUES (@code, @name)", new { code, name = $"CT Feed App {id}" });
            var applicationKey = await connection.QuerySingleAsync<int>("SELECT ApplicationKey FROM web.dim_application WHERE ApplicationCode = @code", new { code });

            await File.WriteAllTextAsync(Path.Combine(folder, "assignments.csv"),
                "UserName,Address,Application\n" +
                $"{userName},{address},{code}\n" +
                $"nobody_{id},{address},{code}\n");

            var test = await client.PostAsJsonAsync("/api/admin/data-feeds/test", new { feedType = "AccountAssignments", folderPath = folder, fileNamePattern = "assignments.csv" });
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            Assert.Empty((await test.Content.ReadFromJsonAsync<JsonObject>())!["missingColumns"]!.AsArray());

            var create = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            feedKey = (await create.Content.ReadFromJsonAsync<KeyResponse>())!.DataFeedKey;

            var run = await RunAndWaitAsync(client, feedKey.Value);
            Assert.Equal((2, 1), (run.TotalRows, run.ErrorRows));
            Assert.Equal("1 assigned, 0 changed, 0 unchanged, 1 errors", run.Summary);
            Assert.Equal(applicationKey, await connection.QuerySingleAsync<int?>("SELECT ApplicationKey FROM dbo.fact_account_progress WHERE AccountKey = @accountKey", new { accountKey }));
        }
        finally
        {
            if (feedKey is not null)
            {
                await connection.ExecuteAsync("DELETE FROM web.data_feed_run WHERE DataFeedKey = @feedKey; DELETE FROM web.data_feed WHERE DataFeedKey = @feedKey;", new { feedKey });
            }
            if (accountKey != 0)
            {
                await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @accountKey; DELETE FROM dbo.fact_account WHERE AccountKey = @accountKey;", new { accountKey });
            }
            await connection.ExecuteAsync("DELETE FROM web.dim_application WHERE ApplicationCode = @code", new { code });
            Directory.Delete(folder, recursive: true);
        }
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
}
