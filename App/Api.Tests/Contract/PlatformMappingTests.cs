using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-247: Admin > Platform Mapping. Each test adds its own platform and accounts and removes them afterwards.
/// </summary>
public class PlatformMappingTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public PlatformMappingTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user = "TestUser.Admin")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record Row(int PlatformKey, string PlatformId, bool IsUnmapped, int AccountCount, int AccountsWithoutType, int AccountsWithoutSor, string? AccountTypeName, string? SourceOfRecord, string? Notes);
    private sealed record SaveResult(int AccountsTypeFilled, int AccountsSorFilled);

    private static async Task<(int PlatformKey, string PlatformId, List<long> AccountKeys)> AddPlatformWithAccountsAsync(SqlConnection connection, string tag)
    {
        var platformId = $"PmTest{tag}";
        var platformKey = await connection.QuerySingleAsync<int>("INSERT INTO dbo.dim_platform (SourceSystemKey, PlatformID, PlatformName) OUTPUT inserted.PlatformKey VALUES (1, @platformId, @platformId)", new { platformId });
        var keys = new List<long>();
        for (var i = 1; i <= 3; i++)
        {
            var key = await connection.QuerySingleAsync<long>(
                "INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, PlatformKey) OUTPUT inserted.AccountKey VALUES (1, @id, @id, @platformKey)",
                new { id = $"pm-{tag}-{i}", platformKey });
            await connection.ExecuteAsync("""
                INSERT INTO dbo.fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
                SELECT @key, (SELECT TOP 1 StageKey FROM dbo.dim_blueprint_stage ORDER BY StageOrder), (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'Not Started')
                """, new { key });
            keys.Add(key);
        }
        return (platformKey, platformId, keys);
    }

    private static async Task CleanUpAsync(SqlConnection connection, int platformKey, List<long> accountKeys)
    {
        await connection.ExecuteAsync("""
            DELETE FROM dbo.platform_account_type_map WHERE PlatformKey = @platformKey;
            DELETE FROM dbo.fact_account_progress WHERE AccountKey IN @accountKeys;
            DELETE FROM dbo.fact_account WHERE AccountKey IN @accountKeys;
            DELETE FROM dbo.dim_platform WHERE PlatformKey = @platformKey;
            """, new { platformKey, accountKeys });
    }

    [Fact]
    public async Task SavingAMapping_FillsOnlyAccountsWithNoValue_WhenAsked_AndIsAudited()
    {
        var admin = Client();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var (platformKey, platformId, accounts) = await AddPlatformWithAccountsAsync(connection, tag);
        try
        {
            var options = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/platform-mapping/options"))!;
            var types = options["accountTypes"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["key"]!.GetValue<int>());
            var sors = options["sourcesOfRecord"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["key"]!.GetValue<int>());
            var domain = types["Domain Account"];
            var local = types["Local/OS Account"];
            var ad = sors["Active Directory"];

            // One account already has a type (a person chose it); another already has a source of record.
            await connection.ExecuteAsync("UPDATE dbo.fact_account_progress SET AccountTypeKey = @local WHERE AccountKey = @key", new { local, key = accounts[0] });
            await connection.ExecuteAsync("UPDATE dbo.fact_account_progress SET SORKey = @sor WHERE AccountKey = @key", new { sor = sors["LDAP"], key = accounts[1] });

            var before = (await admin.GetFromJsonAsync<List<Row>>("/api/admin/platform-mapping"))!.Single(r => r.PlatformKey == platformKey);
            Assert.True(before.IsUnmapped);
            Assert.Equal((3, 2, 2), (before.AccountCount, before.AccountsWithoutType, before.AccountsWithoutSor));

            // Without fill: the mapping is saved and no account changes.
            var plain = await admin.PutAsJsonAsync($"/api/admin/platform-mapping/{platformKey}", new { accountTypeKey = domain, sorKey = ad, notes = " Windows domain accounts ", fillExisting = false });
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
            Assert.Equal((0, 0), ((await plain.Content.ReadFromJsonAsync<SaveResult>())!.AccountsTypeFilled, 0));
            var mapped = (await admin.GetFromJsonAsync<List<Row>>("/api/admin/platform-mapping"))!.Single(r => r.PlatformKey == platformKey);
            Assert.False(mapped.IsUnmapped);
            Assert.Equal(("Domain Account", "Active Directory", "Windows domain accounts"), (mapped.AccountTypeName, mapped.SourceOfRecord, mapped.Notes));
            Assert.Equal(2, mapped.AccountsWithoutType);

            // With fill: only the accounts with no value are given one.
            var fill = await admin.PutAsJsonAsync($"/api/admin/platform-mapping/{platformKey}", new { accountTypeKey = domain, sorKey = ad, notes = "Windows domain accounts", fillExisting = true });
            var result = (await fill.Content.ReadFromJsonAsync<SaveResult>())!;
            Assert.Equal((2, 2), (result.AccountsTypeFilled, result.AccountsSorFilled));
            var progress = (await connection.QueryAsync<(long AccountKey, int? TypeKey, int? SorKey)>(
                "SELECT AccountKey, AccountTypeKey, SORKey FROM dbo.fact_account_progress WHERE AccountKey IN @accounts", new { accounts })).ToDictionary(r => r.AccountKey);
            Assert.Equal((local, ad), (progress[accounts[0]].TypeKey, progress[accounts[0]].SorKey));   // its own type kept, source of record filled
            Assert.Equal((domain, sors["LDAP"]), (progress[accounts[1]].TypeKey, progress[accounts[1]].SorKey));   // its own source of record kept
            Assert.Equal((domain, ad), (progress[accounts[2]].TypeKey, progress[accounts[2]].SorKey));

            var details = (await connection.QueryAsync<string>("""
                SELECT TOP (5) e.Detail FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
                WHERE t.EventTypeName = 'FieldEdit' AND e.EntityName = 'platform_account_type_map' AND e.EntityKey = @key ORDER BY e.AuditEventKey DESC
                """, new { key = platformKey.ToString() })).ToList();
            Assert.Contains(details, d => d.Contains(platformId) && d.Contains("filled 2 account(s) with no account type and 2 with no source of record"));

            // Removing the mapping leaves the accounts as they are.
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/platform-mapping/{platformKey}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/platform-mapping/{platformKey}")).StatusCode);
            Assert.True((await admin.GetFromJsonAsync<List<Row>>("/api/admin/platform-mapping"))!.Single(r => r.PlatformKey == platformKey).IsUnmapped);
            Assert.Equal(domain, await connection.QuerySingleAsync<int>("SELECT AccountTypeKey FROM dbo.fact_account_progress WHERE AccountKey = @key", new { key = accounts[2] }));
        }
        finally
        {
            await CleanUpAsync(connection, platformKey, accounts);
        }
    }

    [Fact]
    public async Task Save_ValidatesTheRequest_AndNeedsCuratePlatformMapping()
    {
        var admin = Client();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var (platformKey, _, accounts) = await AddPlatformWithAccountsAsync(connection, tag);
        try
        {
            var typeKey = await connection.QuerySingleAsync<int>("SELECT TOP (1) AccountTypeKey FROM dbo.dim_account_type ORDER BY AccountTypeKey");
            async Task<HttpStatusCode> Put(int key, object body) => (await admin.PutAsJsonAsync($"/api/admin/platform-mapping/{key}", body)).StatusCode;

            Assert.Equal(HttpStatusCode.NotFound, await Put(int.MaxValue, new { accountTypeKey = typeKey }));
            Assert.Equal(HttpStatusCode.BadRequest, await Put(platformKey, new { accountTypeKey = 0 }));
            Assert.Equal(HttpStatusCode.BadRequest, await Put(platformKey, new { accountTypeKey = typeKey, sorKey = 0 }));
            Assert.Equal(HttpStatusCode.BadRequest, await Put(platformKey, new { accountTypeKey = typeKey, notes = new string('x', 501) }));
            Assert.Equal(HttpStatusCode.OK, await Put(platformKey, new { accountTypeKey = typeKey }));   // a source of record is optional

            var analyst = Client("TestUser.Analyst");
            Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("/api/admin/platform-mapping")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PutAsJsonAsync($"/api/admin/platform-mapping/{platformKey}", new { accountTypeKey = typeKey })).StatusCode);
        }
        finally
        {
            await CleanUpAsync(connection, platformKey, accounts);
        }
    }
}
