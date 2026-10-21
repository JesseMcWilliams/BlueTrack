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
    private long _maxEtlRunBefore;
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
        /// <summary>D-273: how long the Safes call hangs before it answers (a PVWA that stops responding).</summary>
        public TimeSpan SafesDelay { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var absolute = request.RequestUri!.AbsolutePath;
            var path = (absolute.StartsWith("/PasswordVault/API/", StringComparison.Ordinal) ? absolute["/PasswordVault/API/".Length..] : absolute).TrimEnd('/');
            if (path == "Safes" && SafesDelay > TimeSpan.Zero) return HangThenAnswerAsync(request, cancellationToken);
            Requests.Add(request.Method + " " + path);
            int offset = int.TryParse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["offset"], out var o) ? o : 0;
            string json;
            if (path.StartsWith("auth/", StringComparison.OrdinalIgnoreCase)) json = "\"stub-token\"";
            else if (path.Equals("Auth/Logoff", StringComparison.OrdinalIgnoreCase)) json = "{}";
            else if (path == "Platforms") json = """{"Platforms":[{"general":{"id":"UnixSSH","name":"Unix via SSH","active":true,"platformType":"Regular"},"credentialsManagement":{"performPeriodicVerification":true,"requirePasswordVerificationEveryXDays":7}}],"Total":1}""";
            else if (path == "Platforms/Targets") json = """{"Platforms":[],"Total":0}""";
            else if (path == "/PasswordVault/WebServices/PIMServices.svc/Accounts/910001_1/Activities")
                json = """{"GetAccountActivitiesSlashResult":[{"ActivityCode":22,"Activity":"CPM Verify Password","Time":"10/20/2026 00:12:47","UserName":"PasswordManager"}]}""";
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
                        // One account the CPM has acted on, so its activity is read.
                        ["secretManagement"] = s == 1 && a == 1
                            ? new JsonObject { ["automaticManagementEnabled"] = true, ["status"] = "success", ["lastReconciledTime"] = 1792455153 }
                            : new JsonObject { ["automaticManagementEnabled"] = true }
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

        private async Task<HttpResponseMessage> HangThenAnswerAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(SafesDelay, cancellationToken);
            SafesDelay = TimeSpan.Zero;
            return await SendAsync(request, cancellationToken);
        }
    }

    /// <summary>D-273: a clock on which a minute-long timer fires after a millisecond, so a 45-minute limit is reached at once.</summary>
    private sealed class MinutesAreMillisecondsTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.FromMilliseconds(dueTime.TotalMinutes), period);
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        _savedConfig = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT * FROM web.cyberark_source_config WHERE CyberArkSourceConfigKey = 1 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER");
        _maxPullBefore = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(PullKey), 0) FROM dbo.cyberark_direct_pull");
        _maxEtlRunBefore = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(EtlRunKey), 0) FROM dbo.etl_run_log");

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
            DELETE FROM stg_shd_platforms; DELETE FROM stg_shd_platform_properties; DELETE FROM stg_shd_safes; DELETE FROM stg_shd_accounts;
            DELETE FROM stg_shd_account_properties; DELETE FROM stg_shd_account_cpm_activity; DELETE FROM stg_shd_safe_members;
            DELETE FROM stg_shd_users; DELETE FROM stg_shd_user_authorizations; DELETE FROM stg_shd_groups; DELETE FROM stg_shd_group_members;
            DELETE FROM dbo.cyberark_direct_pull WHERE PullKey > @maxPullBefore;
            DELETE FROM dbo.etl_run_log WHERE EtlRunKey > @maxEtlRunBefore;
            DELETE FROM web.cyberark_source_config;
            """, new { maxPullBefore = _maxPullBefore, maxEtlRunBefore = _maxEtlRunBefore });
        if (_savedConfig is not null)
        {
            await connection.ExecuteAsync("""
                INSERT INTO web.cyberark_source_config (CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, PlatformsFileName, UsersFileName, GroupsFileName,
                    GroupMembersFileName, SafesFileName, AccountsFileName, EntitlementsFileName, ImportSelfHosted,
                    ModifiedBy, ModifiedDate, SelfHostedSource, PvwaBaseUrl, PvwaCredentialKey, PvwaLogonMethod, DirectPullTime, DirectMaxChangePercent,
                    DirectPullTimeLimitMinutes)
                SELECT CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, PlatformsFileName, UsersFileName, GroupsFileName,
                    GroupMembersFileName, SafesFileName, AccountsFileName, EntitlementsFileName, ImportSelfHosted,
                    ModifiedBy, ModifiedDate, SelfHostedSource, PvwaBaseUrl, PvwaCredentialKey, PvwaLogonMethod, DirectPullTime, DirectMaxChangePercent,
                    ISNULL(DirectPullTimeLimitMinutes, 45)
                FROM OPENJSON(@json) WITH (
                    CyberArkSourceConfigKey INT, ImportPrivilegeCloud BIT, ExportFolderPath NVARCHAR(400),
                    PlatformsFileName NVARCHAR(200), UsersFileName NVARCHAR(200), GroupsFileName NVARCHAR(200),
                    GroupMembersFileName NVARCHAR(200), SafesFileName NVARCHAR(200), AccountsFileName NVARCHAR(200),
                    EntitlementsFileName NVARCHAR(200), ImportSelfHosted BIT,
                    ModifiedBy INT, ModifiedDate DATETIME2, SelfHostedSource NVARCHAR(10), PvwaBaseUrl NVARCHAR(400), PvwaCredentialKey INT,
                    PvwaLogonMethod NVARCHAR(20), DirectPullTime NVARCHAR(5), DirectMaxChangePercent INT,
                    DirectPullTimeLimitMinutes INT)
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
    public async Task APullThatSucceeds_AlsoFillsTheDirectTables_UnderTheSameBatch()
    {
        var outcome = await PullAsync();

        Assert.Equal("Succeeded", outcome.Status);
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var counts = await connection.QuerySingleAsync<(int, int, int, int, int, int, int)>("""
            SELECT (SELECT COUNT(*) FROM stg_shd_safes), (SELECT COUNT(*) FROM stg_shd_accounts), (SELECT COUNT(*) FROM stg_shd_users),
                   (SELECT COUNT(*) FROM stg_shd_groups), (SELECT COUNT(*) FROM stg_shd_group_members), (SELECT COUNT(*) FROM stg_shd_platforms),
                   (SELECT COUNT(*) FROM stg_shd_account_cpm_activity)
            """);
        Assert.Equal((30, 60, 1, 1, 1, 1, 1), counts);
        var cpm = await connection.QuerySingleAsync<(string, string, DateTime?, int, DateTime?)>("""
            SELECT a.AccountId, a.CpmStatus, a.LastReconciledTime, c.ActivityCode, c.ActivityTime
            FROM stg_shd_accounts a JOIN stg_shd_account_cpm_activity c ON c.AccountId = a.AccountId
            """);
        Assert.Equal(("910001_1", "success", (DateTime?)DateTimeOffset.FromUnixTimeSeconds(1792455153).UtcDateTime, 22, (DateTime?)new DateTime(2026, 10, 20, 0, 12, 47)), cpm);
        Assert.Equal(7, await connection.QuerySingleAsync<int>("SELECT RequirePasswordVerificationEveryXDays FROM stg_shd_platforms"));

        // One batch across both sets of tables, and it is the one the pull log records.
        var batches = await connection.QueryAsync<Guid>("""
            SELECT ImportBatchId FROM stg_shd_safes UNION SELECT ImportBatchId FROM stg_shd_accounts UNION SELECT ImportBatchId FROM stg_sh_files
            """);
        Assert.Equal(Assert.Single(batches), await connection.QuerySingleAsync<Guid>(
            "SELECT ImportBatchId FROM dbo.cyberark_direct_pull WHERE PullKey = (SELECT MAX(PullKey) FROM dbo.cyberark_direct_pull)"));
    }

    [Fact]
    public async Task ARejectedPull_LeavesTheDirectTablesAlone_Too()
    {
        Assert.Equal("Succeeded", (await PullAsync()).Status);
        _pvwa.Safes = 5;

        Assert.Equal("Rejected", (await PullAsync()).Status);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        Assert.Equal((30, 60), await connection.QuerySingleAsync<(int, int)>("SELECT (SELECT COUNT(*) FROM stg_shd_safes), (SELECT COUNT(*) FROM stg_shd_accounts)"));
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
    public async Task WhenSelfHostedIsOff_NothingIsPulled()
    {
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync("UPDATE web.cyberark_source_config SET ImportSelfHosted = 0, ImportPrivilegeCloud = 1, ExportFolderPath = 'D:\\Exports'");
        }

        var outcome = await PullAsync();

        Assert.Equal("Skipped", outcome.Status);
        Assert.Empty(_pvwa.Requests);
    }

    [Fact]
    public async Task APullPastItsTimeLimit_IsStopped_LoggedAsFailed_AndLeavesStagingAlone()
    {
        Assert.Equal("Succeeded", (await PullAsync()).Status);
        var before = await StagedAsync();
        _pvwa.Safes = 5;
        _pvwa.SafesDelay = TimeSpan.FromSeconds(30);

        using var scope = _factory.Services.CreateScope();
        var service = ActivatorUtilities.CreateInstance<SelfHostedDirectPullService>(scope.ServiceProvider, (TimeProvider)new MinutesAreMillisecondsTimeProvider());
        var outcome = await service.RunAsync("tester", acceptChange: true, CancellationToken.None);

        Assert.Equal("Failed", outcome.Status);
        Assert.StartsWith("The pull was stopped after the 45-minute time limit", outcome.Message);
        Assert.Equal(before, await StagedAsync());
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        Assert.Equal("Failed", await connection.QuerySingleAsync<string>("SELECT Status FROM dbo.cyberark_direct_pull WHERE PullKey = (SELECT MAX(PullKey) FROM dbo.cyberark_direct_pull)"));
    }

    [Fact]
    public async Task ACancelledPull_IsLoggedAsCancelled_AndLeavesStagingAlone()
    {
        Assert.Equal("Succeeded", (await PullAsync()).Status);
        var before = await StagedAsync();
        _pvwa.Safes = 5;
        _pvwa.SafesDelay = TimeSpan.FromSeconds(30);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));   // D-275: an admin's Cancel

        using var scope = _factory.Services.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<SelfHostedDirectPullService>()
            .RunAsync("tester", acceptChange: true, CancellationToken.None, cancel.Token);

        Assert.Equal(("Failed", "The pull was cancelled on Data Sources. The staged data wasn't changed."), (outcome.Status, outcome.Message));
        Assert.Equal(before, await StagedAsync());
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
