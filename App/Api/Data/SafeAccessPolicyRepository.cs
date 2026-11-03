using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>Thrown for a change that would leave the Safe roles or policies inconsistent; the message is shown to the admin.</summary>
public sealed class SafeAccessConflictException(string message) : Exception(message);

/// <summary>
/// D-300/D-301 (57_BlueTrack_SafeAccessPolicies.sql): the Safe roles (web.safe_role), the Safe access policies
/// (web.safe_access_policy, web.safe_access_policy_rule), their last check (dbo.safe_access_result,
/// dbo.safe_access_problem, written by dbo.usp_Check_SafeAccessPolicies) and the per-account compliance reviews
/// (web.account_compliance_review).
/// </summary>
public sealed class SafeAccessPolicyRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>The Safe permissions a role can name (dbo.dim_permission), in catalog order.</summary>
    public async Task<IReadOnlyList<string>> GetPermissionNamesAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<string>("SELECT PermissionName FROM dbo.dim_permission ORDER BY PermissionKey")).ToList();
    }

    public async Task<IReadOnlyList<SafeRole>> GetRolesAsync()
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT r.SafeRoleKey, r.RoleName, r.Description, r.ExactMatch,
                   (SELECT COUNT(*) FROM web.safe_access_policy_rule pr WHERE pr.SafeRoleKey = r.SafeRoleKey) AS RuleCount
            FROM web.safe_role r ORDER BY r.RoleName;

            SELECT rp.SafeRoleKey, p.PermissionName
            FROM web.safe_role_permission rp JOIN dbo.dim_permission p ON p.PermissionKey = rp.PermissionKey
            ORDER BY rp.SafeRoleKey, p.PermissionKey;
            """);
        var roles = (await multi.ReadAsync<SafeRole>()).ToList();
        var permissions = (await multi.ReadAsync<(int SafeRoleKey, string PermissionName)>()).ToLookup(p => p.SafeRoleKey, p => p.PermissionName);
        foreach (var role in roles) role.Permissions = permissions[role.SafeRoleKey].ToList();
        return roles;
    }

    /// <summary>Adds (roleKey null) or replaces a role. Returns its key, or null when the role to replace is gone.</summary>
    public async Task<int?> SaveRoleAsync(int? roleKey, SaveSafeRoleRequest request, int userKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var name = request.RoleName!.Trim();
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM web.safe_role WHERE RoleName = @name AND (@roleKey IS NULL OR SafeRoleKey <> @roleKey)",
                new { name, roleKey }, transaction) > 0)
            throw new SafeAccessConflictException($"There's already a Safe role called '{name}'.");
        var p = new { roleKey, RoleName = name, Description = Blank(request.Description), request.ExactMatch, userKey };
        int? key = roleKey is null
            ? await connection.QuerySingleAsync<int>("""
                INSERT INTO web.safe_role (RoleName, Description, ExactMatch, ModifiedBy) VALUES (@RoleName, @Description, @ExactMatch, @userKey);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, p, transaction)
            : await connection.ExecuteAsync("""
                UPDATE web.safe_role SET RoleName = @RoleName, Description = @Description, ExactMatch = @ExactMatch, ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
                WHERE SafeRoleKey = @roleKey
                """, p, transaction) == 0 ? null : roleKey;
        if (key is null) return null;
        await connection.ExecuteAsync("""
            DELETE FROM web.safe_role_permission WHERE SafeRoleKey = @key;
            INSERT INTO web.safe_role_permission (SafeRoleKey, PermissionKey)
            SELECT @key, PermissionKey FROM dbo.dim_permission WHERE PermissionName IN @names;
            """, new { key, names = request.Permissions!.Distinct().ToList() }, transaction);
        transaction.Commit();
        return key;
    }

    /// <summary>Deletes a role no policy rule uses. Returns its name, or null when there's no such role.</summary>
    public async Task<string?> DeleteRoleAsync(int roleKey)
    {
        using var connection = connectionFactory.Create();
        var used = await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.safe_access_policy_rule WHERE SafeRoleKey = @roleKey", new { roleKey });
        if (used > 0) throw new SafeAccessConflictException($"{used} policy rule(s) use this role: change them first.");
        return await connection.QuerySingleOrDefaultAsync<string>("DELETE FROM web.safe_role OUTPUT deleted.RoleName WHERE SafeRoleKey = @roleKey", new { roleKey });
    }

    /// <summary>The role keys that exist, to check a policy's rules against.</summary>
    public async Task<IReadOnlySet<int>> GetRoleKeysAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<int>("SELECT SafeRoleKey FROM web.safe_role")).ToHashSet();
    }

    public async Task<IReadOnlyList<SafeAccessPolicy>> GetPoliciesAsync()
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT p.SafeAccessPolicyKey, p.PolicyName, p.SafeNamePattern, p.SortOrder, p.IsEnabled, p.Description,
                   u.DisplayName AS ModifiedByName, p.ModifiedDate
            FROM web.safe_access_policy p
            LEFT JOIN web.app_user u ON u.UserKey = p.ModifiedBy
            ORDER BY p.SortOrder, p.SafeAccessPolicyKey;

            SELECT r.SafeAccessPolicyKey, r.RuleType, r.MemberPattern, r.SafeRoleKey, sr.RoleName AS SafeRoleName, r.RequirePeople
            FROM web.safe_access_policy_rule r
            LEFT JOIN web.safe_role sr ON sr.SafeRoleKey = r.SafeRoleKey
            ORDER BY r.SafeAccessPolicyKey, r.SafeAccessPolicyRuleKey;
            """);
        var policies = (await multi.ReadAsync<SafeAccessPolicy>()).ToList();
        var rules = (await multi.ReadAsync<RuleRow>()).ToLookup(r => r.SafeAccessPolicyKey);
        foreach (var policy in policies)
        {
            policy.Rules = rules[policy.SafeAccessPolicyKey].Select(r => new SafeAccessPolicyRule
            {
                RuleType = r.RuleType, MemberPattern = r.MemberPattern, SafeRoleKey = r.SafeRoleKey, SafeRoleName = r.SafeRoleName, RequirePeople = r.RequirePeople
            }).ToList();
        }
        return policies;
    }

    private sealed class RuleRow
    {
        public int SafeAccessPolicyKey { get; init; }
        public required string RuleType { get; init; }
        public required string MemberPattern { get; init; }
        public int? SafeRoleKey { get; init; }
        public string? SafeRoleName { get; init; }
        public bool RequirePeople { get; init; }
    }

    /// <summary>Adds (policyKey null) or replaces a policy and its rules. Returns its key, or null when the policy to replace is gone.</summary>
    public async Task<int?> SavePolicyAsync(int? policyKey, SaveSafeAccessPolicyRequest request, int userKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var name = request.PolicyName!.Trim();
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM web.safe_access_policy WHERE PolicyName = @name AND (@policyKey IS NULL OR SafeAccessPolicyKey <> @policyKey)",
                new { name, policyKey }, transaction) > 0)
            throw new SafeAccessConflictException($"There's already a policy called '{name}'.");
        var p = new
        {
            policyKey, PolicyName = name, SafeNamePattern = request.SafeNamePattern!.Trim(), request.SortOrder, request.IsEnabled,
            Description = Blank(request.Description), userKey
        };
        int? key = policyKey is null
            ? await connection.QuerySingleAsync<int>("""
                INSERT INTO web.safe_access_policy (PolicyName, SafeNamePattern, SortOrder, IsEnabled, Description, ModifiedBy)
                VALUES (@PolicyName, @SafeNamePattern, @SortOrder, @IsEnabled, @Description, @userKey);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, p, transaction)
            : await connection.ExecuteAsync("""
                UPDATE web.safe_access_policy SET PolicyName = @PolicyName, SafeNamePattern = @SafeNamePattern, SortOrder = @SortOrder,
                    IsEnabled = @IsEnabled, Description = @Description, ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
                WHERE SafeAccessPolicyKey = @policyKey
                """, p, transaction) == 0 ? null : policyKey;
        if (key is null) return null;
        await connection.ExecuteAsync("DELETE FROM web.safe_access_policy_rule WHERE SafeAccessPolicyKey = @key", new { key }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO web.safe_access_policy_rule (SafeAccessPolicyKey, RuleType, MemberPattern, SafeRoleKey, RequirePeople)
            VALUES (@key, @RuleType, @MemberPattern, @SafeRoleKey, @RequirePeople)
            """, request.Rules!.Select(r => new
        {
            key, r.RuleType, MemberPattern = r.MemberPattern!.Trim(), r.SafeRoleKey,
            RequirePeople = r.RuleType == SafeAccessPolicyRule.Required && r.RequirePeople
        }), transaction);
        transaction.Commit();
        return key;
    }

    /// <summary>Returns the policy's name, or null when there's no such policy.</summary>
    public async Task<string?> DeletePolicyAsync(int policyKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "DELETE FROM web.safe_access_policy OUTPUT deleted.PolicyName WHERE SafeAccessPolicyKey = @policyKey", new { policyKey });
    }

    /// <summary>Check now: runs the nightly check (dbo.usp_Check_SafeAccessPolicies) at once.</summary>
    public async Task CheckAsync()
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("dbo.usp_Check_SafeAccessPolicies", commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 600);
    }

    /// <summary>Reports > Safe Access: every checked Safe, failing first, with its problems and how many of its accounts are reviewed.</summary>
    public async Task<IReadOnlyList<SafeAccessReportRow>> GetReportAsync()
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT r.SafeKey, s.SafeName, ss.SourceSystemCode AS Source, p.PolicyName, r.IsCompliant, r.CheckedAt,
                   (SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.SafeKey = r.SafeKey AND fa.IsDeleted = 0) AS AccountCount,
                   (SELECT COUNT(*) FROM dbo.fact_account fa JOIN web.account_compliance_review acr ON acr.AccountKey = fa.AccountKey
                    WHERE fa.SafeKey = r.SafeKey AND fa.IsDeleted = 0) AS ReviewedCount
            FROM dbo.safe_access_result r
            JOIN dbo.dim_safe s ON s.SafeKey = r.SafeKey
            JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = s.SourceSystemKey
            LEFT JOIN web.safe_access_policy p ON p.SafeAccessPolicyKey = r.SafeAccessPolicyKey
            ORDER BY CASE r.IsCompliant WHEN 0 THEN 0 WHEN 1 THEN 1 ELSE 2 END, s.SafeName;

            SELECT SafeKey, Problem FROM dbo.safe_access_problem ORDER BY SafeKey, SafeAccessProblemKey;
            """);
        var rows = (await multi.ReadAsync<SafeAccessReportRow>()).ToList();
        var problems = (await multi.ReadAsync<(int SafeKey, string Problem)>()).ToLookup(p => p.SafeKey, p => p.Problem);
        foreach (var row in rows) row.Problems = problems[row.SafeKey].ToList();
        return rows;
    }

    /// <summary>The account's Safe check and review; null when there's no such account.</summary>
    public async Task<AccountCompliance?> GetAccountComplianceAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT s.SafeName, p.PolicyName, r.IsCompliant, r.CheckedAt,
                   u.DisplayName AS ReviewedByName, acr.ReviewedAt, acr.Note AS ReviewNote
            FROM dbo.fact_account fa
            LEFT JOIN dbo.dim_safe s ON s.SafeKey = fa.SafeKey
            LEFT JOIN dbo.safe_access_result r ON r.SafeKey = fa.SafeKey
            LEFT JOIN web.safe_access_policy p ON p.SafeAccessPolicyKey = r.SafeAccessPolicyKey
            LEFT JOIN web.account_compliance_review acr ON acr.AccountKey = fa.AccountKey
            LEFT JOIN web.app_user u ON u.UserKey = acr.ReviewedBy
            WHERE fa.AccountKey = @accountKey;

            SELECT pr.Problem FROM dbo.fact_account fa JOIN dbo.safe_access_problem pr ON pr.SafeKey = fa.SafeKey
            WHERE fa.AccountKey = @accountKey ORDER BY pr.SafeAccessProblemKey;
            """, new { accountKey });
        var compliance = await multi.ReadSingleOrDefaultAsync<AccountCompliance>();
        if (compliance is null) return null;
        compliance.Problems = (await multi.ReadAsync<string>()).ToList();
        return compliance;
    }

    /// <summary>Marks the account reviewed (or again, replacing the note). False when there's no such account.</summary>
    public async Task<bool> ReviewAccountAsync(long accountKey, int userKey, string? note)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("""
            MERGE web.account_compliance_review AS t
            USING (SELECT AccountKey, SafeKey FROM dbo.fact_account WHERE AccountKey = @accountKey) AS s ON t.AccountKey = s.AccountKey
            WHEN MATCHED THEN UPDATE SET SafeKey = s.SafeKey, ReviewedBy = @userKey, ReviewedAt = SYSUTCDATETIME(), Note = @note
            WHEN NOT MATCHED THEN INSERT (AccountKey, SafeKey, ReviewedBy, Note) VALUES (s.AccountKey, s.SafeKey, @userKey, @note);
            """, new { accountKey, userKey, note = Blank(note) }) > 0;
    }

    /// <summary>Clears the account's review. False when it had none.</summary>
    public async Task<bool> ClearReviewAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("DELETE FROM web.account_compliance_review WHERE AccountKey = @accountKey", new { accountKey }) > 0;
    }

    /// <summary>
    /// Marks every account (not deleted) in the Safe that isn't reviewed yet as reviewed: one review per account.
    /// Returns the account keys reviewed, or null when there's no such Safe.
    /// </summary>
    public async Task<IReadOnlyList<long>?> ReviewSafeAccountsAsync(int safeKey, int userKey, string? note)
    {
        using var connection = connectionFactory.Create();
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.dim_safe WHERE SafeKey = @safeKey", new { safeKey }) == 0) return null;
        return (await connection.QueryAsync<long>("""
            INSERT INTO web.account_compliance_review (AccountKey, SafeKey, ReviewedBy, Note)
            OUTPUT inserted.AccountKey
            SELECT fa.AccountKey, fa.SafeKey, @userKey, @note
            FROM dbo.fact_account fa
            WHERE fa.SafeKey = @safeKey AND fa.IsDeleted = 0
              AND NOT EXISTS (SELECT 1 FROM web.account_compliance_review r WHERE r.AccountKey = fa.AccountKey);
            """, new { safeKey, userKey, note = Blank(note) })).ToList();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
