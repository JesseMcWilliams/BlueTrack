using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-222 (script 19): usp_Import_Configured records each run in
/// dbo.etl_run_log, including a failure before any file is imported --
/// here, no CyberArk source settings at all.
/// </summary>
public class EtlRunLogTests
{
    [Fact]
    public async Task ImportConfigured_WithNoSourceSettings_LogsAFailedImportRun_AndStillThrows()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var saved = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT * FROM web.cyberark_source_config WHERE CyberArkSourceConfigKey = 1 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER");
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(EtlRunKey), 0) FROM dbo.etl_run_log");
        try
        {
            await connection.ExecuteAsync("DELETE FROM web.cyberark_source_config");

            var error = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("EXEC dbo.usp_Import_Configured"));
            Assert.Contains("no CyberArk source settings", error.Message);

            var run = await connection.QuerySingleAsync<(string StepName, string Status, string ErrorMessage, string ErrorProcedure, DateTime? CompletedAt)>(
                "SELECT StepName, Status, ErrorMessage, ErrorProcedure, CompletedAt FROM dbo.etl_run_log WHERE EtlRunKey > @before", new { before });
            Assert.Equal("Import", run.StepName);
            Assert.Equal("Failed", run.Status);
            Assert.Contains("no CyberArk source settings", run.ErrorMessage);
            Assert.Contains("usp_Import_Configured", run.ErrorProcedure);
            Assert.NotNull(run.CompletedAt);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.etl_run_log WHERE EtlRunKey > @before", new { before });
            await connection.ExecuteAsync("DELETE FROM web.cyberark_source_config");
            if (saved is not null)
            {
                await connection.ExecuteAsync("""
                    INSERT INTO web.cyberark_source_config
                    SELECT * FROM OPENJSON(@json) WITH (
                        CyberArkSourceConfigKey INT, ImportPrivilegeCloud BIT, ExportFolderPath NVARCHAR(400),
                        PlatformsFileName NVARCHAR(200), UsersFileName NVARCHAR(200), GroupsFileName NVARCHAR(200),
                        GroupMembersFileName NVARCHAR(200), SafesFileName NVARCHAR(200), AccountsFileName NVARCHAR(200),
                        EntitlementsFileName NVARCHAR(200), ImportSelfHosted BIT, EvdDatabaseName NVARCHAR(128),
                        ModifiedBy INT, ModifiedDate DATETIME2)
                    """, new { json = saved });
            }
        }
    }
}
