using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-224 (script 21): the daily snapshot behind Reports > Trends is taken
/// even when the nightly Import fails (usp_RunNightlyImportLoad, the SQL
/// Agent job's one step), is recorded in dbo.etl_run_log as 'Snapshot', and
/// still replaces today's snapshot when run twice. The failing import is
/// simulated as in EtlRunLogTests: no CyberArk source settings at all.
/// Today's snapshot is removed afterwards, as TrendsMatchesKpiSummaryTests does.
/// </summary>
public class DailySnapshotTests : IAsyncLifetime
{
    private static int TodayKey => int.Parse(DateTime.Now.ToString("yyyyMMdd"));
    private long _before;
    private string? _savedSourceConfig;

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        _before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(EtlRunKey), 0) FROM dbo.etl_run_log");
        _savedSourceConfig = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT * FROM web.cyberark_source_config WHERE CyberArkSourceConfigKey = 1 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER");
        await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE SnapshotDateKey = @TodayKey", new { TodayKey });
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM dbo.etl_run_log WHERE EtlRunKey > @before", new { before = _before });
        await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE SnapshotDateKey = @TodayKey", new { TodayKey });
        await connection.ExecuteAsync("DELETE FROM web.cyberark_source_config");
        if (_savedSourceConfig is not null)
        {
            await connection.ExecuteAsync("""
                INSERT INTO web.cyberark_source_config
                SELECT * FROM OPENJSON(@json) WITH (
                    CyberArkSourceConfigKey INT, ImportPrivilegeCloud BIT, ExportFolderPath NVARCHAR(400),
                    PlatformsFileName NVARCHAR(200), UsersFileName NVARCHAR(200), GroupsFileName NVARCHAR(200),
                    GroupMembersFileName NVARCHAR(200), SafesFileName NVARCHAR(200), AccountsFileName NVARCHAR(200),
                    EntitlementsFileName NVARCHAR(200), ImportSelfHosted BIT, EvdDatabaseName NVARCHAR(128),
                    ModifiedBy INT, ModifiedDate DATETIME2)
                """, new { json = _savedSourceConfig });
        }
    }

    private sealed record Run(string StepName, string Status, string? ErrorMessage);

    private async Task<List<Run>> RunsSinceStartAsync(SqlConnection connection) =>
        (await connection.QueryAsync<Run>(
            "SELECT StepName, Status, ErrorMessage FROM dbo.etl_run_log WHERE EtlRunKey > @before ORDER BY EtlRunKey", new { before = _before })).AsList();

    private static Task<int> ExpectedSnapshotRowsAsync(SqlConnection connection) =>
        connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM dbo.fact_account_progress fap
            JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey
            WHERE fa.IsDeleted = 0
              AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)
            """);

    private static Task<int> TodaysSnapshotRowsAsync(SqlConnection connection) =>
        connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.fact_account_progress_history WHERE SnapshotDateKey = @TodayKey", new { TodayKey });

    [Fact]
    public async Task NightlyRun_WhenTheImportFails_SkipsTheLoad_StillTakesTheSnapshot_AndRethrowsTheImportError()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM web.cyberark_source_config");

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("EXEC dbo.usp_RunNightlyImportLoad"));
        Assert.Contains("no CyberArk source settings", error.Message);

        var runs = await RunsSinceStartAsync(connection);
        Assert.Equal(["Import", "Snapshot"], runs.Select(r => r.StepName));
        Assert.Equal("Failed", runs[0].Status);
        Assert.Equal("Succeeded", runs[1].Status);
        Assert.Null(runs[1].ErrorMessage);

        Assert.True(await ExpectedSnapshotRowsAsync(connection) > 0);
        Assert.Equal(await ExpectedSnapshotRowsAsync(connection), await TodaysSnapshotRowsAsync(connection));
    }

    [Fact]
    public async Task DailySnapshot_TwiceInADay_ReplacesTodaysSnapshot_AndLogsEachRun()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);

        await connection.ExecuteAsync("EXEC dbo.usp_RunDailySnapshot");
        await connection.ExecuteAsync("EXEC dbo.usp_RunDailySnapshot");

        Assert.Equal(await ExpectedSnapshotRowsAsync(connection), await TodaysSnapshotRowsAsync(connection));
        var runs = await RunsSinceStartAsync(connection);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(("Snapshot", "Succeeded"), (r.StepName, r.Status)));
    }

    [Fact]
    public async Task RunFullLoad_NoLongerSnapshotsInsideItsTransaction_TheNightlyJobDoes()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var definitions = (await connection.QueryAsync<(string Name, string Definition)>("""
            SELECT OBJECT_NAME(object_id), definition FROM sys.sql_modules
            WHERE object_id IN (OBJECT_ID('dbo.usp_RunFullLoad'), OBJECT_ID('dbo.usp_RunNightlyImportLoad'))
            """)).ToDictionary(d => d.Name, d => d.Definition);

        Assert.DoesNotContain("EXEC usp_Load_FactAccountProgressHistory", definitions["usp_RunFullLoad"]);
        Assert.Contains("@TakeSnapshot = 0", definitions["usp_RunNightlyImportLoad"]);
    }

    [Fact]
    public async Task EtlRunLog_AcceptsTheSnapshotStep_ButNoOtherNewName()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("INSERT INTO dbo.etl_run_log (StepName) VALUES ('Snapshot')");
        await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("INSERT INTO dbo.etl_run_log (StepName) VALUES ('Other')"));
    }
}
