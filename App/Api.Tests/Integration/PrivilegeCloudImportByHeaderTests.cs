using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-228 (22_BlueTrack_PcImportByHeader.sql): the Privilege Cloud import reads
/// each CSV's columns by header name, so the old and the new Users/Groups
/// layouts both load, unknown columns are ignored, and a file without a
/// required column fails naming the file and the column.
///
/// The files are synthetic exports in Fixtures/PrivCloudImport, copied to the
/// test output folder, which SQL Server reads with BULK INSERT as the
/// connecting Windows account (BUILTIN\Users can read the repo and the CI
/// runner's work folder). Each test runs in a transaction it rolls back, so
/// staging, the dimensions, import_log, etl_run_log and the source settings
/// are left as they were.
/// </summary>
public class PrivilegeCloudImportByHeaderTests
{
    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PrivCloudImport");

    private static string Fixture(string name) => Path.Combine(FixtureFolder, name);

    private sealed record UserRow(string UserID, string Username, string? Email, bool? ComponentUser, bool? Enabled, bool? Suspended, string? Location);

    private static async Task<(SqlConnection Connection, SqlTransaction Transaction)> BeginAsync()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        return (connection, (SqlTransaction)await connection.BeginTransactionAsync());
    }

    private static async Task<List<UserRow>> StagedUsersAsync(IDbConnection connection, IDbTransaction transaction) =>
        (await connection.QueryAsync<UserRow>(
            "SELECT UserID, Username, Email, ComponentUser, Enabled, Suspended, Location FROM stg_pc_users ORDER BY UserID",
            transaction: transaction)).ToList();

    [Fact]
    public async Task Users_OldLayout_LoadsAsBefore()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        await connection.ExecuteAsync("EXEC dbo.usp_Import_PC_Users @FilePath = @path", new { path = Fixture("Users_OldLayout.csv") }, transaction);

        var users = await StagedUsersAsync(connection, transaction);
        Assert.Equal(2, users.Count);
        Assert.Equal(new UserRow("FX-U1", "fixture.alpha", "alpha@fixture.test", false, null, null, null), users[0]);
        Assert.Equal("fixture.bravo, jr", users[1].Username);   // quoted, with an embedded comma
        Assert.Null(users[1].Email);                            // empty field
        Assert.True(users[1].ComponentUser);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Users_NewLayout_LoadsByHeaderName_AndParsesEnabledAndSuspended()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        await connection.ExecuteAsync("EXEC dbo.usp_Import_PC_Users @FilePath = @path", new { path = Fixture("Users_NewLayout.csv") }, transaction);

        var users = (await StagedUsersAsync(connection, transaction)).ToDictionary(u => u.UserID);
        Assert.Equal(5, users.Count);
        Assert.All(users.Values, u => Assert.Null(u.Email));   // no Email column any more
        Assert.Equal(new UserRow("FX-U1", "fixture.alpha", null, false, true, false, "\\"), users["FX-U1"]);
        Assert.Equal((false, true, "\\Applications, Shared"), (users["FX-U2"].Enabled, users["FX-U2"].Suspended, users["FX-U2"].Location));
        Assert.Equal((true, false), (users["FX-U3"].Enabled, users["FX-U3"].Suspended));   // Yes / No
        Assert.Equal((true, false), (users["FX-U4"].Enabled, users["FX-U4"].Suspended));   // 1 / 0
        Assert.Equal((null, null), (users["FX-U5"].Enabled, users["FX-U5"].Suspended));    // "maybe" / empty
        Assert.Null(users["FX-U5"].Location);

        var log = await connection.QuerySingleAsync<(string Status, int? RowsLoaded)>(
            "SELECT TOP (1) Status, RowsLoaded FROM import_log WHERE SourceFileName = 'Users_NewLayout.csv' ORDER BY StartedAt DESC",
            transaction: transaction);
        Assert.Equal(("Succeeded", (int?)5), log);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Users_WithBomReorderedAndUnknownColumns_LoadsTheKnownColumnsOnly()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        await connection.ExecuteAsync("EXEC dbo.usp_Import_PC_Users @FilePath = @path", new { path = Fixture("Users_BomExtraColumns.csv") }, transaction);

        var users = await StagedUsersAsync(connection, transaction);
        Assert.Equal(2, users.Count);
        // "USERNAME" matched Username despite the case; "" inside quotes is a quote.
        Assert.Equal(new UserRow("FX-U6", "fixture.foxtrot, \"the\" admin", "foxtrot@fixture.test", null, true, null, null), users[0]);
        Assert.Equal(new UserRow("FX-U7", "fixture.golf", null, null, false, null, null), users[1]);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Users_MissingARequiredColumn_FailsNamingTheFileAndColumn_AndLogsIt()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync(
            "EXEC dbo.usp_Import_PC_Users @FilePath = @path", new { path = Fixture("Users_MissingRequired.csv") }, transaction));
        Assert.Contains("Users file 'Users_MissingRequired.csv' is missing the required column(s): Username.", error.Message);

        var log = await connection.QuerySingleAsync<(string Status, string ErrorMessage)>(
            "SELECT TOP (1) Status, ErrorMessage FROM import_log WHERE SourceFileName = 'Users_MissingRequired.csv' ORDER BY StartedAt DESC",
            transaction: transaction);
        Assert.Equal("Failed", log.Status);
        Assert.Contains("missing the required column(s): Username", log.ErrorMessage);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("Groups_OldLayout.csv", 1, null)]
    [InlineData("Groups_NewLayout.csv", 2, "fixture.test")]
    public async Task Groups_OldAndNewLayouts_Load(string file, int rows, string? directoryOfG2)
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        await connection.ExecuteAsync("EXEC dbo.usp_Import_PC_Groups @FilePath = @path", new { path = Fixture(file) }, transaction);

        var groups = (await connection.QueryAsync<(string GroupID, string Description, string? Directory)>(
            "SELECT GroupID, Description, Directory FROM stg_pc_groups ORDER BY GroupID", transaction: transaction)).ToList();
        Assert.Equal(rows, groups.Count);
        Assert.Equal("Admins, all of them", groups[0].Description);
        Assert.Null(groups[0].Directory);
        if (rows == 2) Assert.Equal(directoryOfG2, groups[1].Directory);
        await transaction.RollbackAsync();
    }

    private static Task SetSourcesAsync(IDbConnection connection, IDbTransaction transaction, string usersFile) =>
        connection.ExecuteAsync("""
            DELETE FROM web.cyberark_source_config;
            INSERT INTO web.cyberark_source_config
                (CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, PlatformsFileName, UsersFileName, GroupsFileName,
                 GroupMembersFileName, SafesFileName, AccountsFileName, EntitlementsFileName, ImportSelfHosted, EvdDatabaseName)
            VALUES (1, 1, @folder, 'Platforms.csv', @usersFile, 'Groups_NewLayout.csv',
                    'GroupMembers.csv', 'Safes.csv', 'Accounts.csv', 'Entitlements.csv', 0, NULL);
            """, new { folder = FixtureFolder, usersFile }, transaction);

    [Fact]
    public async Task ImportConfigured_AllSevenFiles_Succeeds_AndTheDimensionsGetTheNewColumns()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(EtlRunKey), 0) FROM dbo.etl_run_log", transaction: transaction);
        await SetSourcesAsync(connection, transaction, "Users_NewLayout.csv");

        await connection.ExecuteAsync("EXEC dbo.usp_Import_Configured", transaction: transaction);

        var run = await connection.QuerySingleAsync<string>("SELECT Status FROM dbo.etl_run_log WHERE EtlRunKey > @before", new { before }, transaction);
        Assert.Equal("Succeeded", run);
        var counts = await connection.QuerySingleAsync<(int P, int U, int G, int M, int S, int A, int E)>("""
            SELECT (SELECT COUNT(*) FROM stg_pc_platforms), (SELECT COUNT(*) FROM stg_pc_users), (SELECT COUNT(*) FROM stg_pc_groups),
                   (SELECT COUNT(*) FROM stg_pc_groupmembers), (SELECT COUNT(*) FROM stg_pc_safes), (SELECT COUNT(*) FROM stg_pc_accounts),
                   (SELECT COUNT(*) FROM stg_pc_entitlements)
            """, transaction: transaction);
        Assert.Equal((1, 5, 2, 1, 1, 1, 1), counts);
        var account = await connection.QuerySingleAsync<(string RemoteMachines, bool AutoManaged, DateTime LastVerified)>(
            "SELECT RemoteMachines, AutoManaged, LastVerified FROM stg_pc_accounts", transaction: transaction);
        Assert.Equal(("host1,host2", true, new DateTime(2026, 1, 5)), account);

        await connection.ExecuteAsync("EXEC dbo.usp_Load_DimUser; EXEC dbo.usp_Load_DimGroup;", transaction: transaction);
        var user = await connection.QuerySingleAsync<(bool? Enabled, bool? Suspended, string? Location)>("""
            SELECT u.Enabled, u.Suspended, u.Location FROM dbo.dim_user u
            JOIN dbo.dim_source_system s ON s.SourceSystemKey = u.SourceSystemKey AND s.SourceSystemCode = 'PRIVCLOUD'
            WHERE u.SourceUserId = 'FX-U2'
            """, transaction: transaction);
        Assert.Equal((false, true, "\\Applications, Shared"), user);
        var directory = await connection.QuerySingleAsync<string?>("""
            SELECT g.Directory FROM dbo.dim_group g
            JOIN dbo.dim_source_system s ON s.SourceSystemKey = g.SourceSystemKey AND s.SourceSystemCode = 'PRIVCLOUD'
            WHERE g.SourceGroupId = 'FX-G2'
            """, transaction: transaction);
        Assert.Equal("fixture.test", directory);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ImportConfigured_UsersFileMissingARequiredColumn_FailsTheRun_AndEtlRunLogNamesTheColumn()
    {
        var (connection, transaction) = await BeginAsync();
        await using var _ = connection;
        var before = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(EtlRunKey), 0) FROM dbo.etl_run_log", transaction: transaction);
        await SetSourcesAsync(connection, transaction, "Users_MissingRequired.csv");

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("EXEC dbo.usp_Import_Configured", transaction: transaction));
        Assert.Contains("missing the required column(s): Username", error.Message);

        var run = await connection.QuerySingleAsync<(string Status, string ErrorMessage)>(
            "SELECT Status, ErrorMessage FROM dbo.etl_run_log WHERE EtlRunKey > @before", new { before }, transaction);
        Assert.Equal("Failed", run.Status);
        Assert.Contains("Users file 'Users_MissingRequired.csv' is missing the required column(s): Username", run.ErrorMessage);
        await transaction.RollbackAsync();
    }

    /// <summary>The column list names only real staging columns, and every staging data column is listed.</summary>
    [Fact]
    public async Task ImportColumnList_MatchesTheStagingTables()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var mismatches = (await connection.QueryAsync<string>("""
            WITH kinds AS (
                SELECT * FROM (VALUES (N'Platforms', N'stg_pc_platforms'), (N'Users', N'stg_pc_users'), (N'Groups', N'stg_pc_groups'),
                                      (N'GroupMembers', N'stg_pc_groupmembers'), (N'Safes', N'stg_pc_safes'),
                                      (N'Accounts', N'stg_pc_accounts'), (N'Entitlements', N'stg_pc_entitlements')) k (FileKind, TableName)),
            staged AS (
                SELECT k.FileKind, c.name AS ColumnName
                FROM kinds k JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + k.TableName)
                WHERE c.name NOT IN (N'LoadId', N'ImportBatchId', N'SourceFileName', N'LoadTimestamp', N'ExportDate'))
            SELECT CONCAT(COALESCE(s.FileKind, ic.FileKind), '.', COALESCE(s.ColumnName, ic.ColumnName))
            FROM staged s FULL JOIN web.cyberark_import_column ic ON ic.FileKind = s.FileKind AND ic.ColumnName = s.ColumnName
            WHERE s.ColumnName IS NULL OR ic.ColumnName IS NULL
            """)).ToList();
        Assert.Empty(mismatches);
    }
}
