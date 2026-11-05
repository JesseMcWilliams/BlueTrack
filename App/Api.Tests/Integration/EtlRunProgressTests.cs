using System.Text.RegularExpressions;
using BlueTrack.Api.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-337: the Load logs each procedure (dbo.etl_run_step), keeps that log when it fails and rolls back, and
/// dbo.usp_Get_EtlSessionActivity says what a run's session is waiting on, signed so a login without VIEW SERVER STATE
/// can see it (Database/Manual/10). These need a sysadmin connection (the test database's, as on CI).
/// </summary>
public class EtlRunProgressTests
{
    private static SqlConnection Open() => new(TestDatabase.ConnectionString);

    [Fact]
    public async Task TheLoad_LogsEachProcedure_InOrder()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();   // undone below: the test database keeps its data
        await connection.ExecuteAsync("EXEC dbo.usp_RunFullLoad @TakeSnapshot = 0", transaction: transaction, commandTimeout: 300);

        var steps = (await connection.QueryAsync<(int StepOrder, string ProcName, string Status, bool Ended)>("""
            SELECT StepOrder, ProcName, Status, CAST(CASE WHEN CompletedAt IS NULL THEN 0 ELSE 1 END AS BIT)
            FROM dbo.etl_run_step WHERE EtlRunKey = (SELECT MAX(EtlRunKey) FROM dbo.etl_run_log WHERE StepName = 'Load')
            ORDER BY StepOrder
            """, transaction: transaction)).ToList();
        await transaction.RollbackAsync();

