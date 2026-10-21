using BlueTrack.Api.CyberArkDirect;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-277 (script 45): the Load with Self-Hosted staging from a Direct pull reads the REST-shaped stg_shd_* tables too.
/// A pull is staged through the real mapper and repository (which commit on their own connection); the Load's steps
/// then run in a transaction that is rolled back, and the staging tables are emptied afterwards.
/// </summary>
public class SelfHostedDirectLoadTests
{
    private const long SafeId = 987101;
    private static readonly DateTime Verified = new(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ChangeFailed = new(2026, 10, 19, 4, 0, 0, DateTimeKind.Utc);

    private static PvwaSnapshot Snapshot()
    {
        var all = new Dictionary<string, bool> { ["listAccounts"] = true, ["viewAuditLog"] = true, ["useAccounts"] = false, ["moveAccountsAndFolders"] = true };
        return new PvwaSnapshot
        {
            Safes = { new PvwaSafe(SafeId, "Direct%20Safe", "Direct Safe", "\\Applications", "Administrator", DateTime.UtcNow, 30, 5) },
            Accounts =
            {
                new PvwaAccount(SafeId, 1, "Direct Safe", "acct-1", "host1", "root", "UnixSSH",
                    new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc), new DateTime(2026, 10, 19, 4, 0, 0, DateTimeKind.Utc), null,
                    false, "Locked by the business", new Dictionary<string, string> { ["LogonDomain"] = "CORP" })
                {
                    SecretType = "password", CpmStatus = " Failure ", LastReconciledTime = new DateTime(2026, 10, 10, 1, 0, 0, DateTimeKind.Utc),
                    AccessRestrictedToRemoteMachines = true,
                    CpmActivity =
                    [
                        new PvwaCpmActivity(22, "CPM Verify Password", Verified, "PasswordManager", null, null),
                        new PvwaCpmActivity(57, "CPM Change Password Failed", ChangeFailed, "PasswordManager", "unreachable", null)
                    ]
                },
                // REST leaves LogonDomain out for this one; the value BlueTrack already has is kept.
                new PvwaAccount(SafeId, 2, "Direct Safe", "acct-2", "host2", "svc", "UnixSSH", null, null, null, true, null, new Dictionary<string, string>())
            },
            Members =
            {
                new PvwaMember(SafeId, "Direct Safe", 2, "Administrator", false, null, all) { MemberType = "User", IsPredefinedUser = true },
                new PvwaMember(SafeId, "Direct Safe", 9, "Auditors", true, null, all) { MemberType = "Group" },
                // No such user in /Users (above the service user, say): kept as unresolved, not a failed load.
                new PvwaMember(SafeId, "Direct Safe", 777, "hidden.user", false, null, all) { MemberType = "User" }
            },
            Users = { new PvwaUser(2, "Administrator", "CyberArk", "Built-InAdmins", "\\", true, "A", "B", "a@b.com") },
            Groups = { new PvwaGroup(9, "Auditors", "Vault", "\\Applications", null, [2]) { Directory = "company.com" } },
            Platforms = { new PvwaPlatform("UnixSSH", "Unix via SSH", null, true, "Regular") }
        };
    }

    private static async Task ClearStagingAsync(SqlConnection connection) => await connection.ExecuteAsync("""
        DELETE FROM stg_sh_users; DELETE FROM stg_sh_groups; DELETE FROM stg_sh_groupmembers; DELETE FROM stg_sh_safes; DELETE FROM stg_sh_owners;
        DELETE FROM stg_sh_files; DELETE FROM stg_sh_objectproperties; DELETE FROM stg_sh_platforms;
        DELETE FROM stg_shd_platforms; DELETE FROM stg_shd_platform_properties; DELETE FROM stg_shd_safes; DELETE FROM stg_shd_accounts;
        DELETE FROM stg_shd_account_properties; DELETE FROM stg_shd_account_cpm_activity; DELETE FROM stg_shd_safe_members;
        DELETE FROM stg_shd_users; DELETE FROM stg_shd_user_authorizations; DELETE FROM stg_shd_groups; DELETE FROM stg_shd_group_members;
        """);

    private const string LoadSql = """
        EXEC dbo.usp_Load_DimLocation; EXEC dbo.usp_Load_DimPlatform; EXEC dbo.usp_Load_DimSafe; EXEC dbo.usp_Load_DimUser;
        EXEC dbo.usp_Load_DimGroup; EXEC dbo.usp_Load_FactAccount; EXEC dbo.usp_Load_FactSafeEntitlement;
        """;

