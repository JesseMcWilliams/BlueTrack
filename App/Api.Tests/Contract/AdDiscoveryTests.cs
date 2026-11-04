using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-317: AD Account Discovery on Data Sources: its run time, Run now, and the run log.</summary>
public class AdDiscoveryTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task RunTime_IsSavedAsHHmm_AndABadOneIsRefused()
    {
        var client = Client("TestUser.Admin");
        var before = (await client.GetFromJsonAsync<JsonObject>("/api/admin/ad-discovery"))!["runTime"]!.GetValue<string>();
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/ad-discovery/settings", new { runTime = "25:00" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/ad-discovery/settings", new { runTime = "05:15" })).StatusCode);
            Assert.Equal("05:15", (await client.GetFromJsonAsync<JsonObject>("/api/admin/ad-discovery"))!["runTime"]!.GetValue<string>());
        }
        finally
        {
            await client.PutAsJsonAsync("/api/admin/ad-discovery/settings", new { runTime = before });
        }
    }

    // The test database has no LDAP domain, so a run has nothing to do and says so.
    [Fact]
    public async Task RunNow_IsRecorded_WithWhoStartedIt_AndWhyItHadNothingToDo()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var before = await connection.QuerySingleAsync<int>("SELECT ISNULL(MAX(AdDiscoveryRunKey), 0) FROM web.ad_discovery_run");

        var response = await Client("TestUser.Admin").PostAsync("/api/admin/ad-discovery/run", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        (string Status, string TriggeredBy, string? Message)? run = null;
        for (var i = 0; i < 100 && run is not { Status: not "Running" }; i++)
        {
            await Task.Delay(100);
            run = await connection.QuerySingleOrDefaultAsync<(string, string, string?)?>(
                "SELECT TOP (1) Status, TriggeredBy, Message FROM web.ad_discovery_run WHERE AdDiscoveryRunKey > @before ORDER BY AdDiscoveryRunKey", new { before });
        }

        Assert.NotNull(run);
        Assert.Equal("Skipped", run.Value.Status);
        Assert.Contains("TestUser.Admin", run.Value.TriggeredBy);
        Assert.Contains("No enabled LDAP domain is used as a data source", run.Value.Message);
        var recent = (await Client("TestUser.Admin").GetFromJsonAsync<JsonObject>("/api/admin/ad-discovery"))!["recentRuns"]!.AsArray();
        Assert.Contains(recent, r => r!["status"]!.GetValue<string>() == "Skipped");
    }

    // D-324: Data Sources turns a domain's use as a data source on and off, and lists the domains with it.
    [Fact]
    public async Task ADomainsUseAsADataSource_IsSetFromDataSources()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var domain = $"ct-flag-{Guid.NewGuid():N}"[..20] + ".local";
        var key = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.ldap_config (DomainName, IsEnabled, UseTrustedConnection, UseSsl) OUTPUT inserted.LdapConfigKey VALUES (@domain, 1, 1, 0)", new { domain });
        try
        {
            var client = Client("TestUser.Admin");
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/admin/ad-discovery/domains/{key}", new { useAsDataSource = false })).StatusCode);
            Assert.False(await connection.QuerySingleAsync<bool>("SELECT UseAsDataSource FROM web.ldap_config WHERE LdapConfigKey = @key", new { key }));

            var domains = (await client.GetFromJsonAsync<JsonObject>("/api/admin/ad-discovery"))!["domains"]!.AsArray();
            Assert.Contains(domains, d => d!["domainName"]!.GetValue<string>() == domain && !d["useAsDataSource"]!.GetValue<bool>());
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/admin/ad-discovery/domains/2147483647", new { useAsDataSource = true })).StatusCode);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.ldap_config WHERE LdapConfigKey = @key", new { key });
        }
    }

    [Fact]
    public async Task Viewer_IsForbidden()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/admin/ad-discovery")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").PostAsync("/api/admin/ad-discovery/run", null)).StatusCode);
    }

    [Theory]
    [InlineData(2, 2, 0, "Succeeded")]
    [InlineData(2, 1, 1, "Partial")]
    [InlineData(2, 0, 2, "Failed")]
    public void Summary_Status_FollowsTheDomainsRead(int domains, int read, int problems, string expected)
    {
        var summary = new AdDiscoveryRunSummary(domains, read, 5, Enumerable.Repeat("x", problems).ToList(), null);
        Assert.Equal(expected, summary.Status);
        Assert.Equal("Skipped", AdDiscoveryRunSummary.Skip("nothing").Status);
    }
}
