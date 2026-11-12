using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Models;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-300/D-301: Admin > Safe Access Policies, the check, Reports > Safe Access and the per-account compliance review.
/// Each test builds its own Safes, groups and memberships, and removes them (and its roles and policies) again.
/// </summary>
public class SafeAccessPoliciesTests : IClassFixture<BlueTrackWebApplicationFactory>, IAsyncLifetime
{
    private readonly BlueTrackWebApplicationFactory _factory;
    private int _maxRoleKey;
    private int _maxPolicyKey;
    private string? _enabledPolicies;

    public SafeAccessPoliciesTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        _maxRoleKey = await connection.QuerySingleAsync<int>("SELECT ISNULL(MAX(SafeRoleKey), 0) FROM web.safe_role");
        _maxPolicyKey = await connection.QuerySingleAsync<int>("SELECT ISNULL(MAX(SafeAccessPolicyKey), 0) FROM web.safe_access_policy");
        _enabledPolicies = await connection.QuerySingleOrDefaultAsync<string>("SELECT STRING_AGG(CAST(SafeAccessPolicyKey AS NVARCHAR(20)), ',') FROM web.safe_access_policy WHERE IsEnabled = 1");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM web.safe_access_policy WHERE SafeAccessPolicyKey > @maxPolicy;
            DELETE FROM web.safe_role WHERE SafeRoleKey > @maxRole;
            UPDATE web.safe_access_policy SET IsEnabled = CASE WHEN CAST(SafeAccessPolicyKey AS NVARCHAR(20)) IN (SELECT value FROM STRING_SPLIT(@enabled, ',')) THEN 1 ELSE 0 END;
            EXEC dbo.usp_Check_SafeAccessPolicies;
            """, new { maxPolicy = _maxPolicyKey, maxRole = _maxRoleKey, enabled = _enabledPolicies ?? "" });
    }

    [Fact]
    public async Task OnlyManageSafeAccessPolicies_EditsThem_AndApproversReview()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/admin/safe-access/policies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Approver").GetAsync("/api/admin/safe-access/policies")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client("TestUser.Admin").GetAsync("/api/admin/safe-access/policies")).StatusCode);
        // D-301: Analysts don't review; the review endpoints answer 404 for a missing account only past the permission check.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Analyst").PutAsJsonAsync("/api/account-progress/-1/compliance-review", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client("TestUser.Approver").PutAsJsonAsync("/api/account-progress/-1/compliance-review", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client("TestUser.Viewer").GetAsync("/api/reports/safe-access")).StatusCode);
    }

    [Fact]
    public async Task TheExampleIsSeededDisabled_AndRolesAndPoliciesAreValidated()
    {
        var admin = Client("TestUser.Admin");
        var example = Assert.Single((await admin.GetFromJsonAsync<List<SafeAccessPolicy>>("/api/admin/safe-access/policies"))!, p => p.PolicyName == "Example: every Safe");
        Assert.False(example.IsEnabled);
        Assert.Equal([("Required", "{SafeName}_Users", "End user", true), ("Forbidden", "Vault Admins", (string?)null, false)],
            example.Rules.Select(r => (r.RuleType!, r.MemberPattern!, r.SafeRoleName, r.RequirePeople)));

        async Task<HttpStatusCode> Role(object body) => (await admin.PostAsJsonAsync("/api/admin/safe-access/roles", body)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Role(new { RoleName = "x", Permissions = Array.Empty<string>() }));
        Assert.Equal(HttpStatusCode.BadRequest, await Role(new { RoleName = "x", Permissions = new[] { "FlyAccounts" } }));
        Assert.Equal(HttpStatusCode.Conflict, await Role(new { RoleName = "End user", Permissions = new[] { "ListAccounts" } }));

        async Task<HttpStatusCode> Policy(object body) => (await admin.PostAsJsonAsync("/api/admin/safe-access/policies", body)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Policy(new { PolicyName = "x", SafeNamePattern = "*", Rules = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, await Policy(new { PolicyName = "x", SafeNamePattern = "*", Rules = new[] { new { RuleType = "Required", MemberPattern = "a" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Policy(new { PolicyName = "x", SafeNamePattern = "*", Rules = new[] { new { RuleType = "Maybe", MemberPattern = "a" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Policy(new { PolicyName = "x", SafeNamePattern = "", Rules = new[] { new { RuleType = "Forbidden", MemberPattern = "a" } } }));

        // A role a rule uses can't be deleted.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/safe-access/roles/{example.Rules[0].SafeRoleKey}")).StatusCode);
    }

    [Fact]
    public async Task TheCheck_FindsMissingAndForbiddenMembers_AndAReviewLastsUntilTheAccountMovesSafe()
    {
        var admin = Client("TestUser.Admin");
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var token = $"sap{Guid.NewGuid():N}"[..11];
        var safeName = $"{token}_App";
        var ids = await connection.QuerySingleAsync<Ids>("""
            INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (1, @safeName, @safeName);
            DECLARE @safe INT = SCOPE_IDENTITY();
            INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (1, @token + '_Other', @token + '_Other');
            DECLARE @other INT = SCOPE_IDENTITY();
            INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (1, @token + 'g1', @safeName + '_Users');
            DECLARE @users INT = SCOPE_IDENTITY();
            INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (1, @token + 'g2', @token + ' Vault Admins');
            DECLARE @admins INT = SCOPE_IDENTITY();
            INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username) VALUES (1, @token + 'u1', @token + '.person');
            DECLARE @user INT = SCOPE_IDENTITY();
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted, SafeKey) VALUES (1, @token, @token, 0, @safe);
            SELECT @safe AS Safe, @other AS OtherSafe, @users AS Users, @admins AS Admins, @user AS [User], CAST(SCOPE_IDENTITY() AS BIGINT) AS Account;
            """, new { token, safeName });
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
                VALUES (@Account, dbo.fn_StartStageKey(), (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'In Progress'));
                INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, SnapshotDate) VALUES (@Safe, 'Group', @Users, CAST(GETDATE() AS DATE));
                INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
                SELECT SCOPE_IDENTITY(), PermissionKey, 1 FROM dim_permission WHERE PermissionName IN ('ListAccounts', 'UseAccounts');
                INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, SnapshotDate) VALUES (@Safe, 'Group', @Admins, CAST(GETDATE() AS DATE));
                """, ids);

            // Only this test's policy is on: the others are turned off while it runs.
            await connection.ExecuteAsync("UPDATE web.safe_access_policy SET IsEnabled = 0");
            var role = (await (await admin.PostAsJsonAsync("/api/admin/safe-access/roles", new
            {
                RoleName = $"{token} retrieve", Permissions = new[] { "ListAccounts", "UseAccounts", "RetrieveAccounts" }
            })).Content.ReadFromJsonAsync<RoleKeyResponse>())!.SafeRoleKey;
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/safe-access/policies", new
            {
                PolicyName = $"{token} apps", SafeNamePattern = $"{token}_*", SortOrder = 0,
                Rules = new object[]
                {
                    new { RuleType = "Required", MemberPattern = "{SafeName}_Users", SafeRoleKey = role, RequirePeople = true },
                    new { RuleType = "Forbidden", MemberPattern = "*Vault Admins" }
                }
            })).StatusCode);

            async Task<SafeAccessReportRow> CheckAsync()
            {
                Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/admin/safe-access/check", null)).StatusCode);
                return Assert.Single((await admin.GetFromJsonAsync<List<SafeAccessReportRow>>("/api/reports/safe-access"))!, r => r.SafeKey == ids.Safe);
            }

            // The group lacks Retrieve and has nobody in it, and a Vault Admins group is a member.
            var row = await CheckAsync();
            Assert.Equal((false, $"{token} apps"), (row.IsCompliant, row.PolicyName));
            Assert.Equal([$"{safeName}_Users: doesn't hold {token} retrieve.", $"{token} Vault Admins: must not be a member."], row.Problems);

            // Retrieve granted: it still needs people in it.
            await connection.ExecuteAsync("""
                INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
                SELECT e.EntitlementKey, p.PermissionKey, 1 FROM fact_safe_entitlement e CROSS JOIN dim_permission p
                WHERE e.GroupKey = @Users AND p.PermissionName = 'RetrieveAccounts';
                DELETE FROM fact_safe_entitlement WHERE GroupKey = @Admins;
                """, ids);
            Assert.Equal([$"{safeName}_Users: has no people in it (Vault or AD)."], (await CheckAsync()).Problems);

            await connection.ExecuteAsync("INSERT INTO bridge_group_membership (GroupKey, UserKey, SnapshotDate) VALUES (@Users, @User, CAST(GETDATE() AS DATE))", ids);
            row = await CheckAsync();
            Assert.Equal((true, 0, 1, 0), (row.IsCompliant, row.Problems.Count, row.AccountCount, row.ReviewedCount));
            var field = await connection.QuerySingleAsync<string>("SELECT TextValue FROM dbo.fn_ProgressAccountFields(@Account) WHERE FieldName = 'SafeAccessCompliant'", ids);
            Assert.Equal("Yes", field);

            // D-301: an Approver reviews the Safe's accounts in one go; the account page shows who and when.
            var approver = Client("TestUser.Approver");
            var reviewed = await (await approver.PostAsJsonAsync($"/api/reports/safe-access/{ids.Safe}/review", new { Note = "Checked with the app owner" })).Content.ReadFromJsonAsync<ReviewedResponse>();
            Assert.Equal(1, reviewed!.ReviewedCount);
            var compliance = (await approver.GetFromJsonAsync<AccountCompliance>($"/api/account-progress/{ids.Account}/compliance"))!;
            Assert.Equal((true, "Checked with the app owner"), (compliance.IsCompliant, compliance.ReviewNote));
            Assert.NotNull(compliance.ReviewedAt);
            Assert.Equal("Yes", await connection.QuerySingleAsync<string>("SELECT TextValue FROM dbo.fn_ProgressAccountFields(@Account) WHERE FieldName = 'ComplianceReviewed'", ids));

            // A Safe failing later doesn't clear it; moving the account to another Safe does.
            await connection.ExecuteAsync("DELETE FROM bridge_group_membership WHERE GroupKey = @Users", ids);
            Assert.Equal(1, (await CheckAsync()).ReviewedCount);
            await connection.ExecuteAsync("UPDATE fact_account SET SafeKey = @OtherSafe WHERE AccountKey = @Account; EXEC dbo.usp_Check_SafeAccessPolicies;", ids);
            Assert.Null((await approver.GetFromJsonAsync<AccountCompliance>($"/api/account-progress/{ids.Account}/compliance"))!.ReviewedAt);

            // Reviewed by hand and cleared by hand.
            Assert.Equal(HttpStatusCode.NoContent, (await approver.PutAsJsonAsync($"/api/account-progress/{ids.Account}/compliance-review", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await approver.DeleteAsync($"/api/account-progress/{ids.Account}/compliance-review")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await approver.DeleteAsync($"/api/account-progress/{ids.Account}/compliance-review")).StatusCode);
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM web.account_compliance_review WHERE AccountKey = @Account;
                DELETE FROM dbo.safe_access_problem WHERE SafeKey IN (@Safe, @OtherSafe);
                DELETE FROM dbo.safe_access_result WHERE SafeKey IN (@Safe, @OtherSafe);
                DELETE FROM bridge_entitlement_permission WHERE EntitlementKey IN (SELECT EntitlementKey FROM fact_safe_entitlement WHERE SafeKey IN (@Safe, @OtherSafe));
                DELETE FROM fact_safe_entitlement WHERE SafeKey IN (@Safe, @OtherSafe);
                DELETE FROM bridge_group_membership WHERE GroupKey IN (@Users, @Admins);
                DELETE FROM fact_account_progress WHERE AccountKey = @Account;
                DELETE FROM fact_account WHERE AccountKey = @Account;
                DELETE FROM dim_group WHERE GroupKey IN (@Users, @Admins);
                DELETE FROM dim_user WHERE UserKey = @User;
                DELETE FROM dim_safe WHERE SafeKey IN (@Safe, @OtherSafe);
                """, ids);
        }
    }

    /// <summary>
    /// D-356: Self-Hosted's MoveAccountsFrom and MoveAccountsInto together are Privilege Cloud's MoveAccountsAndFolders,
    /// both ways, for holding a role and for an exact match; a role naming only one of the two is held, not exactly.
    /// </summary>
    [Fact]
    public async Task TheMovePermissions_MatchAcrossSources()
    {
        var admin = Client("TestUser.Admin");
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var token = $"sam{Guid.NewGuid():N}"[..11];
        var ids = await connection.QuerySingleAsync<MoveIds>("""
            INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (1, @token, @token);
            DECLARE @safe INT = SCOPE_IDENTITY();
            INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (1, @token + 'g1', @token + '_SH');
            DECLARE @sh INT = SCOPE_IDENTITY();
            INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (1, @token + 'g2', @token + '_PC');
            DECLARE @pc INT = SCOPE_IDENTITY();
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted, SafeKey) VALUES (1, @token, @token, 0, @safe);
            DECLARE @account BIGINT = SCOPE_IDENTITY();
            INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, SnapshotDate) VALUES (@safe, 'Group', @sh, CAST(GETDATE() AS DATE));
            INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
            SELECT SCOPE_IDENTITY(), PermissionKey, 1 FROM dim_permission WHERE PermissionName IN ('ListAccounts', 'MoveAccountsFrom', 'MoveAccountsInto');
            INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, SnapshotDate) VALUES (@safe, 'Group', @pc, CAST(GETDATE() AS DATE));
            INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
            SELECT SCOPE_IDENTITY(), PermissionKey, 1 FROM dim_permission WHERE PermissionName IN ('ListAccounts', 'MoveAccountsAndFolders');
            SELECT @safe AS Safe, @sh AS SelfHosted, @pc AS PrivilegeCloud, @account AS Account;
            """, new { token });
        try
        {
            await connection.ExecuteAsync("UPDATE web.safe_access_policy SET IsEnabled = 0");
            async Task<int> Role(string name, bool exact, params string[] permissions) =>
                (await (await admin.PostAsJsonAsync("/api/admin/safe-access/roles", new { RoleName = $"{token} {name}", ExactMatch = exact, Permissions = permissions }))
                    .Content.ReadFromJsonAsync<RoleKeyResponse>())!.SafeRoleKey;
            var combined = await Role("combined", true, "ListAccounts", "MoveAccountsAndFolders");
            var split = await Role("split", true, "ListAccounts", "MoveAccountsFrom", "MoveAccountsInto");
            var fromOnly = await Role("from only", false, "ListAccounts", "MoveAccountsFrom");
            var fromOnlyExact = await Role("from only exact", true, "ListAccounts", "MoveAccountsFrom");
            object Required(string member, int role) => new { RuleType = "Required", MemberPattern = $"{token}_{member}", SafeRoleKey = role };
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/safe-access/policies", new
            {
                PolicyName = $"{token} moves", SafeNamePattern = token, SortOrder = 0,
                Rules = new[]
                {
                    Required("SH", combined), Required("PC", combined), Required("SH", split), Required("PC", split),
                    Required("PC", fromOnly), Required("PC", fromOnlyExact), Required("SH", fromOnlyExact)
                }
            })).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/admin/safe-access/check", null)).StatusCode);
            var row = Assert.Single((await admin.GetFromJsonAsync<List<SafeAccessReportRow>>("/api/reports/safe-access"))!, r => r.SafeKey == ids.Safe);
            // Only the exact "from only" role fails: each member also holds Into (Self-Hosted) or the combined move (Privilege Cloud).
            Assert.Equal(
                new[] { $"{token}_PC: doesn't hold {token} from only exact (exactly).", $"{token}_SH: doesn't hold {token} from only exact (exactly)." },
                row.Problems.Order(StringComparer.Ordinal));
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM dbo.safe_access_problem WHERE SafeKey = @Safe;
                DELETE FROM dbo.safe_access_result WHERE SafeKey = @Safe;
                DELETE FROM bridge_entitlement_permission WHERE EntitlementKey IN (SELECT EntitlementKey FROM fact_safe_entitlement WHERE SafeKey = @Safe);
                DELETE FROM fact_safe_entitlement WHERE SafeKey = @Safe;
                DELETE FROM fact_account WHERE AccountKey = @Account;
                DELETE FROM dim_group WHERE GroupKey IN (@SelfHosted, @PrivilegeCloud);
                DELETE FROM dim_safe WHERE SafeKey = @Safe;
                """, ids);
        }
    }

    private sealed class MoveIds
    {
        public int Safe { get; init; }
        public int SelfHosted { get; init; }
        public int PrivilegeCloud { get; init; }
        public long Account { get; init; }
    }

    private sealed class Ids
    {
        public int Safe { get; init; }
        public int OtherSafe { get; init; }
        public int Users { get; init; }
        public int Admins { get; init; }
        public int User { get; init; }
        public long Account { get; init; }
    }

    private sealed record RoleKeyResponse(int SafeRoleKey);
    private sealed record ReviewedResponse(int ReviewedCount);
}
