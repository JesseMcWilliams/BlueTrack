using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-232 (24_BlueTrack_EntitlementMemberResolution.sql): usp_Load_FactSafeEntitlement
/// resolves a Privilege Cloud member by its manual resolution, then by ID, then
/// by exact name (case ignored, only when one user or group has it), and keeps
/// every row. Each test stages its own safe, users, groups and entitlements in a
/// transaction it rolls back, so staging, the dimensions and the fact table are
/// left as they were (the load reloads every Privilege Cloud entitlement).
/// </summary>
public class EntitlementMemberResolutionLoadTests
{
    private sealed record FactRow(string MemberType, string? MemberName, int? UserKey, int? GroupKey, string? UnresolvedMemberId, string? ResolutionMethod, long EntitlementKey);

    private sealed class Fixture
    {
        public required SqlConnection Connection { get; init; }
        public required SqlTransaction Transaction { get; init; }
        public required string Token { get; init; }
        public required string SafeUrlId { get; init; }
        public int PrivCloudKey { get; init; }
        public int ResolvedBy { get; init; }
        public Dictionary<string, int> Users { get; } = [];
        public Dictionary<string, int> Groups { get; } = [];
    }

    private static async Task<Fixture> BeginAsync()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var token = $"emr{Guid.NewGuid():N}"[..14];
        var privCloudKey = await connection.QuerySingleAsync<int>(
            "SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD'", transaction: transaction);
        var resolvedBy = await connection.QuerySingleAsync<int>(
            "SELECT UserKey FROM web.app_user WHERE ExternalIdentifier = 'IntegrationTestUser1'", transaction: transaction);
        var fixture = new Fixture
        {
            Connection = connection, Transaction = transaction, Token = token, SafeUrlId = $"{token}_Safe",
            PrivCloudKey = privCloudKey, ResolvedBy = resolvedBy
        };
        await connection.ExecuteAsync("""
            DELETE FROM dbo.stg_pc_entitlements;
            INSERT INTO dbo.dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@privCloudKey, @safe, @safe);
            """, new { privCloudKey, safe = fixture.SafeUrlId }, transaction);
        return fixture;
    }

    private static async Task<int> AddUserAsync(Fixture f, string sourceId, string username)
    {
        var key = await f.Connection.QuerySingleAsync<int>("""
            INSERT INTO dbo.dim_user (SourceSystemKey, SourceUserId, Username) OUTPUT inserted.UserKey VALUES (@src, @id, @name)
            """, new { src = f.PrivCloudKey, id = $"{f.Token}-{sourceId}", name = $"{f.Token}.{username}" }, f.Transaction);
        f.Users[sourceId] = key;
        return key;
    }

    private static async Task<int> AddGroupAsync(Fixture f, string sourceId, string groupName)
    {
        var key = await f.Connection.QuerySingleAsync<int>("""
            INSERT INTO dbo.dim_group (SourceSystemKey, SourceGroupId, GroupName) OUTPUT inserted.GroupKey VALUES (@src, @id, @name)
            """, new { src = f.PrivCloudKey, id = $"{f.Token}-{sourceId}", name = $"{f.Token}-{groupName}" }, f.Transaction);
        f.Groups[sourceId] = key;
        return key;
    }

    /// <summary>Stages one entitlement on the fixture's safe; memberId and memberName get the token prefix.</summary>
    private static Task StageAsync(Fixture f, string memberType, string memberId, string memberName, bool retrieve = true) =>
        f.Connection.ExecuteAsync("""
            INSERT INTO dbo.stg_pc_entitlements (ImportBatchId, SourceFileName, SafeUrlId, SafeName, SafeNumber, MemberId, MemberName, MemberType,
                                                 ListAccounts, RetrieveAccounts)
            VALUES (NEWID(), 'test.csv', @safe, @safe, 1, @memberId, @memberName, @memberType, 1, @retrieve)
            """, new { safe = f.SafeUrlId, memberId = $"{f.Token}-{memberId}", memberName, memberType, retrieve }, f.Transaction);

    private static string UserName(Fixture f, string name) => $"{f.Token}.{name}";
    private static string GroupName(Fixture f, string name) => $"{f.Token}-{name}";

    private static Task LoadAsync(Fixture f) => f.Connection.ExecuteAsync("EXEC dbo.usp_Load_FactSafeEntitlement", transaction: f.Transaction);

    private static async Task<List<FactRow>> FactRowsAsync(Fixture f) =>
        (await f.Connection.QueryAsync<FactRow>("""
            SELECT fse.MemberType, fse.MemberName, fse.UserKey, fse.GroupKey, fse.UnresolvedMemberId, fse.ResolutionMethod, fse.EntitlementKey
            FROM dbo.fact_safe_entitlement fse
            JOIN dbo.dim_safe sf ON sf.SafeKey = fse.SafeKey
            WHERE sf.SafeUrlId = @safe
            ORDER BY fse.MemberName
            """, new { safe = f.SafeUrlId }, f.Transaction)).ToList();

    private static FactRow Row(List<FactRow> rows, string memberName) => Assert.Single(rows, r => r.MemberName == memberName);

    private static Task AddManualAsync(Fixture f, string memberType, string memberId, string resolution, int? userKey = null, int? groupKey = null, string? note = null) =>
        f.Connection.ExecuteAsync("""
            INSERT INTO web.entitlement_member_resolution (SourceSystemKey, MemberType, SourceMemberId, Resolution, UserKey, GroupKey, Note, ResolvedBy)
            VALUES (@src, @memberType, @memberId, @resolution, @userKey, @groupKey, @note, @resolvedBy)
            """, new { src = f.PrivCloudKey, memberType, memberId = $"{f.Token}-{memberId}", resolution, userKey, groupKey, note, resolvedBy = f.ResolvedBy }, f.Transaction);

    [Fact]
    public async Task IdMatch_ThenUniqueNameMatch_CaseIgnored_AndAmbiguousOrUnknownNamesStayUnresolved()
    {
        var f = await BeginAsync();
        await using var _ = f.Connection;
        var byId = await AddUserAsync(f, "U1", "byid");
        var byName = await AddUserAsync(f, "U2", "byname");
        await AddUserAsync(f, "U3", "twice");
        await AddUserAsync(f, "U4", "TWICE");             // same name as U3, case aside: ambiguous
        var group = await AddGroupAsync(f, "G1", "Vault Admins");

        await StageAsync(f, "User", "U1", UserName(f, "byid"));
        await StageAsync(f, "User", "guid-2", UserName(f, "BYNAME"));      // different ID, same name in another case
        await StageAsync(f, "User", "guid-3", UserName(f, "twice"));
        await StageAsync(f, "User", "guid-4", UserName(f, "nobody"));
        await StageAsync(f, "Group", "Role_G1", GroupName(f, "Vault Admins"));

        await LoadAsync(f);
        var rows = await FactRowsAsync(f);

        Assert.Equal(5, rows.Count);
        Assert.Equal(new FactRow("User", UserName(f, "byid"), byId, null, null, "ById", 0), Row(rows, UserName(f, "byid")) with { EntitlementKey = 0 });
        Assert.Equal(new FactRow("User", UserName(f, "BYNAME"), byName, null, $"{f.Token}-guid-2", "ByName", 0), Row(rows, UserName(f, "BYNAME")) with { EntitlementKey = 0 });
        Assert.Equal(new FactRow("User", UserName(f, "twice"), null, null, $"{f.Token}-guid-3", null, 0), Row(rows, UserName(f, "twice")) with { EntitlementKey = 0 });
        Assert.Equal(new FactRow("User", UserName(f, "nobody"), null, null, $"{f.Token}-guid-4", null, 0), Row(rows, UserName(f, "nobody")) with { EntitlementKey = 0 });
        Assert.Equal(new FactRow("Group", GroupName(f, "Vault Admins"), null, group, $"{f.Token}-Role_G1", "ByName", 0), Row(rows, GroupName(f, "Vault Admins")) with { EntitlementKey = 0 });

        // Every row keeps its permissions (List + Retrieve), resolved or not.
        var permissionCounts = await f.Connection.QueryAsync<int>(
            "SELECT COUNT(*) FROM dbo.bridge_entitlement_permission WHERE EntitlementKey IN @keys GROUP BY EntitlementKey",
            new { keys = rows.Select(r => r.EntitlementKey).ToList() }, f.Transaction);
        Assert.Equal([2, 2, 2, 2, 2], permissionCounts.ToArray());

        // The name match counts as direct access for vw_effective_safe_access.
        var effective = await f.Connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM dbo.vw_effective_safe_access a JOIN dbo.dim_safe sf ON sf.SafeKey = a.SafeKey
            WHERE sf.SafeUrlId = @safe AND a.UserKey = @byName AND a.AccessPath = 'Direct'
            """, new { safe = f.SafeUrlId, byName }, f.Transaction);
        Assert.Equal(1, effective);
        await f.Transaction.RollbackAsync();
    }

    [Fact]
    public async Task NameMatchedToAUserWhoAlsoHoldsTheSafeById_KeepsBothRows_WithTheirOwnPermissions()
    {
        var f = await BeginAsync();
        await using var _ = f.Connection;
        var user = await AddUserAsync(f, "U1", "alice");
        await StageAsync(f, "User", "U1", UserName(f, "alice"), retrieve: true);
        await StageAsync(f, "User", "guid-1", UserName(f, "alice"), retrieve: false);

        await LoadAsync(f);
        var rows = await FactRowsAsync(f);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(user, r.UserKey));
        Assert.Equal(["ById", "ByName"], rows.Select(r => r.ResolutionMethod!).Order().ToArray());
        var retrieve = await f.Connection.QueryAsync<(string Method, bool IsGranted)>("""
            SELECT fse.ResolutionMethod, bep.IsGranted
            FROM dbo.bridge_entitlement_permission bep
            JOIN dbo.fact_safe_entitlement fse ON fse.EntitlementKey = bep.EntitlementKey
            JOIN dbo.dim_permission p ON p.PermissionKey = bep.PermissionKey
            JOIN dbo.permission_alias pa ON pa.PermissionKey = p.PermissionKey AND pa.RawPermissionName = 'RetrieveAccounts'
            WHERE fse.EntitlementKey IN @keys
            ORDER BY fse.ResolutionMethod
            """, new { keys = rows.Select(r => r.EntitlementKey).ToList() }, f.Transaction);
        Assert.Equal([("ById", true), ("ByName", false)], retrieve.ToArray());
        await f.Transaction.RollbackAsync();
    }

    [Fact]
    public async Task ManualResolutions_ComeFirst_AndSurviveTheReload_ExternalKeepsNoKey()
    {
        var f = await BeginAsync();
        await using var _ = f.Connection;
        await AddUserAsync(f, "U1", "nameclash");
        var chosen = await AddUserAsync(f, "U2", "chosen");
        await StageAsync(f, "User", "guid-1", UserName(f, "nameclash"));   // would match U1 by name
        await StageAsync(f, "Group", "DPA_Admin_Role", "DPA_Admin_Role");
        await AddManualAsync(f, "User", "guid-1", "Linked", userKey: chosen);
        await AddManualAsync(f, "Group", "DPA_Admin_Role", "External", note: "Built-in cloud role");

        await LoadAsync(f);
        await LoadAsync(f);   // the nightly reload keeps them
        var rows = await FactRowsAsync(f);

        Assert.Equal(new FactRow("Group", "DPA_Admin_Role", null, null, $"{f.Token}-DPA_Admin_Role", "External", 0), Row(rows, "DPA_Admin_Role") with { EntitlementKey = 0 });
        Assert.Equal(new FactRow("User", UserName(f, "nameclash"), chosen, null, $"{f.Token}-guid-1", "Manual", 0), Row(rows, UserName(f, "nameclash")) with { EntitlementKey = 0 });

        // The report view says who resolved it and keeps the note.
        var external = await f.Connection.QuerySingleAsync<(string? ResolutionNote, string? ResolvedByName)>(
            "SELECT ResolutionNote, ResolvedByName FROM dbo.vw_unresolved_entitlement_members WHERE UnresolvedMemberId = @id",
            new { id = $"{f.Token}-DPA_Admin_Role" }, f.Transaction);
        Assert.Equal("Built-in cloud role", external.ResolutionNote);
        Assert.False(string.IsNullOrEmpty(external.ResolvedByName));
        await f.Transaction.RollbackAsync();
    }

    [Fact]
    public async Task ApplyAfterUndo_FallsBackToTheNameMatch_AndApplyAfterResolve_ChangesTheRowsAtOnce()
    {
        var f = await BeginAsync();
        await using var _ = f.Connection;
        var nameMatch = await AddUserAsync(f, "U1", "bob");
        var other = await AddUserAsync(f, "U2", "robert");
        await StageAsync(f, "User", "guid-1", UserName(f, "bob"));
        await StageAsync(f, "User", "guid-9", UserName(f, "unknown"));
        await LoadAsync(f);

        async Task ApplyAsync(string memberId) => await f.Connection.ExecuteAsync(
            "EXEC dbo.usp_Apply_EntitlementMemberResolution @SourceSystemKey = @src, @MemberType = 'User', @SourceMemberId = @id",
            new { src = f.PrivCloudKey, id = $"{f.Token}-{memberId}" }, f.Transaction);

        Assert.Equal<(string?, int?)>(("ByName", nameMatch), Pick(Row(await FactRowsAsync(f), UserName(f, "bob"))));

        await AddManualAsync(f, "User", "guid-1", "Linked", userKey: other);
        await AddManualAsync(f, "User", "guid-9", "External");
        await ApplyAsync("guid-1");
        await ApplyAsync("guid-9");
        var rows = await FactRowsAsync(f);
        Assert.Equal<(string?, int?)>(("Manual", other), Pick(Row(rows, UserName(f, "bob"))));
        Assert.Equal<(string?, int?)>(("External", (int?)null), Pick(Row(rows, UserName(f, "unknown"))));

        await f.Connection.ExecuteAsync("DELETE FROM web.entitlement_member_resolution WHERE SourceMemberId IN @ids",
            new { ids = new[] { $"{f.Token}-guid-1", $"{f.Token}-guid-9" } }, f.Transaction);
        await ApplyAsync("guid-1");
        await ApplyAsync("guid-9");
        rows = await FactRowsAsync(f);
        Assert.Equal<(string?, int?)>(("ByName", nameMatch), Pick(Row(rows, UserName(f, "bob"))));
        Assert.Equal<(string?, int?)>(((string?)null, (int?)null), Pick(Row(rows, UserName(f, "unknown"))));
        await f.Transaction.RollbackAsync();
    }

    private static (string? Method, int? UserKey) Pick(FactRow row) => (row.ResolutionMethod, row.UserKey);

    [Fact]
    public async Task TheChecks_RejectAResolvedRowWithoutAKey_AndAManualLinkOfTheWrongType()
    {
        var f = await BeginAsync();
        await using var _ = f.Connection;
        var user = await AddUserAsync(f, "U1", "carol");

        var fact = await Assert.ThrowsAsync<SqlException>(() => f.Connection.ExecuteAsync("""
            INSERT INTO dbo.fact_safe_entitlement (SafeKey, MemberType, UnresolvedMemberId, ResolutionMethod, SnapshotDate)
            SELECT SafeKey, 'User', 'x', 'ByName', CAST(SYSUTCDATETIME() AS DATE) FROM dbo.dim_safe WHERE SafeUrlId = @safe
            """, new { safe = f.SafeUrlId }, f.Transaction));
        Assert.Contains("CK_fact_safe_entitlement_resolution", fact.Message);

        var manual = await Assert.ThrowsAsync<SqlException>(() => AddManualAsync(f, "Group", "g-1", "Linked", userKey: user));
        Assert.Contains("CK_entitlement_member_resolution", manual.Message);
        await f.Transaction.RollbackAsync();
    }
}
