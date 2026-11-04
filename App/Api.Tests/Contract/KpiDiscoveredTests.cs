using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-326: the KPI funnel starts with everything discovered: tracked accounts, plus AD candidates and DNA accounts not
/// tracked yet. In the DNA import's collection, since that test replaces the stored DNA accounts.
/// </summary>
[Collection("DnaImport")]
public class KpiDiscoveredTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    [Fact]
    public async Task EverythingDiscovered_IsTheTrackedAccounts_PlusTheUntrackedAdAndDnaAccounts()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
        var summary = (await client.GetFromJsonAsync<JsonObject>("/api/reports/kpi-summary"))!;
        int Value(string name) => summary[name]!.GetValue<int>();

        Assert.Equal(Value("totalAccounts") + Value("untrackedAdCandidates") + Value("untrackedDnaAccounts"), Value("discoveredAccounts"));

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        Assert.Equal(await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM web.discovered_account WHERE Status <> 'Accepted' AND ResolvedAccountKey IS NULL AND PossibleExistingAccountKey IS NULL"),
            Value("untrackedAdCandidates"));
    }

    [Fact]
    public async Task TheKpiExport_StartsWithAllAccountsVsEverythingDiscovered()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
        var csv = await client.GetStringAsync("/api/reports/kpi-summary/export?format=csv");
        var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("All Accounts vs. Everything Discovered,", lines[1]);
        Assert.Equal(6, lines.Length);   // the header and five ratios
    }
}
