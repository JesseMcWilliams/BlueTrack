using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-200: the scheduled-backup settings on Global Application Configuration, and the recent-backups list.</summary>
public class ScheduledBackupConfigTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ScheduledBackupConfigTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private static JsonObject With(JsonObject original, Action<JsonObject> change)
    {
        var copy = JsonNode.Parse(original.ToJsonString())!.AsObject();
        change(copy);
        return copy;
    }

    [Fact]
    public async Task ScheduledBackupSettings_ValidateSaveAndTurnOff()
    {
        var client = Admin();
        var original = (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;

        try
        {
            var badTime = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["scheduledBackupTime"] = "25:00"; }));
            Assert.Equal(HttpStatusCode.BadRequest, badTime.StatusCode);

            var badDays = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["scheduledBackupRetentionDays"] = 0; }));
            Assert.Equal(HttpStatusCode.BadRequest, badDays.StatusCode);

            var noFolder = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["scheduledBackupTime"] = "01:30"; c["backupFolder"] = null; }));
            Assert.Equal(HttpStatusCode.BadRequest, noFolder.StatusCode);

            var scheduled = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c =>
            {
                c["scheduledBackupTime"] = "01:30"; c["scheduledBackupRetentionDays"] = 21; c["backupFolder"] = @"C:\BlueTrackContractTestBackups";
            }));
            Assert.Equal(HttpStatusCode.NoContent, scheduled.StatusCode);
            var after = (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
            Assert.Equal("01:30", after["scheduledBackupTime"]!.GetValue<string>());
            Assert.Equal(21, after["scheduledBackupRetentionDays"]!.GetValue<int>());

            // "" turns it off; null would leave it as it is.
            var off = await client.PutAsJsonAsync("/api/admin/configuration", With(after, c => { c["scheduledBackupTime"] = ""; }));
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
            Assert.Null((await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!["scheduledBackupTime"]);
        }
        finally
        {
            var restore = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["scheduledBackupTime"] = original["scheduledBackupTime"]?.GetValue<string>() ?? ""; }));
            Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        }
    }

    [Fact]
    public async Task RecentBackups_AreListed()
    {
        var response = await Admin().GetAsync("/api/admin/deployment/backups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<JsonArray>());
    }
}
