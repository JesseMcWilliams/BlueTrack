/* ============================================================================
   73_BlueTrack_SafeAccessMovePermissions.sql

   RUN THIS AFTER 01-72. CREATE OR ALTER only -- safe to re-run.

   D-356 (the user, 2026-11-12: a Safe access policy failed on Self-Hosted
   Safes although the permissions matched on screen; its Safe roles named
   MoveAccountsAndFolders, and the members held MoveAccountsFrom and
   MoveAccountsInto). Privilege Cloud reports moving accounts as one
   permission (MoveAccountsAndFolders); Self-Hosted reports it as two
   (CAOMoveFrom and CAOMoveInto, loaded as MoveAccountsFrom and
   MoveAccountsInto). The Safe access check now treats them as the same:
   - dbo.fn_SafeAccessGranted: a Safe member's granted permissions, plus
     MoveAccountsFrom and MoveAccountsInto when it holds
     MoveAccountsAndFolders, plus MoveAccountsAndFolders when it holds both.
     A role's permissions are checked against this.
   - dbo.fn_SafeRolePermissions: a Safe role's permissions, plus
     MoveAccountsFrom and MoveAccountsInto when it names
     MoveAccountsAndFolders, plus MoveAccountsAndFolders when it names both.
     With Exact match, every permission the member holds must be in this.
   - dbo.usp_Check_SafeAccessPolicies: as in 57, with the two above.
   So a role naming MoveAccountsAndFolders is held by a Self-Hosted member
   with both moves, a role naming the two is held by a Privilege Cloud member
   with the one, and a role naming only one of the two is held (but not
   exactly) by a member with MoveAccountsAndFolders.
   ============================================================================ */
USE $DatabaseName$;
GO

CREATE OR ALTER FUNCTION dbo.fn_SafeAccessGranted(@EntitlementKey BIGINT)
RETURNS TABLE
AS
RETURN
WITH granted AS (
    SELECT bp.PermissionKey, p.PermissionName
    FROM dbo.bridge_entitlement_permission bp
    JOIN dbo.dim_permission p ON p.PermissionKey = bp.PermissionKey
    WHERE bp.EntitlementKey = @EntitlementKey AND bp.IsGranted = 1
)
SELECT PermissionKey FROM granted
UNION
SELECT p.PermissionKey FROM dbo.dim_permission p
WHERE p.PermissionName IN (N'MoveAccountsFrom', N'MoveAccountsInto')
  AND EXISTS (SELECT 1 FROM granted g WHERE g.PermissionName = N'MoveAccountsAndFolders')
UNION
SELECT p.PermissionKey FROM dbo.dim_permission p
WHERE p.PermissionName = N'MoveAccountsAndFolders'
  AND (SELECT COUNT(*) FROM granted g WHERE g.PermissionName IN (N'MoveAccountsFrom', N'MoveAccountsInto')) = 2;
GO

CREATE OR ALTER FUNCTION dbo.fn_SafeRolePermissions(@SafeRoleKey INT)
RETURNS TABLE
AS
RETURN
WITH named AS (
    SELECT rp.PermissionKey, p.PermissionName
    FROM web.safe_role_permission rp
    JOIN dbo.dim_permission p ON p.PermissionKey = rp.PermissionKey
    WHERE rp.SafeRoleKey = @SafeRoleKey
)
SELECT PermissionKey FROM named
UNION
SELECT p.PermissionKey FROM dbo.dim_permission p
WHERE p.PermissionName IN (N'MoveAccountsFrom', N'MoveAccountsInto')
  AND EXISTS (SELECT 1 FROM named n WHERE n.PermissionName = N'MoveAccountsAndFolders')
UNION
SELECT p.PermissionKey FROM dbo.dim_permission p
WHERE p.PermissionName = N'MoveAccountsAndFolders'
  AND (SELECT COUNT(*) FROM named n WHERE n.PermissionName IN (N'MoveAccountsFrom', N'MoveAccountsInto')) = 2;
GO

/* D-300: checks every Safe that has an account not deleted against the first enabled policy (in SortOrder) whose Safe
   name pattern matches it, replacing dbo.safe_access_result and dbo.safe_access_problem. D-301: forgets the review of
   an account that has moved to another Safe. D-356: the move permissions compare across sources (see the header).
   Called by usp_RunFullLoad (before the Progress Rules) and by Check now. */
