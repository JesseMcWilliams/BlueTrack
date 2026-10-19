using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// UI review W3-4: GET /api/risk-exceptions/{key}/accounts lists the accounts an application-scoped exception covers
/// (here through the Safe the application is assigned to), is empty for an account-scoped exception, and 404s for an
/// unknown one.
/// </summary>
public class RiskExceptionCoveredAccountsTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task ApplicationScoped_ListsTheAccountsInItsSafes()
    {
        var admin = Client("TestUser.Admin");
        var approver = Client("TestUser.Approver");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var appResponse = await admin.PostAsJsonAsync("/api/applications", new { applicationCode = "COV" + tag, applicationName = "Covered " + tag });
        appResponse.EnsureSuccessStatusCode();
        var applicationKey = (await appResponse.Content.ReadFromJsonAsync<JsonObject>())!["applicationKey"]!.GetValue<int>();
        var safes = await admin.GetFromJsonAsync<JsonArray>("/api/safes");
        var safe = safes!.First(s => s!["safeName"]!.GetValue<string>() == "TestSafe01")!;
        var safeKey = safe["safeKey"]!.GetValue<int>();
        var original = safe["applicationKey"]?.GetValue<int>();
        try
        {
            (await admin.PutAsJsonAsync($"/api/safes/{safeKey}/application", applicationKey)).EnsureSuccessStatusCode();
            var created = await approver.PostAsJsonAsync("/api/risk-exceptions", new
            {
                applicationKey,
                justification = "Covered accounts test",
                reviewDate = DateTime.UtcNow.Date.AddDays(30)
            });
            created.EnsureSuccessStatusCode();
            var exceptionKey = (await created.Content.ReadFromJsonAsync<JsonObject>())!["exceptionKey"]!.GetValue<int>();

            var response = await Client("TestUser.Viewer").GetAsync($"/api/risk-exceptions/{exceptionKey}/accounts");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var accounts = (await response.Content.ReadFromJsonAsync<JsonArray>())!;
            Assert.NotEmpty(accounts);
            Assert.All(accounts, a => Assert.Equal("TestSafe01", a!["safeName"]!.GetValue<string>()));
            Assert.Equal(accounts.Count.ToString(), Assert.Single(response.Headers.GetValues("X-Total-Count")));
        }
        finally
        {
            (await admin.PutAsJsonAsync<int?>($"/api/safes/{safeKey}/application", original)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task AccountScoped_IsEmpty_AndAnUnknownExceptionIsNotFound()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var created = await Client("TestUser.Approver").PostAsJsonAsync("/api/risk-exceptions", new
        {
            accountKey,
            justification = "Account scoped",
            reviewDate = DateTime.UtcNow.Date.AddDays(30)
        });
        created.EnsureSuccessStatusCode();
        var exceptionKey = (await created.Content.ReadFromJsonAsync<JsonObject>())!["exceptionKey"]!.GetValue<int>();
        var viewer = Client("TestUser.Viewer");

        var accounts = await viewer.GetFromJsonAsync<JsonArray>($"/api/risk-exceptions/{exceptionKey}/accounts");
        Assert.Empty(accounts!);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/risk-exceptions/2147483000/accounts")).StatusCode);
    }
}
