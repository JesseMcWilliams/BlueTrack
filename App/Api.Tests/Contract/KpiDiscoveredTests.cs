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
    // D-329: an account found by both AD discovery and DNA counts once, with the AD candidates (which include dismissed
    // ones; the first test checks that). Only the DNA count is compared: other tests change the AD candidates meanwhile.
    [Fact]
    public async Task AnAccountFoundByAdAndDna_CountsOnce_AndADismissedCandidateCounts()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
        async Task<JsonObject> SummaryAsync() => (await client.GetFromJsonAsync<JsonObject>("/api/reports/kpi-summary"))!;
        var name = $"ct_dup_{Guid.NewGuid():N}"[..20];
        var dismissed = $"ct_dis_{Guid.NewGuid():N}"[..20];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var before = await SummaryAsync();
        var importKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dna_import (FileName, AccountCount) OUTPUT inserted.DnaImportKey VALUES (N'kpi-test.xlsx', 1)");
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO web.discovered_account (DomainName, SamAccountName, Status) VALUES (N'ct.local', @name, N'New'), (N'ct.local', @dismissed, N'Dismissed');
                INSERT INTO web.dna_account (DnaImportKey, SheetCode, IsDomainAccount, AccountName) VALUES (@importKey, N'NC-DA', 1, N'CT\' + @name);
                """, new { name, dismissed, importKey });

            var after = await SummaryAsync();
            Assert.Equal(before["untrackedDnaAccounts"]!.GetValue<int>(), after["untrackedDnaAccounts"]!.GetValue<int>());
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM web.dna_account WHERE DnaImportKey = @importKey;
                DELETE FROM web.dna_import WHERE DnaImportKey = @importKey;
                DELETE FROM web.discovered_account WHERE SamAccountName IN (@name, @dismissed);
                """, new { importKey, name, dismissed });
        }
    }
}
