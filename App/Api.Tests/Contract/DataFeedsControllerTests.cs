using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-181: the Data Sources page's API (DataFeedsController), gated by
/// ManageDataSources, and the data-feed settings on Global Application
/// Configuration. Feeds created here are deleted by the test that created
/// them; the runs use files that change no data (no file, or a safe name
/// that doesn't exist).
/// </summary>
public class DataFeedsControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public DataFeedsControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record KeyResponse(int DataFeedKey);
    private sealed record RunResponse(long DataFeedRunKey, string TriggerType, string? TriggeredByName, string Outcome, string? FileName, int? TotalRows, int? ErrorRows, string? ErrorMessage);
    private sealed record StatusResponse(bool IsRunning, bool WithinBusinessHours, DateTime NextScheduledRun);
    private sealed record TestResponse(bool FolderFound, string? MatchedFile, int MatchingFileCount, List<string> Columns, List<string> MissingColumns, string Message);

    private static object Feed(string name, string folder, string pattern, string feedType = "SafeAssignments", int? profileKey = null) => new
    {
        displayName = name, feedType, folderPath = folder, fileNamePattern = pattern, importMappingProfileKey = profileKey, isEnabled = false, displayOrder = 900
    };

    [Fact]
    public async Task Endpoints_AsViewer_AreForbidden()
    {
        var client = Client("TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/data-feeds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/data-feeds/run", new { })).StatusCode);
    }

    [Fact]
    public async Task Create_RejectsInvalidValues()
    {
        var client = Client("TestUser.Admin");
        var response = await client.PostAsJsonAsync("/api/admin/data-feeds", new
        {
            displayName = "x", feedType = "NotAType", folderPath = "relative\\path", fileNamePattern = "sub\\file.csv", isEnabled = true, displayOrder = 0
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>();
        Assert.Contains("Feed type", detail);
        Assert.Contains("full path", detail);
        Assert.Contains("file name only", detail);
    }

    [Fact]
    public async Task Feed_CrudTestAndRunNow_RecordsRuns()
    {
        var client = Client("TestUser.Admin");
        var folder = Directory.CreateTempSubdirectory("BlueTrackFeedContract_").FullName;
        var name = $"ContractTestFeed_{Guid.NewGuid():N}";
        var createResponse = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed(name, folder, "safes_{yyyy-MM-dd}.csv"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var key = (await createResponse.Content.ReadFromJsonAsync<KeyResponse>())!.DataFeedKey;

        try
        {
            // Duplicate name.
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/data-feeds", Feed(name, folder, "*.csv"))).StatusCode);

            // Test with no file yet, then with one.
            var noFile = await (await client.PostAsJsonAsync("/api/admin/data-feeds/test", new { folderPath = folder, fileNamePattern = "safes_{yyyy-MM-dd}.csv" }))
                .Content.ReadFromJsonAsync<TestResponse>();
            Assert.True(noFile!.FolderFound);
            Assert.Null(noFile.MatchedFile);

            // A run with no file is recorded as Failed.
            await RunAndWaitAsync(client, key);
            var failed = Assert.Single((await client.GetFromJsonAsync<List<RunResponse>>($"/api/admin/data-feeds/{key}/runs"))!);
            Assert.Equal("Failed", failed.Outcome);
            Assert.Equal("RunNow", failed.TriggerType);
            Assert.Contains("No file matching", failed.ErrorMessage);

            var fileName = $"safes_{DateTime.Now:yyyy-MM-dd}.csv";
            await File.WriteAllTextAsync(Path.Combine(folder, fileName), "SafeName,Application\r\nNoSuchSafe_ContractTest,NoSuchApp_ContractTest\r\n");
            var found = await (await client.PostAsJsonAsync("/api/admin/data-feeds/test", new { folderPath = folder, fileNamePattern = "safes_{yyyy-MM-dd}.csv" }))
                .Content.ReadFromJsonAsync<TestResponse>();
            Assert.Equal(fileName, found!.MatchedFile);
            Assert.Equal(["SafeName", "Application"], found.Columns);

            // With the import named, the header is checked against its required columns.
            var checkedOk = await (await client.PostAsJsonAsync("/api/admin/data-feeds/test", new { folderPath = folder, fileNamePattern = "safes_{yyyy-MM-dd}.csv", feedType = "SafeAssignments" }))
                .Content.ReadFromJsonAsync<TestResponse>();
            Assert.Empty(checkedOk!.MissingColumns);
            var checkedBad = await (await client.PostAsJsonAsync("/api/admin/data-feeds/test", new { folderPath = folder, fileNamePattern = "safes_{yyyy-MM-dd}.csv", feedType = "Applications" }))
                .Content.ReadFromJsonAsync<TestResponse>();
            Assert.Equal(["ApplicationCode"], checkedBad!.MissingColumns);
            Assert.Contains("Missing required columns: ApplicationCode", checkedBad.Message);

            // A run over that file imports it, with a row error and no data change; the file is left in place.
            await RunAndWaitAsync(client, key);
            var runs = (await client.GetFromJsonAsync<List<RunResponse>>($"/api/admin/data-feeds/{key}/runs"))!;
            Assert.Equal(2, runs.Count);
            var imported = runs[0];
            Assert.Equal("CompletedWithErrors", imported.Outcome);
            Assert.Equal(1, imported.TotalRows);
            Assert.Equal(1, imported.ErrorRows);
            Assert.EndsWith(fileName, imported.FileName);
            Assert.NotNull(imported.TriggeredByName);
            Assert.True(File.Exists(Path.Combine(folder, fileName)));
            var detail = await client.GetFromJsonAsync<JsonObject>($"/api/admin/data-feeds/runs/{imported.DataFeedRunKey}");
            Assert.Contains("errors", detail!["resultJson"]!.GetValue<string>());

            // Update, and the list shows the last run.
            var updateResponse = await client.PutAsJsonAsync($"/api/admin/data-feeds/{key}", Feed(name, folder, "*.csv", "Applications"));
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);
            var list = await client.GetFromJsonAsync<JsonArray>("/api/admin/data-feeds");
            var row = list!.Single(f => f!["dataFeedKey"]!.GetValue<int>() == key)!;
            Assert.Equal("Applications", row["feedType"]!.GetValue<string>());
            Assert.Equal(imported.DataFeedRunKey, row["lastRun"]!["dataFeedRunKey"]!.GetValue<long>());
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/admin/data-feeds/{key}")).StatusCode);
            Directory.Delete(folder, recursive: true);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/data-feeds/{key}")).StatusCode);
    }

    private static async Task RunAndWaitAsync(HttpClient client, int key)
    {
        var response = await client.PostAsJsonAsync("/api/admin/data-feeds/run", new { dataFeedKeys = new[] { key } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        for (var i = 0; i < 100; i++)
        {
            var status = await client.GetFromJsonAsync<StatusResponse>("/api/admin/data-feeds/status");
            if (!status!.IsRunning) return;
            await Task.Delay(200);
        }
        Assert.Fail("The data feed run didn't finish within 20 seconds.");
    }

    [Fact]
    public async Task GlobalConfig_RejectsInvalidScheduleSettings_AndSavesValidOnes()
    {
        var client = Client("TestUser.Admin");
        var original = (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        Assert.Matches(@"^\d\d:\d\d$", original["dataFeedRunTime"]!.GetValue<string>());

        var bad = original.DeepClone().AsObject();
        bad["dataFeedRunTime"] = "4am";
        bad["businessDays"] = "Someday";
        var badResponse = await client.PutAsJsonAsync("/api/admin/configuration", bad);
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        var changed = original.DeepClone().AsObject();
        changed["dataFeedRunRetentionDays"] = 30;
        changed["businessDays"] = "Mon,Wed";
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/configuration", changed)).StatusCode);
            var after = (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
            Assert.Equal(30, after["dataFeedRunRetentionDays"]!.GetValue<int>());
            Assert.Equal("Mon,Wed", after["businessDays"]!.GetValue<string>());
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/configuration", original)).StatusCode);
        }
    }
}