    [Fact]
    public async Task ADirectPull_LoadsTheRestFields_Locations_DirectoryType_AndEveryMember()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await new SelfHostedDirectRepository(new TestDbConnectionFactory())
                .StageAsync(new SelfHostedStagingMapper().Map(Snapshot(), Guid.NewGuid(), "PVWA REST (test)"), CancellationToken.None);
            Assert.True(await connection.ExecuteScalarAsync<bool>("SELECT dbo.fn_SelfHostedStagedByDirect()"));

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("""
                INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted, IsDeletedInSource, PlatformLogonDomain)
                SELECT SourceSystemKey, '987101_2', 'acct-2', 0, 0, 'EVD-DOMAIN' FROM dbo.dim_source_system WHERE SourceSystemCode = 'SELFHOSTED';
                """, transaction: transaction);

            await connection.ExecuteAsync(LoadSql, transaction: transaction, commandTimeout: 300);

            var a1 = await connection.QuerySingleAsync<AccountRow>(AccountSql, new { id = "987101_1" }, transaction);
            Assert.Equal((false, "Locked by the business", "failure", "password", true), (a1.AutoManaged, a1.ManualReason, a1.CPMStatus, a1.SecretType, a1.RemoteAccessRestricted));
            Assert.Equal((new DateTime(2026, 10, 19), new DateTime(2026, 10, 10), Verified.Date), (a1.LastCPMModifiedDate, a1.LastReconciledDate, a1.LastVerifiedDate));
            Assert.Equal(new DateTime(2026, 9, 1, 8, 30, 0), a1.CreatedTime);
            Assert.Equal(("Change", "Failure", ChangeFailed), (a1.LastCPMAction, a1.LastCPMActionResult, a1.LastCPMActionTime));
            Assert.Equal("CORP", a1.PlatformLogonDomain);
            // Script 46: the Power BI view gives the new columns too.
            var powerBi = await connection.QuerySingleAsync<(string? Action, string? Result)>("""
                SELECT v.LastCPMAction, v.LastCPMActionResult FROM dbo.vw_powerbi_accounts v
                JOIN dbo.dim_source_system s ON s.SourceSystemKey = v.SourceSystemKey AND s.SourceSystemCode = 'SELFHOSTED'
                WHERE v.SourceAccountId = '987101_1'
                """, transaction: transaction);
            Assert.Equal(("Change", "Failure"), powerBi);

            var a2 = await connection.QuerySingleAsync<AccountRow>(AccountSql, new { id = "987101_2" }, transaction);
            Assert.Equal((true, (string?)null, (string?)null), (a2.AutoManaged, a2.CPMStatus, a2.LastCPMAction));
            Assert.Equal("EVD-DOMAIN", a2.PlatformLogonDomain);   // REST has none: the value BlueTrack had is kept

            // Locations by path: the Safe and the group point at the \Applications row.
            var locations = await connection.QuerySingleAsync<(string? Safe, string? Group)>("""
                SELECT (SELECT l.SourceLocationId FROM dbo.dim_safe sf JOIN dbo.dim_location l ON l.LocationKey = sf.LocationKey
                        JOIN dbo.dim_source_system s ON s.SourceSystemKey = sf.SourceSystemKey AND s.SourceSystemCode = 'SELFHOSTED'
                        WHERE sf.SafeUrlId = '987101'),
                       (SELECT l.SourceLocationId FROM dbo.dim_group g JOIN dbo.dim_location l ON l.LocationKey = g.LocationKey
                        JOIN dbo.dim_source_system s ON s.SourceSystemKey = g.SourceSystemKey AND s.SourceSystemCode = 'SELFHOSTED'
                        WHERE g.SourceGroupId = '9')
                """, transaction: transaction);
            Assert.Equal(("\\Applications", "\\Applications"), locations);
            Assert.Equal("company.com", await connection.ExecuteScalarAsync<string>("""
                SELECT g.DirectoryType FROM dbo.dim_group g JOIN dbo.dim_source_system s ON s.SourceSystemKey = g.SourceSystemKey
                WHERE s.SourceSystemCode = 'SELFHOSTED' AND g.SourceGroupId = '9'
                """, transaction: transaction));

            // Every member, predefined Administrator included; the unknown user is kept as unresolved.
            var members = (await connection.QueryAsync<(string MemberType, string? Method, string? Unresolved, bool? Predefined)>("""
                SELECT fse.MemberType, fse.ResolutionMethod, fse.UnresolvedMemberId, fse.IsPredefinedUser
                FROM dbo.fact_safe_entitlement fse JOIN dbo.dim_safe sf ON sf.SafeKey = fse.SafeKey
                WHERE sf.SafeUrlId = '987101' ORDER BY fse.MemberType, fse.UnresolvedMemberId
                """, transaction: transaction)).ToList();
            Assert.Equal(new List<(string, string?, string?, bool?)>
            {
                ("Group", "ById", null, null), ("User", "ById", null, true), ("User", null, "777", null)
            }, members);