CREATE OR ALTER PROCEDURE dbo.usp_Check_SafeAccessPolicies
AS
BEGIN
    SET NOCOUNT ON;

    DELETE r FROM web.account_compliance_review r
    JOIN dbo.fact_account fa ON fa.AccountKey = r.AccountKey
    WHERE ISNULL(fa.SafeKey, -1) <> ISNULL(r.SafeKey, -1);

    SELECT s.SafeKey, s.SafeName,
           (SELECT TOP (1) p.SafeAccessPolicyKey FROM web.safe_access_policy p
            WHERE p.IsEnabled = 1 AND s.SafeName LIKE dbo.fn_SafeAccessLike(p.SafeNamePattern, NULL)
            ORDER BY p.SortOrder, p.SafeAccessPolicyKey) AS SafeAccessPolicyKey
    INTO #safes
    FROM dbo.dim_safe s
    WHERE EXISTS (SELECT 1 FROM dbo.fact_account fa WHERE fa.SafeKey = s.SafeKey AND fa.IsDeleted = 0);

    /* Each Safe member with its name, and for each rule of the Safe's policy whose pattern it matches, whether it
       holds the rule's role (always, for a rule with none) and whether it has people (a user is one). */
    SELECT sf.SafeKey, r.SafeAccessPolicyRuleKey, e.EntitlementKey, COALESCE(u.Username, g.GroupName) AS MemberName,
           CAST(CASE WHEN r.SafeRoleKey IS NULL OR (
                    NOT EXISTS (SELECT 1 FROM web.safe_role_permission rp WHERE rp.SafeRoleKey = r.SafeRoleKey
                                AND NOT EXISTS (SELECT 1 FROM dbo.fn_SafeAccessGranted(e.EntitlementKey) ga WHERE ga.PermissionKey = rp.PermissionKey))
                    AND (sr.ExactMatch = 0 OR NOT EXISTS (
                        SELECT 1 FROM dbo.bridge_entitlement_permission bp
                        WHERE bp.EntitlementKey = e.EntitlementKey AND bp.IsGranted = 1
                          AND NOT EXISTS (SELECT 1 FROM dbo.fn_SafeRolePermissions(r.SafeRoleKey) rp WHERE rp.PermissionKey = bp.PermissionKey))))
                THEN 1 ELSE 0 END AS BIT) AS HoldsRole,
           CAST(CASE WHEN e.MemberType = 'User' OR EXISTS (SELECT 1 FROM dbo.vw_safe_access_people pp WHERE pp.EntitlementKey = e.EntitlementKey)
                THEN 1 ELSE 0 END AS BIT) AS HasPeople
    INTO #matches
    FROM #safes sf
    JOIN web.safe_access_policy_rule r ON r.SafeAccessPolicyKey = sf.SafeAccessPolicyKey
    LEFT JOIN web.safe_role sr ON sr.SafeRoleKey = r.SafeRoleKey
    JOIN dbo.fact_safe_entitlement e ON e.SafeKey = sf.SafeKey
    LEFT JOIN dbo.dim_user u ON u.UserKey = e.UserKey
    LEFT JOIN dbo.dim_group g ON g.GroupKey = e.GroupKey
    WHERE COALESCE(u.Username, g.GroupName) LIKE dbo.fn_SafeAccessLike(r.MemberPattern, sf.SafeName);

    DECLARE @problems TABLE (SafeKey INT, SafeAccessPolicyRuleKey INT, Problem NVARCHAR(1000));

    /* Required: a matching member that holds the role (and has people, when asked). Otherwise say why. */
    INSERT INTO @problems (SafeKey, SafeAccessPolicyRuleKey, Problem)
    SELECT sf.SafeKey, r.SafeAccessPolicyRuleKey, LEFT(
        CASE
            WHEN NOT EXISTS (SELECT 1 FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey)
                THEN CONCAT(N'No member matches ', REPLACE(r.MemberPattern, N'{SafeName}', sf.SafeName), N' (needs ', sr.RoleName, N').')
            WHEN NOT EXISTS (SELECT 1 FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey AND m.HoldsRole = 1)
                THEN CONCAT((SELECT STRING_AGG(m.MemberName, N', ') FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey),
                            N': doesn''t hold ', sr.RoleName, CASE WHEN sr.ExactMatch = 1 THEN N' (exactly)' ELSE N'' END, N'.')
            ELSE CONCAT((SELECT STRING_AGG(m.MemberName, N', ') FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey AND m.HoldsRole = 1),
                        N': has no people in it (Vault or AD).')
        END, 1000)
    FROM #safes sf
    JOIN web.safe_access_policy_rule r ON r.SafeAccessPolicyKey = sf.SafeAccessPolicyKey AND r.RuleType = N'Required'
    JOIN web.safe_role sr ON sr.SafeRoleKey = r.SafeRoleKey
    WHERE NOT EXISTS (SELECT 1 FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey
                      AND m.HoldsRole = 1 AND (r.RequirePeople = 0 OR m.HasPeople = 1));

    /* Forbidden: each matching member (holding the role, when the rule names one). */
    INSERT INTO @problems (SafeKey, SafeAccessPolicyRuleKey, Problem)
    SELECT m.SafeKey, m.SafeAccessPolicyRuleKey, LEFT(
        CASE WHEN sr.RoleName IS NULL THEN CONCAT(m.MemberName, N': must not be a member.')
             ELSE CONCAT(m.MemberName, N': must not hold ', sr.RoleName, N'.') END, 1000)
    FROM #matches m
    JOIN web.safe_access_policy_rule r ON r.SafeAccessPolicyRuleKey = m.SafeAccessPolicyRuleKey AND r.RuleType = N'Forbidden'
    LEFT JOIN web.safe_role sr ON sr.SafeRoleKey = r.SafeRoleKey
    WHERE m.HoldsRole = 1;

    DELETE FROM dbo.safe_access_problem;
    INSERT INTO dbo.safe_access_problem (SafeKey, SafeAccessPolicyRuleKey, Problem)
    SELECT SafeKey, SafeAccessPolicyRuleKey, Problem FROM @problems;

    DELETE FROM dbo.safe_access_result;
    INSERT INTO dbo.safe_access_result (SafeKey, SafeAccessPolicyKey, IsCompliant, CheckedAt)
    SELECT sf.SafeKey, sf.SafeAccessPolicyKey,
           CASE WHEN sf.SafeAccessPolicyKey IS NULL THEN NULL
                WHEN EXISTS (SELECT 1 FROM @problems p WHERE p.SafeKey = sf.SafeKey) THEN 0 ELSE 1 END,
           SYSUTCDATETIME()
    FROM #safes sf;
END
GO
