/* ============================================================================
   Tools/Sql/SafeAccessPolicyCheck.sql (D-356)

   Why does a Safe pass or fail its Safe access policy? Read-only: it changes
   nothing. Run it in the BlueTrack database (SSMS, or sqlcmd -d BlueTrack)
   as an account that can read the dbo and web schemas.

   Set @SafeName below. Leave @PolicyName NULL to use the policy the check
   picks (the first enabled one, by Sort order, whose Safe name pattern
   matches); or name a policy to test the Safe against it, even a disabled
   one. It mirrors dbo.usp_Check_SafeAccessPolicies (Database/57, 73),
   including the move permissions counting the same across sources.

   1. The Safe (one row per source), the policy picked now, the last check.
   2. The last check's problem lines.
   3. Every member against every rule: the name the check compares (NULL
      for a member that wasn't resolved to a user or group), whether it
      matches the rule's pattern, the role's permissions it lacks, the
      permissions it holds outside the role (these fail Exact match), and
      whether it has people in it. A member meets a rule when MatchesPattern
      is Yes, RolePermissionsMissing is NULL and, for an Exact match role,
      GrantedOutsideRole is NULL.
   4. Each member's permission rows as loaded, to compare with CyberArk.
   ============================================================================ */
SET NOCOUNT ON;
DECLARE @SafeName   NVARCHAR(300) = N'<Safe name>';
DECLARE @PolicyName NVARCHAR(200) = NULL;

/* 1. The Safe (one row per source), the policy the check picks now, and the last check's result. */
SELECT s.SafeKey, ss.SourceSystemCode AS Source, s.SafeName,
       pnow.PolicyName AS PolicyPickedNow, plast.PolicyName AS PolicyAtLastCheck,
       r.IsCompliant, r.CheckedAt,
       (SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.SafeKey = s.SafeKey AND fa.IsDeleted = 0) AS ActiveAccounts
FROM dbo.dim_safe s
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = s.SourceSystemKey
LEFT JOIN dbo.safe_access_result r ON r.SafeKey = s.SafeKey
LEFT JOIN web.safe_access_policy plast ON plast.SafeAccessPolicyKey = r.SafeAccessPolicyKey
OUTER APPLY (SELECT TOP (1) p.PolicyName FROM web.safe_access_policy p
             WHERE p.IsEnabled = 1 AND s.SafeName LIKE dbo.fn_SafeAccessLike(p.SafeNamePattern, NULL)
             ORDER BY p.SortOrder, p.SafeAccessPolicyKey) pnow
WHERE s.SafeName = @SafeName;

/* 2. The last check's problem lines. */
SELECT s.SafeKey, pr.Problem
FROM dbo.dim_safe s JOIN dbo.safe_access_problem pr ON pr.SafeKey = s.SafeKey
WHERE s.SafeName = @SafeName
ORDER BY s.SafeKey, pr.SafeAccessProblemKey;

/* 3. Every member of the Safe against every rule of the policy: the name the check compares (Username or GroupName,
   NULL for a member that wasn't resolved), whether it matches the rule's pattern, the role's permissions it lacks,
   the granted permissions outside the role (these fail an ExactMatch role), and whether it has people. */
WITH safe AS (
    SELECT s.SafeKey, s.SafeName,
           COALESCE((SELECT p.SafeAccessPolicyKey FROM web.safe_access_policy p WHERE p.PolicyName = @PolicyName),
                    (SELECT TOP (1) p.SafeAccessPolicyKey FROM web.safe_access_policy p
                     WHERE p.IsEnabled = 1 AND s.SafeName LIKE dbo.fn_SafeAccessLike(p.SafeNamePattern, NULL)
                     ORDER BY p.SortOrder, p.SafeAccessPolicyKey)) AS SafeAccessPolicyKey
    FROM dbo.dim_safe s
    WHERE s.SafeName = @SafeName
)
SELECT sf.SafeKey, r.RuleType, r.MemberPattern, dbo.fn_SafeAccessLike(r.MemberPattern, sf.SafeName) AS LikePattern,
       sr.RoleName, sr.ExactMatch, r.RequirePeople,
       e.EntitlementKey, e.MemberType,
       COALESCE(u.Username, g.GroupName) AS NameTheCheckCompares,
       e.MemberName AS NameFromSource, e.ResolutionMethod,
       CASE WHEN COALESCE(u.Username, g.GroupName) LIKE dbo.fn_SafeAccessLike(r.MemberPattern, sf.SafeName) THEN 'Yes' ELSE 'No' END AS MatchesPattern,
       (SELECT STRING_AGG(p.PermissionName, N', ') WITHIN GROUP (ORDER BY p.PermissionKey)
        FROM web.safe_role_permission rp JOIN dbo.dim_permission p ON p.PermissionKey = rp.PermissionKey
        WHERE rp.SafeRoleKey = r.SafeRoleKey
          AND NOT EXISTS (SELECT 1 FROM dbo.fn_SafeAccessGranted(e.EntitlementKey) ga WHERE ga.PermissionKey = rp.PermissionKey)) AS RolePermissionsMissing,
       (SELECT STRING_AGG(p.PermissionName, N', ') WITHIN GROUP (ORDER BY p.PermissionKey)
        FROM dbo.bridge_entitlement_permission bp JOIN dbo.dim_permission p ON p.PermissionKey = bp.PermissionKey
        WHERE bp.EntitlementKey = e.EntitlementKey AND bp.IsGranted = 1 AND r.SafeRoleKey IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.fn_SafeRolePermissions(r.SafeRoleKey) rp WHERE rp.PermissionKey = bp.PermissionKey)) AS GrantedOutsideRole,
       CASE WHEN e.MemberType = 'User' OR EXISTS (SELECT 1 FROM dbo.vw_safe_access_people pp WHERE pp.EntitlementKey = e.EntitlementKey)
            THEN 'Yes' ELSE 'No' END AS HasPeople
FROM safe sf
JOIN web.safe_access_policy_rule r ON r.SafeAccessPolicyKey = sf.SafeAccessPolicyKey
LEFT JOIN web.safe_role sr ON sr.SafeRoleKey = r.SafeRoleKey
JOIN dbo.fact_safe_entitlement e ON e.SafeKey = sf.SafeKey
LEFT JOIN dbo.dim_user u ON u.UserKey = e.UserKey
LEFT JOIN dbo.dim_group g ON g.GroupKey = e.GroupKey
ORDER BY sf.SafeKey, r.SafeAccessPolicyRuleKey, MatchesPattern DESC, NameTheCheckCompares;

/* 4. Each member's permission rows as loaded (granted and not), to compare with what CyberArk shows. */
SELECT s.SafeKey, e.EntitlementKey, COALESCE(u.Username, g.GroupName, e.MemberName) AS Member, p.PermissionName, bp.IsGranted
FROM dbo.dim_safe s
JOIN dbo.fact_safe_entitlement e ON e.SafeKey = s.SafeKey
LEFT JOIN dbo.dim_user u ON u.UserKey = e.UserKey
LEFT JOIN dbo.dim_group g ON g.GroupKey = e.GroupKey
JOIN dbo.bridge_entitlement_permission bp ON bp.EntitlementKey = e.EntitlementKey
JOIN dbo.dim_permission p ON p.PermissionKey = bp.PermissionKey
WHERE s.SafeName = @SafeName
ORDER BY s.SafeKey, Member, p.PermissionKey;