            // The REST permission names, ViewAuditLog among them; MoveAccountsAndFolders as the EVD's from and into.
            var permissions = (await connection.QueryAsync<(string Name, bool Granted)>("""
                SELECT DISTINCT p.PermissionName, bep.IsGranted
                FROM dbo.bridge_entitlement_permission bep
                JOIN dbo.fact_safe_entitlement fse ON fse.EntitlementKey = bep.EntitlementKey
                JOIN dbo.dim_safe sf ON sf.SafeKey = fse.SafeKey
                JOIN dbo.dim_permission p ON p.PermissionKey = bep.PermissionKey
                WHERE sf.SafeUrlId = '987101' ORDER BY p.PermissionName
                """, transaction: transaction)).ToList();
            Assert.Contains(("ViewAuditLog", true), permissions);
            Assert.Contains(("UseAccounts", false), permissions);
            Assert.Contains(("MoveAccountsFrom", true), permissions);
            Assert.Contains(("MoveAccountsInto", true), permissions);
            Assert.DoesNotContain(permissions, p => p.Name == "MoveAccountsAndFolders");

            await transaction.RollbackAsync();
        }
        finally
        {
            await ClearStagingAsync(connection);
        }
    }

    [Fact]
    public async Task StagingNotFromOnePull_IsNotDirectStaging()
    {
        // D-277: only a real pull (stg_sh_* and stg_shd_* under one batch) switches the Load to the REST fields.
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO stg_sh_safes (ImportBatchId, SourceFileName, CASSafeID, CASSafeName) VALUES (NEWID(), N'PVWA REST (test)', 987101, N'Direct Safe');
                INSERT INTO stg_shd_safes (ImportBatchId, SourceFileName, SafeNumber, SafeUrlId, SafeName) VALUES (NEWID(), N'PVWA REST (test)', 987101, N'Direct%20Safe', N'Direct Safe');
                """);

            Assert.False(await connection.ExecuteScalarAsync<bool>("SELECT dbo.fn_SelfHostedStagedByDirect()"));
        }
        finally
        {
            await ClearStagingAsync(connection);
        }
    }

    [Fact]
    public async Task InternalSafes_AreMarked_BuiltIn_OrNamedAfterACpm()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("""
            DECLARE @sh INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
            INSERT INTO dbo.dim_user (SourceSystemKey, SourceUserId, Username, UserType) VALUES (@sh, '987922', 'TestCPM', '31');
            INSERT INTO dbo.dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES
                (@sh, '987201', 'PVWAConfig'), (@sh, '987202', 'TestCPM_Accounts'), (@sh, '987203', 'Marked Business Safe'),
                (@sh, '987204', 'Business Safe'), (@sh, '987205', 'System');
            INSERT INTO web.shd_comparison_mark (ItemKind, ItemKey, ItemName, Category) VALUES
                (N'Safe', N'987203', N'Marked Business Safe', N'Internal'),
                (N'Safe', N'987205', N'System', N'Expected');   -- an admin's Expected mark wins over the built-in name
            """, transaction: transaction);

        var internalSafes = (await connection.QueryAsync<(string Name, string Reason)>("""
            SELECT s.SafeName, i.InternalReason FROM dbo.vw_selfhosted_internal_safe i JOIN dbo.dim_safe s ON s.SafeKey = i.SafeKey
            WHERE s.SafeUrlId LIKE '9872__' ORDER BY s.SafeName
            """, transaction: transaction)).ToList();

        Assert.Equal(new List<(string, string)> { ("Marked Business Safe", "Marked"), ("PVWAConfig", "BuiltIn"), ("TestCPM_Accounts", "CpmSafe") }, internalSafes);
        await transaction.RollbackAsync();
    }

    private const string AccountSql = """
        SELECT fa.AutoManaged, fa.ManualReason, fa.CPMStatus, fa.SecretType, fa.RemoteAccessRestricted, fa.LastCPMModifiedDate,
               fa.LastReconciledDate, fa.LastVerifiedDate, fa.CreatedTime, fa.LastCPMAction, fa.LastCPMActionResult, fa.LastCPMActionTime,
               fa.PlatformLogonDomain
        FROM dbo.fact_account fa JOIN dbo.dim_source_system s ON s.SourceSystemKey = fa.SourceSystemKey AND s.SourceSystemCode = 'SELFHOSTED'
        WHERE fa.SourceAccountId = @id
        """;

    private sealed class AccountRow
    {
        public bool? AutoManaged { get; init; }
        public string? ManualReason { get; init; }
        public string? CPMStatus { get; init; }
        public string? SecretType { get; init; }
        public bool? RemoteAccessRestricted { get; init; }
        public DateTime? LastCPMModifiedDate { get; init; }
        public DateTime? LastReconciledDate { get; init; }
        public DateTime? LastVerifiedDate { get; init; }
        public DateTime? CreatedTime { get; init; }
        public string? LastCPMAction { get; init; }
        public string? LastCPMActionResult { get; init; }
        public DateTime? LastCPMActionTime { get; init; }
        public string? PlatformLogonDomain { get; init; }
    }
}
