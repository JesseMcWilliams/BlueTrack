using System.Data;
using BlueTrack.Api.CyberArkDirect;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-248 (script 38): Self-Hosted Direct against the real test database -- the pull log, writing a pull into the
/// stg_sh_* tables, the Import step's gate on the latest pull, and the Load using PVWA's platform list. Tests that
/// change shared rows run in a transaction they roll back; the staging write opens its own connection, so that
/// test empties the staging tables itself afterwards (they are empty between tests).
/// </summary>
public class SelfHostedDirectTests
{
    private static async Task<(SqlConnection Connection, SqlTransaction Transaction)> BeginAsync()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        return (connection, (SqlTransaction)await connection.BeginTransactionAsync());
    }

    private static Task SetSourceAsync(IDbConnection connection, IDbTransaction transaction, string source, bool selfHosted = true) =>
        connection.ExecuteAsync("""
            DELETE FROM web.cyberark_source_config;
            INSERT INTO web.cyberark_source_config
                (CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, ImportSelfHosted, EvdDatabaseName, SelfHostedSource, PvwaBaseUrl)
            VALUES (1, 0, NULL, @selfHosted, NULL, @source, 'https://pvwa.example.com');
            DELETE FROM dbo.cyberark_direct_pull;
            """, new { source, selfHosted }, transaction);

    private static Task AddPullAsync(IDbConnection connection, IDbTransaction transaction, string status, int hoursAgo, string? message = null) =>
        connection.ExecuteAsync("""
            INSERT INTO dbo.cyberark_direct_pull (StartedAt, CompletedAt, Status, TriggeredBy, Message, SafeCount, AccountCount)
            VALUES (DATEADD(HOUR, -@hoursAgo, SYSUTCDATETIME()), CASE WHEN @status = 'Started' THEN NULL ELSE DATEADD(HOUR, -@hoursAgo, SYSUTCDATETIME()) END,
                    @status, 'test', @message, 3, 10)
            """, new { status, hoursAgo, message }, transaction);

    [Fact]
    public async Task ImportConfigured_Direct_WithNoPullYet_FailsTheNight_AndLogsTheReason()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        await SetSourceAsync(connection, transaction, "Direct");
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(EtlRunKey), 0) FROM dbo.etl_run_log", transaction: transaction);

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("EXEC dbo.usp_Import_Configured", transaction: transaction));

        Assert.Contains("no PVWA pull has run yet", error.Message);
        var run = await connection.QuerySingleAsync<(string Status, string ErrorMessage)>(
            "SELECT Status, ErrorMessage FROM dbo.etl_run_log WHERE EtlRunKey > @before", new { before }, transaction);
        Assert.Equal("Failed", run.Status);
        Assert.Contains("Self-Hosted Direct", run.ErrorMessage);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("Rejected", 1, "Safes went from 40 to 10", "pull rejected. Safes went from 40 to 10")]
    [InlineData("Failed", 1, "PVWA refused to log on (401)", "pull failed. PVWA refused to log on (401)")]
    [InlineData("Started", 1, null, "never finished")]
    [InlineData("Succeeded", 21, null, "more than 20 hours old")]
    public async Task ImportConfigured_Direct_WhenTheLatestPullIsNotARecentSuccess_FailsTheNight(string status, int hoursAgo, string? message, string expected)
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        await SetSourceAsync(connection, transaction, "Direct");
        await AddPullAsync(connection, transaction, "Succeeded", 30);   // an older good pull doesn't rescue a newer bad one
        await AddPullAsync(connection, transaction, status, hoursAgo, message);

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("EXEC dbo.usp_Import_Configured", transaction: transaction));

        Assert.Contains(expected, error.Message);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ImportConfigured_Direct_AfterARecentSuccess_Succeeds_AndLeavesThePulledStagingAlone()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        await SetSourceAsync(connection, transaction, "Direct");
        await AddPullAsync(connection, transaction, "Succeeded", 1);
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(EtlRunKey), 0) FROM dbo.etl_run_log", transaction: transaction);
        await connection.ExecuteAsync("""
            DELETE FROM stg_sh_safes;
            INSERT INTO stg_sh_safes (ImportBatchId, SourceFileName, CASSafeID, CASSafeName) VALUES (NEWID(), 'PVWA REST (test)', 987654, 'PulledSafe');
            INSERT INTO stg_sh_platforms (ImportBatchId, SourceFileName, PlatformID) VALUES (NEWID(), 'PVWA REST (test)', 'PulledPlatform');
            """, transaction: transaction);

        await connection.ExecuteAsync("EXEC dbo.usp_Import_Configured", transaction: transaction);

        Assert.Equal("Succeeded", await connection.QuerySingleAsync<string>("SELECT Status FROM dbo.etl_run_log WHERE EtlRunKey > @before", new { before }, transaction));
        Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM stg_sh_safes WHERE CASSafeName = 'PulledSafe'", transaction: transaction));
        Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM stg_sh_platforms WHERE PlatformID = 'PulledPlatform'", transaction: transaction));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ImportConfigured_Direct_WithSelfHostedOff_IgnoresTheGate()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        // Self-Hosted off and no Privilege Cloud: nothing to import, so the existing "turn on a source" rule fires, not the Direct gate.
        await SetSourceAsync(connection, transaction, "Direct", selfHosted: false);

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("EXEC dbo.usp_Import_Configured", transaction: transaction));

        Assert.Contains("both @ImportPrivilegeCloud and @ImportSelfHosted are off", error.Message);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ClearStagingSelfHosted_AlsoEmptiesThePvwaPlatformList()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        await connection.ExecuteAsync("INSERT INTO stg_sh_platforms (ImportBatchId, SourceFileName, PlatformID) VALUES (NEWID(), 'x', 'Left')", transaction: transaction);

        await connection.ExecuteAsync("EXEC dbo.usp_Clear_Staging_SelfHosted", transaction: transaction);

        Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM stg_sh_platforms", transaction: transaction));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Load_UsesPvwasPlatformList_ToNameAndDescribeThePlatforms()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var used = $"UsedPlat{tag}";
        var unused = $"UnusedPlat{tag}";
        await connection.ExecuteAsync("""
            DELETE FROM stg_sh_files; DELETE FROM stg_sh_objectproperties; DELETE FROM stg_sh_platforms;
            INSERT INTO stg_sh_files (ImportBatchId, SourceFileName, CAFSafeID, CAFSafeName, CAFFileID, CAFFileName, CAFType)
            VALUES (NEWID(), 'x', 987001, 'S', 1, 'a', 2);
            INSERT INTO stg_sh_objectproperties (ImportBatchId, SourceFileName, CAOPSafeId, CAOPFileId, CAOPObjectPropertyName, CAOPObjectPropertyValue)
            VALUES (NEWID(), 'x', 987001, 1, 'PolicyID', @used);
            INSERT INTO stg_sh_platforms (ImportBatchId, SourceFileName, PlatformID, PlatformName, Description, IsActive, PlatformType)
            VALUES (NEWID(), 'x', @used, 'Friendly Used Name', 'Used description', 1, 'regular'),
                   (NEWID(), 'x', @unused, 'Friendly Unused Name', NULL, 0, 'group');
            EXEC dbo.usp_Load_DimPlatform;
            """, new { used, unused }, transaction);

        var rows = (await connection.QueryAsync<(string PlatformID, string? PlatformName, string? Description, bool? IsActive, string? PlatformType)>("""
            SELECT p.PlatformID, p.PlatformName, p.Description, p.IsActive, p.PlatformType
            FROM dbo.dim_platform p JOIN dbo.dim_source_system s ON s.SourceSystemKey = p.SourceSystemKey AND s.SourceSystemCode = 'SELFHOSTED'
            WHERE p.PlatformID IN (@used, @unused) ORDER BY p.PlatformID
            """, new { used, unused }, transaction)).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(("Friendly Unused Name", (string?)null, (bool?)false, "group"), (rows[0].PlatformName, rows[0].Description, rows[0].IsActive, rows[0].PlatformType));
        Assert.Equal(("Friendly Used Name", "Used description", (bool?)true, "regular"), (rows[1].PlatformName, rows[1].Description, rows[1].IsActive, rows[1].PlatformType));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task StageAsync_ReplacesTheStagingTables_InOneTransaction_AndTheLoadsKeysMatch()
    {
        var repository = new SelfHostedDirectRepository(new TestDbConnectionFactory());
        var batch = Guid.NewGuid();
        PvwaSnapshot Snapshot(int accounts)
        {
            var s = new PvwaSnapshot
            {
                Safes = { new PvwaSafe(987001, "Safe%20One", "Safe One", "\\", "Administrator", DateTime.UtcNow, 30, 5) },
                Members = { new PvwaMember(987001, "Safe One", 2, "Administrator", false, null, new Dictionary<string, bool> { ["listAccounts"] = true }) },
                Users = { new PvwaUser(2, "Administrator", "CyberArk", "Built-InAdmins", "\\", true, "A", "B", "a@b.com") },
                Groups = { new PvwaGroup(9, "Auditors", "Vault", "\\", null, [2]) },
                Platforms = { new PvwaPlatform("UnixSSH", "Unix via SSH", null, true, "Regular") }
            };
            for (var i = 1; i <= accounts; i++)
            {
                s.Accounts.Add(new PvwaAccount(987001, i, "Safe One", $"acct{i}", "host", "root", "UnixSSH", DateTime.UtcNow, null, null, true, null,
                    new Dictionary<string, string> { ["LogonDomain"] = "CORP" }));
            }
            return s;
        }

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        async Task<(int Safes, int Owners, int Users, int Groups, int Members, int Files, int Props, int Platforms)> Counts() =>
            await connection.QuerySingleAsync<(int, int, int, int, int, int, int, int)>("""
                SELECT (SELECT COUNT(*) FROM stg_sh_safes), (SELECT COUNT(*) FROM stg_sh_owners), (SELECT COUNT(*) FROM stg_sh_users),
                       (SELECT COUNT(*) FROM stg_sh_groups), (SELECT COUNT(*) FROM stg_sh_groupmembers), (SELECT COUNT(*) FROM stg_sh_files),
                       (SELECT COUNT(*) FROM stg_sh_objectproperties), (SELECT COUNT(*) FROM stg_sh_platforms)
                """);
        try
        {
            await repository.StageAsync(new SelfHostedStagingMapper().Map(Snapshot(3), batch, "PVWA REST (test)"), CancellationToken.None);
            // 3 accounts x (UserName, Address, PolicyID, LogonDomain)
            Assert.Equal((1, 1, 1, 1, 1, 3, 12, 1), await Counts());

            // A second pull replaces the first; it doesn't add to it.
            await repository.StageAsync(new SelfHostedStagingMapper().Map(Snapshot(1), Guid.NewGuid(), "PVWA REST (test)"), CancellationToken.None);
            Assert.Equal((1, 1, 1, 1, 1, 1, 4, 1), await Counts());

            // The keys the Load joins on are the EVD's: safe id, SafeID_FileID account ids, owner id and type.
            var file = await connection.QuerySingleAsync<(long SafeId, long FileId, int Type)>("SELECT CAFSafeID, CAFFileID, CAFType FROM stg_sh_files");
            Assert.Equal((987001L, 1L, 2), file);
            var owner = await connection.QuerySingleAsync<(long SafeId, long OwnerId, int Type, string List)>("SELECT CAOSafeID, CAOOwnerID, CAOOwnerType, CAOList FROM stg_sh_owners");
            Assert.Equal((987001L, 2L, 0, "YES"), owner);
            Assert.Equal("PVWA REST (test)", await connection.QuerySingleAsync<string>("SELECT TOP (1) SourceFileName FROM stg_sh_safes"));
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM stg_sh_users; DELETE FROM stg_sh_groups; DELETE FROM stg_sh_groupmembers; DELETE FROM stg_sh_safes; DELETE FROM stg_sh_owners;
                DELETE FROM stg_sh_files; DELETE FROM stg_sh_objectproperties; DELETE FROM stg_sh_platforms;
                """);
        }
    }

    [Fact]
    public async Task StageAsync_WhenARowCantBeWritten_RollsBackAndKeepsThePreviousStaging()
    {
        var repository = new SelfHostedDirectRepository(new TestDbConnectionFactory());
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        try
        {
            await connection.ExecuteAsync("INSERT INTO stg_sh_safes (ImportBatchId, SourceFileName, CASSafeID, CASSafeName) VALUES (NEWID(), 'old', 987099, 'OldSafe')");
            var snapshot = new PvwaSnapshot { Safes = { new PvwaSafe(987001, "S", "S", "\\", null, null, null, null) } };
            var tables = new SelfHostedStagingMapper().Map(snapshot, Guid.NewGuid(), "PVWA REST (test)");
            tables.Users.Rows.Add(Guid.NewGuid(), new string('x', 300), 1L, "u", DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, "NO", DBNull.Value, DBNull.Value);   // SourceFileName is NVARCHAR(260)

            await Assert.ThrowsAnyAsync<Exception>(() => repository.StageAsync(tables, CancellationToken.None));

            Assert.Equal(["OldSafe"], (await connection.QueryAsync<string>("SELECT CASSafeName FROM stg_sh_safes")).ToList());
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM stg_sh_safes; DELETE FROM stg_sh_users;");
        }
    }

    [Fact]
    public async Task PullLog_StartCompleteAndLastSucceeded_AndAStalledStartIsMarkedFailed()
    {
        var repository = new SelfHostedDirectRepository(new TestDbConnectionFactory());
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var maxBefore = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(PullKey), 0) FROM dbo.cyberark_direct_pull");
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO dbo.cyberark_direct_pull (StartedAt, Status, TriggeredBy) VALUES (DATEADD(HOUR, -5, SYSUTCDATETIME()), 'Started', 'old crash')
                """);

            var key = await repository.StartPullAsync("tester", acceptedChange: true);

            var stalled = await connection.QuerySingleAsync<(string Status, string Message)>(
                "SELECT Status, Message FROM dbo.cyberark_direct_pull WHERE TriggeredBy = 'old crash' AND PullKey > @maxBefore", new { maxBefore });
            Assert.Equal("Failed", stalled.Status);
            Assert.Contains("never finished", stalled.Message);

            await repository.CompleteAsync(key, "Succeeded", new PullCounts(40, 2452, 43, 21, 700, 20), null, Guid.NewGuid());
            var last = await repository.GetLastSucceededAsync();
            Assert.NotNull(last);
            Assert.Equal((key, 40, 2452, true, "tester"), (last.PullKey, last.SafeCount, last.AccountCount, last.AcceptedChange, last.TriggeredBy));

            var rejectedKey = await repository.StartPullAsync("schedule", acceptedChange: false);
            await repository.CompleteAsync(rejectedKey, "Rejected", new PullCounts(1, 1, 1, 1, 1, 1), new string('m', 2500), null);
            Assert.Equal(key, (await repository.GetLastSucceededAsync())!.PullKey);   // a rejected pull isn't the baseline
            var recent = await repository.GetRecentAsync(2);
            Assert.Equal(rejectedKey, recent[0].PullKey);
            Assert.Equal(2000, recent[0].Message!.Length);   // cut to the column
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.cyberark_direct_pull WHERE PullKey > @maxBefore", new { maxBefore });
        }
    }

    // ---- D-250 ----

    [Fact]
    public async Task GetRecent_GivesEachPullTheCountsOfTheLastSuccessBeforeIt()
    {
        var repository = new SelfHostedDirectRepository(new TestDbConnectionFactory());
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var maxBefore = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(PullKey), 0) FROM dbo.cyberark_direct_pull");
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO dbo.cyberark_direct_pull (Status, CompletedAt, TriggeredBy, SafeCount, AccountCount) VALUES ('Succeeded', SYSUTCDATETIME(), 't', 40, 2452);
                INSERT INTO dbo.cyberark_direct_pull (Status, CompletedAt, TriggeredBy, SafeCount, AccountCount) VALUES ('Rejected', SYSUTCDATETIME(), 't', 10, 100);
                INSERT INTO dbo.cyberark_direct_pull (Status, CompletedAt, TriggeredBy, SafeCount, AccountCount) VALUES ('Succeeded', SYSUTCDATETIME(), 't', 42, 2460);
                INSERT INTO dbo.cyberark_direct_pull (Status, CompletedAt, TriggeredBy) VALUES ('Failed', SYSUTCDATETIME(), 't');
                """);

            var recent = (await repository.GetRecentAsync(4)).Select(p => (p.Status, p.BaselineSafeCount, p.BaselineAccountCount)).ToList();

            Assert.Equal(
                [("Failed", (int?)42, (int?)2460), ("Succeeded", 40, 2452), ("Rejected", 40, 2452), ("Succeeded", null, null)],
                recent);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.cyberark_direct_pull WHERE PullKey > @maxBefore", new { maxBefore });
        }
    }

    [Fact]
    public async Task GetStagedSafes_ReturnsTheSafesTheLastPullStaged()
    {
        var repository = new SelfHostedDirectRepository(new TestDbConnectionFactory());
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        try
        {
            await connection.ExecuteAsync("""
                DELETE FROM stg_sh_safes;
                INSERT INTO stg_sh_safes (ImportBatchId, SourceFileName, CASSafeID, CASSafeName) VALUES (NEWID(), 'x', 987001, 'One'), (NEWID(), 'x', 987002, 'Two');
                """);

            var safes = await repository.GetStagedSafesAsync();

            Assert.Equal([(987001L, "One"), (987002L, "Two")], safes.OrderBy(s => s.Number));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM stg_sh_safes");
        }
    }

    [Fact]
    public async Task CredentialBackendCheck_CpNeedsAnAppId_CcpNeedsABaseUrlAndAnAppId_OthersNeedNothing()
    {
        var repository = new BlueTrack.Api.Data.SecretsStoreRepository(new TestDbConnectionFactory(), new BlueTrack.Api.Secrets.WindowsDpapiProtector());
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var saved = (await connection.QueryAsync<(string BackendType, string? Settings)>(
            "SELECT BackendType, BackendSettings FROM web.secrets_store WHERE BackendType IN ('CyberArkCP', 'CyberArkCCP')")).ToList();
        async Task Set(string type, string? settings) =>
            await connection.ExecuteAsync("UPDATE web.secrets_store SET BackendSettings = @settings WHERE BackendType = @type", new { type, settings });
        try
        {
            await Set("CyberArkCP", null);
            await Set("CyberArkCCP", """{"BaseUrl":"https://ccp.example.com","AppId":""}""");

            var cpNone = await BlueTrack.Api.Secrets.CredentialBackendCheck.CheckAsync(repository, "CyberArkCP");
            Assert.Equal(false, cpNone.Ready);
            Assert.Contains("no AppID", cpNone.Note);
            Assert.Contains("Admin > Secrets Store", cpNone.Note);
            Assert.Equal(false, (await BlueTrack.Api.Secrets.CredentialBackendCheck.CheckAsync(repository, "CyberArkCCP")).Ready);

            await Set("CyberArkCP", """{"AppId":"APP_Test"}""");
            await Set("CyberArkCCP", """{"BaseUrl":"https://ccp.example.com","AppId":"APP_Test"}""");
            Assert.Equal((true, (string?)null), await BlueTrack.Api.Secrets.CredentialBackendCheck.CheckAsync(repository, "CyberArkCP"));
            Assert.Equal((true, (string?)null), await BlueTrack.Api.Secrets.CredentialBackendCheck.CheckAsync(repository, "CyberArkCCP"));

            await Set("CyberArkCP", "not json");
            Assert.Equal(false, (await BlueTrack.Api.Secrets.CredentialBackendCheck.CheckAsync(repository, "CyberArkCP")).Ready);

            Assert.Equal((null, null), await BlueTrack.Api.Secrets.CredentialBackendCheck.CheckAsync(repository, "WindowsDpapi"));
            Assert.Equal((null, null), await BlueTrack.Api.Secrets.CredentialBackendCheck.CheckAsync(repository, "AzureKeyVault"));
        }
        finally
        {
            foreach (var (type, settings) in saved) await Set(type, settings);
        }
    }

    [Fact]
    public async Task Credentials_ListWhatUsesThem()
    {
        var credentials = new BlueTrack.Api.Data.CredentialRepository(
            new TestDbConnectionFactory(), new BlueTrack.Api.Secrets.WindowsDpapiProtector(), null!, null!);
        var key = await credentials.CreateAsync(new BlueTrack.Api.Models.SaveCredentialRequest { CredentialName = "UsedBy" + Guid.NewGuid().ToString("N")[..8], BackendType = "CyberArkCP", VaultSafe = "S", VaultFolder = "", VaultObject = "O" }, null);
        var unused = await credentials.CreateAsync(new BlueTrack.Api.Models.SaveCredentialRequest { CredentialName = "Unused" + Guid.NewGuid().ToString("N")[..8], BackendType = "CyberArkCP", VaultSafe = "S", VaultFolder = "", VaultObject = "O" }, null);
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var domain = "usedby" + Guid.NewGuid().ToString("N")[..6] + ".test";
        try
        {
            await connection.ExecuteAsync("INSERT INTO web.ldap_config (IsEnabled, DomainName, CredentialKey) VALUES (0, @domain, @key)", new { domain, key });

            var all = await credentials.GetAllAsync();

            Assert.Equal([$"LDAP configuration ({domain})"], all.Single(c => c.CredentialKey == key).UsedBy);
            Assert.Empty(all.Single(c => c.CredentialKey == unused).UsedBy);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.ldap_config WHERE DomainName = @domain; DELETE FROM web.credential WHERE CredentialKey IN (@key, @unused)", new { domain, key, unused });
        }
    }
}
