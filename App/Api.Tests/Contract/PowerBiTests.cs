using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-314: Admin > Power BI: where to connect, the reporting objects with their columns, and how they relate.</summary>
public class PowerBiTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task Admin_GetsTheDatabase_TheObjectsWithTheirColumns_AndTheRelationships()
    {
        var body = await Client("TestUser.Admin").GetFromJsonAsync<JsonObject>("/api/admin/power-bi");

        Assert.False(string.IsNullOrWhiteSpace(body!["serverName"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(body["databaseName"]!.GetValue<string>()));
        var accounts = body["objects"]!.AsArray().Single(o => o!["name"]!.GetValue<string>() == "vw_powerbi_accounts")!;
        var columns = accounts["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Contains("AccountKey", columns);
        Assert.Contains("SafeKey", columns);
        Assert.Contains(body["relationships"]!.AsArray(), r =>
            r!["fromObject"]!.GetValue<string>() == "vw_powerbi_accounts" && r["fromColumn"]!.GetValue<string>() == "SafeKey"
            && r["toObject"]!.GetValue<string>() == "dim_safe" && r["toColumn"]!.GetValue<string>() == "SafeKey");
    }

    // Every relationship names columns that exist on both sides, so the page never tells someone to relate a column that isn't there.
    [Fact]
    public async Task EveryRelationship_NamesColumnsTheObjectsHave()
    {
        var body = await Client("TestUser.Admin").GetFromJsonAsync<JsonObject>("/api/admin/power-bi");
        var columnsOf = body!["objects"]!.AsArray().ToDictionary(
            o => o!["name"]!.GetValue<string>(), o => o!["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToHashSet());

        foreach (var r in body["relationships"]!.AsArray())
        {
            Assert.Contains(r!["fromColumn"]!.GetValue<string>(), columnsOf[r["fromObject"]!.GetValue<string>()]);
            Assert.Contains(r["toColumn"]!.GetValue<string>(), columnsOf[r["toObject"]!.GetValue<string>()]);
        }
    }

    [Fact]
    public async Task Viewer_IsForbidden()
    {
        var response = await Client("TestUser.Viewer").GetAsync("/api/admin/power-bi");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    // D-321: the reader role grants exactly what the page lists -- no more (it stays read-only to these), no fewer.
    [Fact]
    public void TheReaderRoleScript_GrantsExactlyThePagesObjects()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        var script = File.ReadAllText(Path.Combine(dir!.FullName, "Database", "Manual", "09_BlueTrack_PowerBiReaderRole.sql"));
        var granted = System.Text.RegularExpressions.Regex.Matches(script, @"GRANT SELECT ON \[(\w+)\]\.\[(\w+)\] TO BlueTrack_PowerBI_Reader")
            .Select(m => $"{m.Groups[1].Value}.{m.Groups[2].Value}").Order().ToList();

        Assert.Equal(BlueTrack.Api.Data.PowerBiRepository.ObjectNames.Order().ToList(), granted);
        Assert.DoesNotContain("GRANT INSERT", script);
        Assert.DoesNotContain("GRANT EXECUTE", script);
    }

    [Fact]
    public async Task SaysWhetherTheReaderRoleIsSetUp()
    {
        var body = await Client("TestUser.Admin").GetFromJsonAsync<JsonObject>("/api/admin/power-bi");
        Assert.NotNull(body!["readerRoleExists"]?.GetValue<bool>());
    }
}