        Assert.Equal(20, steps.Count);
        Assert.Equal(("dbo.usp_Load_DimLocation", "dbo.usp_RecalculateRiskScores"), (steps[0].ProcName, steps[^1].ProcName));
        Assert.Equal(Enumerable.Range(1, 20), steps.Select(s => s.StepOrder));
        Assert.All(steps, s => Assert.Equal(("Succeeded", true), (s.Status, s.Ended)));
    }

    [Fact]
    public async Task ALoadThatFails_KeepsItsSteps_AfterItsRollback()
    {
        // Step 19 of 20 is swapped for one that fails: the real one is renamed aside and renamed back afterwards, so
        // its definition is never rewritten.
        const string broken = "dbo.usp_DeriveAccountTargetMap_FromAddress";
        const string aside = "usp_DeriveAccountTargetMap_FromAddress_D337Test";
        await using var connection = Open();
        var maxRun = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(EtlRunKey), 0) FROM dbo.etl_run_log");
        await connection.ExecuteAsync($"EXEC sp_rename '{broken}', '{aside}';");
        try
        {
            await connection.ExecuteAsync($"CREATE PROCEDURE {broken} AS THROW 50000, 'D-337 test: this step fails.', 1;");
            var ex = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("EXEC dbo.usp_RunFullLoad @TakeSnapshot = 0", commandTimeout: 300));
            Assert.Contains("this step fails", ex.Message);

            var run = await connection.QuerySingleAsync<(long Key, string Status, string ErrorProcedure)>(
                "SELECT EtlRunKey, Status, ErrorProcedure FROM dbo.etl_run_log WHERE EtlRunKey > @maxRun AND StepName = 'Load'", new { maxRun });
            Assert.Equal("Failed", run.Status);
            Assert.EndsWith("usp_DeriveAccountTargetMap_FromAddress", run.ErrorProcedure);
            var steps = (await connection.QueryAsync<(string ProcName, string Status)>(
                "SELECT ProcName, Status FROM dbo.etl_run_step WHERE EtlRunKey = @key ORDER BY StepOrder", new { key = run.Key })).ToList();
            Assert.Equal(19, steps.Count);   // the 20th never started
            Assert.All(steps.Take(18), s => Assert.Equal("RolledBack", s.Status));
            Assert.Equal((broken, "Failed"), steps[^1]);
        }
        finally
        {
            await connection.ExecuteAsync($"DROP PROCEDURE IF EXISTS {broken}; EXEC sp_rename 'dbo.{aside}', 'usp_DeriveAccountTargetMap_FromAddress';");
            await connection.ExecuteAsync("DELETE FROM dbo.etl_run_log WHERE EtlRunKey > @maxRun", new { maxRun });
        }
    }

    [Fact]
    public async Task ARunningStep_ShowsWhatItsSessionWaitsOn_AndWhoBlocksIt()
    {
        var table = $"##bt_lock_{Guid.NewGuid():N}";
        await using var blocker = Open();
        await blocker.OpenAsync();
        await blocker.ExecuteAsync($"CREATE TABLE {table} (Id INT PRIMARY KEY, V INT); INSERT INTO {table} VALUES (1, 0);");
        var blockerSession = await blocker.QuerySingleAsync<int>("SELECT @@SPID");
        await using var hold = (SqlTransaction)await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync($"UPDATE {table} SET V = 1 WHERE Id = 1", transaction: hold);

        await using var load = Open();
        await load.OpenAsync();
        var runKey = await load.QuerySingleAsync<long>("INSERT INTO dbo.etl_run_log (StepName) VALUES ('Load'); SELECT CAST(SCOPE_IDENTITY() AS BIGINT);");
        var blocked = load.ExecuteAsync($"UPDATE {table} SET V = 2 WHERE Id = 1", commandTimeout: 60);
        try
        {
            var repository = new EtlRunRepository(new TestDbConnectionFactory());
            Models.EtlSessionActivity? seen = null;
            for (var i = 0; i < 50 && seen?.BlockingSessionId is null; i++)
            {
                await Task.Delay(100);
                seen = (await repository.GetRunningAsync()).SingleOrDefault(r => r.Activity.EtlRunKey == runKey)?.Activity;
            }

            Assert.NotNull(seen);
            Assert.Equal((true, true), (seen.CanSeeSessions, seen.SessionFound));
            Assert.Equal(blockerSession, seen.BlockingSessionId);
            Assert.StartsWith("LCK_M_", seen.WaitType);
            Assert.Equal(("UPDATE", 1), (seen.Command, seen.BlockerOpenTransactions));
            Assert.Contains(table, seen.CurrentStatement);   // the statement waiting (SQL Server shows its values as @1, @2)
        }
        finally
        {
            await hold.RollbackAsync();
            await blocked;
            await load.ExecuteAsync("DELETE FROM dbo.etl_run_log WHERE EtlRunKey = @runKey", new { runKey });
        }
    }

    [Fact]
    public async Task ARunWhoseSessionEnded_IsShownAsSuch()
    {
        await using var connection = Open();
        var runKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO dbo.etl_run_log (StepName, SessionId) VALUES ('Load', 32000); SELECT CAST(SCOPE_IDENTITY() AS BIGINT);");
        try
        {
            var run = (await new EtlRunRepository(new TestDbConnectionFactory()).GetRunningAsync()).Single(r => r.Activity.EtlRunKey == runKey);
            Assert.Equal((true, false), (run.Activity.CanSeeSessions, run.Activity.SessionFound));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.etl_run_log WHERE EtlRunKey = @runKey", new { runKey });
        }
    }

    /// <summary>
    /// The user's choice (D-337): the permission goes to the signed procedure, not to the app's login. A login without
    /// VIEW SERVER STATE sees the sessions through the signed procedure only; unsigned, it gets the runs and no error.
    /// The script is also safe to run again.
    /// </summary>
    [Fact]
    public async Task TheSignedProcedure_LetsALoginWithoutTheServerPermission_SeeSessions()
    {
        var login = $"bt_probe_{Guid.NewGuid():N}"[..20];
        await using var connection = Open();
        await connection.OpenAsync();
        var database = connection.Database;
        var runKey = await connection.QuerySingleAsync<long>("INSERT INTO dbo.etl_run_log (StepName) VALUES ('Load'); SELECT CAST(SCOPE_IDENTITY() AS BIGINT);");
        try
        {
            await connection.ExecuteAsync($"""
                CREATE LOGIN [{login}] WITH PASSWORD = 'ThisIsMy_FAKE_Password6!', CHECK_POLICY = OFF;
                CREATE USER [{login}] FOR LOGIN [{login}];
                GRANT EXECUTE ON dbo.usp_Get_EtlSessionActivity TO [{login}];
                """);

            await RunSigningScriptAsync(connection, database);
            Assert.True(await CanSeeAsync(connection, login, runKey));

            await connection.ExecuteAsync($"DROP SIGNATURE FROM dbo.usp_Get_EtlSessionActivity BY CERTIFICATE [BlueTrack_{database}_SessionActivity];");
            Assert.False(await CanSeeAsync(connection, login, runKey));

            await RunSigningScriptAsync(connection, database);
            Assert.True(await CanSeeAsync(connection, login, runKey));
        }
        finally
        {
            await connection.ExecuteAsync($"""
                DELETE FROM dbo.etl_run_log WHERE EtlRunKey = {runKey};
                IF USER_ID('{login}') IS NOT NULL DROP USER [{login}];
                IF SUSER_ID('{login}') IS NOT NULL DROP LOGIN [{login}];
                """);
        }
    }

    private static async Task<bool> CanSeeAsync(SqlConnection connection, string login, long runKey)
    {
        var rows = (await connection.QueryAsync<Models.EtlSessionActivity>(
            $"EXECUTE AS LOGIN = '{login}'; EXEC dbo.usp_Get_EtlSessionActivity; REVERT;")).ToList();
        return rows.Single(r => r.EtlRunKey == runKey).CanSeeSessions;
    }

    /// <summary>Database/Manual/10 as the installer's sqlcmd runs it: $(DatabaseName) filled in, split at GO.</summary>
    private static async Task RunSigningScriptAsync(SqlConnection connection, string database)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "Database", "Manual", "10_BlueTrack_SignSessionActivity.sql"));
        foreach (var batch in Regex.Split(script.Replace("$(DatabaseName)", database), @"^\s*GO\s*$", RegexOptions.Multiline))
        {
            if (!string.IsNullOrWhiteSpace(batch)) await connection.ExecuteAsync(batch);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Manual"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("The repository root (with Database/Manual) wasn't found above the test folder.");
    }
}
