using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-308: a Safe's page (GET /api/safes/{safeKey}), the Accounts list's <c>safe</c> filter behind its accounts, and the
/// Safe on the list's rows and on the account page.
/// </summary>
public class SafeDetailsTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Viewer()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
        return client;
    }

    private static async Task<(long AccountKey, int SafeKey, string SafeName, int ActiveCount)> AnAccountWithASafeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QueryFirstAsync<(long, int, string, int)>("""
            SELECT TOP 1 fa.AccountKey, ds.SafeKey, ds.SafeName,
                   (SELECT COUNT(*) FROM dbo.fact_account x WHERE x.SafeKey = ds.SafeKey AND x.IsDeleted = 0)
            FROM dbo.fact_account fa
            JOIN dbo.fact_account_progress fap ON fap.AccountKey = fa.AccountKey
            JOIN dbo.dim_safe ds ON ds.SafeKey = fa.SafeKey
            WHERE fa.IsDeleted = 0
            ORDER BY fa.AccountKey
            """);
    }

    [Fact]
    public async Task GetSafe_ReturnsTheSafeAndItsAccountCount()
    {
        var (_, safeKey, safeName, activeCount) = await AnAccountWithASafeAsync();

        var safe = await Viewer().GetFromJsonAsync<JsonObject>($"/api/safes/{safeKey}");

        Assert.Equal(safeName, safe!["safeName"]!.GetValue<string>());
        Assert.Equal(activeCount, safe["activeAccountCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetSafe_UnknownKey_IsNotFound()
    {
        var response = await Viewer().GetAsync("/api/safes/2147483647");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AccountList_SafeFilter_ListsOnlyThatSafesAccounts_WithTheSafeOnEachRow()
    {
        var (_, safeKey, safeName, _) = await AnAccountWithASafeAsync();

        var response = await Viewer().GetAsync($"/api/account-progress?safe={safeKey}&pageSize=200");
        response.EnsureSuccessStatusCode();
        var rows = (await response.Content.ReadFromJsonAsync<JsonArray>())!;

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal(safeKey, row!["safeKey"]!.GetValue<int>());
            Assert.Equal(safeName, row["safeName"]!.GetValue<string>());
        });
        Assert.Equal(rows.Count.ToString(), response.Headers.GetValues("X-Filtered-Count").Single());
    }

    [Fact]
    public async Task AccountDetail_NamesItsSafe()
    {
        var (accountKey, safeKey, safeName, _) = await AnAccountWithASafeAsync();

        var detail = await Viewer().GetFromJsonAsync<JsonObject>($"/api/account-progress/{accountKey}");

        Assert.Equal(safeKey, detail!["safeKey"]!.GetValue<int>());
        Assert.Equal(safeName, detail["safeName"]!.GetValue<string>());
    }
}
