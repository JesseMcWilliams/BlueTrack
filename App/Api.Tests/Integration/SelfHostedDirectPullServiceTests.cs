using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BlueTrack.Api.CyberArkDirect;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Tests.Contract;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-248 and D-250: a whole pull through the real service, the real repositories and the real test database, with
/// a stub in place of PVWA and a Windows DPAPI credential in place of CP: it logs on, reads, applies the change
/// guard, writes staging, and logs the outcome. The saved source settings are put back afterwards, as are the
/// pull log and the staging tables.
/// </summary>
public class SelfHostedDirectPullServiceTests : IClassFixture<BlueTrackWebApplicationFactory>, IAsyncLifetime
{
    private readonly PvwaStub _pvwa = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private string? _savedConfig;
    private long _maxPullBefore;
    private int _credentialKey;

    public SelfHostedDirectPullServiceTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // The pull's HttpClient talks to the stub, not to a PVWA.
            services.AddHttpClient(SelfHostedDirectPullService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _pvwa);
        }));
    }

    /// <summary>A PVWA with <c>Safes</c> Safes of <c>AccountsPerSafe</c> accounts each, one user, one group and one platform.</summary>
    private sealed class PvwaStub : HttpMessageHandler
    {
        public int Safes { get; set; } = 30;
        public int AccountsPerSafe { get; set; } = 2;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/PasswordVault/API/".Length..].TrimEnd('/');
            Requests.Add(request.Method + " " + path);
            int offset = int.TryParse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["offset"], out var o) ? o : 0;
            string json;
            if (path.StartsWith("auth/", StringComparison.OrdinalIgnoreCase)) json = "\"stub-token\"";
            else if (path.Equals("Auth/Logoff", StringComparison.OrdinalIgnoreCase)) json = "{}";
            else if (path == "Platforms") json = """{"Platforms":[{"general":{"id":"UnixSSH","name":"Unix via SSH","active":true,"platformType":"Regular"}}],"Total":1}""";
            else if (path == "Safes")
            {
                json = new JsonObject
                {
                    ["value"] = new JsonArray(offset > 0 ? [] : Enumerable.Range(1, Safes).Select(i => (JsonNode)new JsonObject
                    {
                        ["safeNumber"] = 910_000 + i, ["safeName"] = $"PullTestSafe{i:00}", ["safeUrlId"] = $"PullTestSafe{i:00}", ["location"] = "\\"
                    }).ToArray())
                }.ToJsonString();
            }
            else if (path == "Accounts")
            {
                json = new JsonObject
                {
                    ["value"] = new JsonArray(offset > 0 ? [] : Enumerable.Range(1, Safes).SelectMany(s => Enumerable.Range(1, AccountsPerSafe).Select(a => (JsonNode)new JsonObject
                    {
                        ["id"] = $"{910_000 + s}_{a}", ["name"] = $"acct{s}-{a}", ["address"] = "host", ["userName"] = "u", ["platformId"] = "UnixSSH", ["safeName"] = $"PullTestSafe{s:00}",
                        ["secretManagement"] = new JsonObject { ["automaticManagementEnabled"] = true }
                    })).ToArray())
                }.ToJsonString();
            }
            else if (path.StartsWith("Safes/") && path.EndsWith("/Members")) json = """{"value":[]}""";
            else if (path == "Users") json = """{"Users":[{"id":910001,"username":"pulltest.user","source":"LDAP","userType":"EPVUser","componentUser":false,"enableUser":true,"personalDetails":{"firstName":"A","lastName":"B"}}],"Total":1}""";
            else if (path == "Users/910001") json = """{"internet":{"businessEmail":"pulltest@example.com"}}""";
            else if (path == "UserGroups") json = """{"value":[{"id":910002,"groupName":"PullTestGroup","groupType":"Vault","members":[{"id":910001,"username":"pulltest.user"}]}]}""";
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"ErrorMessage\":\"no route: " + path + "\"}") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        _savedConfig = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT * FROM web.cyberark_source_config WHERE CyberArkSourceConfigKey = 1 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER");
        _maxPullBefore = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(PullKey), 0) FROM dbo.cyberark_direct_pull");

        using var scope = _factory.Services.CreateScope();
        var credentials = scope.ServiceProvider.GetRequiredService<CredentialRepository>();
        _credentialKey = await credentials.CreateAsync(new SaveCredentialRequest
        {
            CredentialName = "PullTest" + Guid.NewGuid().ToString("N")[..8], BackendType = "WindowsDpapi",
            Username = "CA_PullTest", PlaintextPassword = "ThisIsMy_FAKE_Password6!", ScopePreference = "Machine"
        }, null);
        var userKey = await connection.QuerySingleAsync<int>("SELECT MIN(UserKey) FROM web.app_user");
        await scope.ServiceProvider.GetRequiredService<CyberArkSourceConfigRepository>().SaveAsync(new CyberArkSourceSettings
        {
            ImportSelfHosted = true, SelfHostedSource = "Direct", PvwaBaseUrl = "https://pvwa.pulltest.example.com",
            PvwaCredentialKey = _credentialKey, PvwaLogonMethod = "CyberArk", DirectMaxChangePercent = 20
        }, userKey);
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM stg_sh_users; DELETE FROM stg_sh_groups; DELETE FROM stg_sh_groupmembers; DELETE FROM stg_sh_safes; DELETE FROM stg_sh_owners;
            DELETE FROM stg_sh_files; DELETE FROM stg_sh_objectproperties; DELETE FROM stg_sh_platforms;
            DELETE FROM dbo.cyberark_direct_pull WHERE PullKey > @maxPullBefore;
            DELETE FROM web.cyberark_source_config;
            """, new { maxPullBefore = _maxPullBefore });
        if (_savedConfig is not null)
        {
            await connection.ExecuteAsync("""
                INSERT INTO web.cyberark_source_config (CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, PlatformsFileName, UsersFileName, GroupsFileName,
                    GroupMembersFileName, SafesFileName, AccountsFileName, EntitlementsFileName, ImportSelfHosted, EvdDatabaseName,
                    ModifiedBy, ModifiedDate, SelfHostedSource, PvwaBaseUrl, PvwaCredentialKey, PvwaLogonMethod, DirectPullTime, DirectMaxChangePercent)
                SELECT CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, PlatformsFileName, UsersFileName, GroupsFileName,
                    GroupMembersFileName, SafesFileName, AccountsFileName, EntitlementsFileName, ImportSelfHosted, EvdDatabaseName,
                    ModifiedBy, ModifiedDate, SelfHostedSource, PvwaBaseUrl, PvwaCredentialKey, PvwaLogonMethod, DirectPullTime, DirectMaxChangePercent
                FROM OPENJSON(@json) WITH (
                    CyberArkSourceConfigKey INT, ImportPrivilegeCloud BIT, ExportFolderPath NVARCHAR(400),
                    PlatformsFileName NVARCHAR(200), UsersFileName NVARCHAR(200), GroupsFileName NVARCHAR(200),
                    GroupMembersFileName NVARCHAR(200), SafesFileName NVARCHAR(200), AccountsFileName NVARCHAR(200),
                    EntitlementsFileName NVARCHAR(200), ImportSelfHosted BIT, EvdDatabaseName NVARCHAR(128),
                    ModifiedBy INT, ModifiedDate DATETIME2, SelfHostedSource NVARCHAR(10), PvwaBaseUrl NVARCHAR(400), PvwaCredentialKey INT,
                    PvwaLogonMethod NVARCHAR(20), DirectPullTime NVARCHAR(5), DirectMaxChangePercent INT)
                """, new { json = _savedConfig });
        }
        await connection.ExecuteAsync("DELETE FROM web.credential WHERE CredentialKey = @credentialKey", new { credentialKey = _credentialKey });
        await _factory.DisposeAsync();
    }

    private async Task<PullOutcome> PullAsync(bool acceptChange = false)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SelfHostedDirectPullService>().RunAsync("tester", acceptChange, CancellationToken.None);
    }

    private static async Task<(int Safes, int Files, int Users, int Groups, int Platforms)> StagedAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<(int, int, int, int, int)>("""
            SELECT (SELECT COUNT(*) FROM stg_sh_safes), (SELECT COUNT(*) FROM stg_sh_files), (SELECT COUNT(*) FROM stg_sh_users),
                   (SELECT COUNT(*) FROM stg_sh_groups), (SELECT COUNT(*) FROM stg_sh_platforms)
            """);
    }

    [Fact]
    public async Task APullThatSucceeds_LogsOnReadsStagesAndLogsOff()
    {
        var outcome = await PullAsync();

        Assert.Equal("Succeeded", outcome.Status);
        Assert.Equal(new PullCounts(30, 60, 1, 1, 0, 1), outcome.Counts);
        Assert.Equal((30, 60, 1, 1, 1), await StagedAsync());
        Assert.Equal("POST auth/CyberArk/Logon", _pvwa.Requests[0]);
        Assert.Equal("POST Auth/Logoff", _pvwa.Requests[^1]);
        Assert.DoesNotContain(_pvwa.Requests, r => r.Contains("DiscoveredAccounts"));

        using var scope = _factory.Services.CreateScope();
        var last = await scope.ServiceProvider.GetRequiredService<SelfHostedDirectRepository>().GetLastSucceededAsync();
        Assert.Equal(("tester", 30, 60), (last!.TriggeredBy, last.SafeCount, last.AccountCount));
    }

    [Fact]
    public async Task ABigDrop_IsRejected_NamingTheSafesThatDisappeared_AndLeavesStagingAlone()
    {
        Assert.Equal("Succeeded", (await PullAsync()).Status);
        _pvwa.Safes = 5;

        var outcome = await PullAsync();

        Assert.Equal("Rejected", outcome.Status);
        Assert.Contains("Safes went from 30 to 5", outcome.Message);
        Assert.Contains("Safes no longer returned (25): PullTestSafe06, PullTestSafe07", outcome.Message);
        Assert.Contains("and 5 more", outcome.Message);   // 25 names, 20 shown
        Assert.Equal((30, 60, 1, 1, 1), await StagedAsync());   // the last good pull is still staged

        using var scope = _factory.Services.CreateScope();
        var recent = await scope.ServiceProvider.GetRequiredService<SelfHostedDirectRepository>().GetRecentAsync(2);
        Assert.Equal("Rejected", recent[0].Status);
        Assert.Equal((30, 60), (recent[0].BaselineSafeCount, recent[0].BaselineAccountCount));   // "30 to 5" on the page
        Assert.Equal((5, 10), (recent[0].SafeCount, recent[0].AccountCount));
        Assert.Contains("PullTestSafe06", recent[0].Message);
    }

    [Fact]
    public async Task AcceptingTheChange_StagesTheSmallerPull_AndRecordsItWasAccepted()
    {
        Assert.Equal("Succeeded", (await PullAsync()).Status);
        _pvwa.Safes = 5;
        Assert.Equal("Rejected", (await PullAsync()).Status);

        var outcome = await PullAsync(acceptChange: true);

        Assert.Equal("Succeeded", outcome.Status);
        Assert.Contains("An admin accepted this change: Safes went from 30 to 5", outcome.Message);
        Assert.Contains("Safes no longer returned (25)", outcome.Message);
        Assert.Equal((5, 10, 1, 1, 1), await StagedAsync());
        using var scope = _factory.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<SelfHostedDirectRepository>().GetLastSucceededAsync())!.AcceptedChange);
    }

    [Fact]
    public async Task APullWithNoSafes_Fails_AndChangesNothing()
    {
        Assert.Equal("Succeeded", (await PullAsync()).Status);
        _pvwa.Safes = 0;

        var outcome = await PullAsync();

        Assert.Equal("Failed", outcome.Status);
        Assert.Contains("PVWA returned no Safes", outcome.Message);
        Assert.Equal((30, 60, 1, 1, 1), await StagedAsync());
    }

    [Fact]
    public async Task ARefusedLogon_FailsWithoutTheCredentialInTheMessage()
    {
        var refusing = new RefusingStub();
        await using var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddHttpClient(SelfHostedDirectPullService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => refusing)));
        using var scope = factory.Services.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<SelfHostedDirectPullService>().RunAsync("tester", false, CancellationToken.None);

        Assert.Equal("Failed", outcome.Status);
        Assert.Contains("the credential was refused", outcome.Message);
        Assert.DoesNotContain("ThisIsMy_FAKE_Password6!", outcome.Message);
        Assert.Equal((0, 0, 0, 0, 0), await StagedAsync());
    }

    private sealed class RefusingStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"ErrorMessage\":\"Authentication failure\"}") });
    }

    [Fact]
    public async Task WhenDirectIsNotTheSavedSource_NothingIsPulled()
    {
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync("UPDATE web.cyberark_source_config SET SelfHostedSource = 'Evd', EvdDatabaseName = 'CyberArkSH'");
        }

        var outcome = await PullAsync();

        Assert.Equal("Skipped", outcome.Status);
        Assert.Empty(_pvwa.Requests);
    }

    [Fact]
    public async Task Test_ReportsTheCredentialLogonAndCounts_WritingNothing()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = (await scope.ServiceProvider.GetRequiredService<CyberArkSourceConfigRepository>().GetAsync()).Settings;

        var checks = await scope.ServiceProvider.GetRequiredService<SelfHostedDirectPullService>().TestAsync(settings, CancellationToken.None);

        Assert.All(checks, c => Assert.True(c.Ok, c.Item + ": " + c.Message));
        Assert.Contains(checks, c => c.Item == "Credential" && c.Message.Contains("CA_PullTest"));
        Assert.Contains(checks, c => c.Item == "PVWA logon");
        Assert.Contains("30 Safes, 60 accounts", checks.Single(c => c.Item == "Read").Message);
        Assert.Equal((0, 0, 0, 0, 0), await StagedAsync());
    }
}
