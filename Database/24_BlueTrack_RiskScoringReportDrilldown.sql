/* ============================================================================
   24_BlueTrack_RiskScoringReportDrilldown.sql

   RUN THIS AFTER 01-23. Safe to re-run (CREATE OR ALTER -- pure logic, no
   data of its own).

   Design_Risk_Scoring.md, D-101-105, D-119 Phase E: the new Risk Score
   report's drill-down needs to show which real Targets/Access Groups are
   contributing to an Account's score, not just the bare numeric values
   web.ufn_ReachableRiskValues (23_BlueTrack_RiskScoringCalculation.sql)
   already returns for the scoring procedures. Rather than duplicating its
   reachability CTEs in a second function (real drift risk -- the two would
   need to be kept in lockstep by hand), this redefines the SAME function to
   also return EntityType/EntityKey/EntityName alongside the original
   RiskValue column. Backward compatible: usp_CalculateRiskScore_DominantPlusTail
   and usp_CalculateRiskScore_CombinedExposure only ever reference the
   RiskValue column by name (SELECT RiskValue.../WHERE RiskValue...), so the
   extra columns are inert to them.

   Redefining an already-applied script's function via CREATE OR ALTER in a
   later script, rather than editing 23 in place, also isn't just a D-58
   style preference here -- DbUp's journal records a script as done by
   filename only, so editing 23's already-applied content wouldn't actually
   re-run against any environment that already applied it.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER FUNCTION web.ufn_ReachableRiskValues (@TargetSetKey NVARCHAR(20), @EntityKey BIGINT)
RETURNS TABLE
AS
RETURN
(
    WITH ReachableTargetKeys AS (
        -- Account: every Target reachable through any Access Group it belongs to...
        SELECT DISTINCT t.TargetKey
        FROM web.account_access_group_map aagm
        JOIN web.access_group_target_map agtm ON agtm.AccessGroupKey = aagm.AccessGroupKey
        JOIN web.dim_target t ON t.TargetKey = agtm.TargetKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey

        UNION
        -- ...plus, for any Local-scope group it belongs to, the target that group itself lives on...
        SELECT DISTINCT t.TargetKey
        FROM web.account_access_group_map aagm
        JOIN web.dim_access_group ag ON ag.AccessGroupKey = aagm.AccessGroupKey
        JOIN web.dim_target t ON t.TargetKey = ag.FoundOnTargetKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey AND ag.GroupScope = 'Local' AND ag.FoundOnTargetKey IS NOT NULL

        UNION
        -- ...plus every Target reached directly, bypassing any group.
        SELECT DISTINCT t.TargetKey
        FROM web.account_target_map atm
        JOIN web.dim_target t ON t.TargetKey = atm.TargetKey
        WHERE @TargetSetKey = 'Account' AND atm.AccountKey = @EntityKey

        UNION
        -- Access Group: every Target it's mapped to...
        SELECT DISTINCT t.TargetKey
        FROM web.access_group_target_map agtm
        JOIN web.dim_target t ON t.TargetKey = agtm.TargetKey
        WHERE @TargetSetKey = 'AccessGroup' AND agtm.AccessGroupKey = @EntityKey

        UNION
        -- ...plus, if Local scope, the target it lives on.
        SELECT DISTINCT t.TargetKey
        FROM web.dim_access_group ag
        JOIN web.dim_target t ON t.TargetKey = ag.FoundOnTargetKey
        WHERE @TargetSetKey = 'AccessGroup' AND ag.AccessGroupKey = @EntityKey AND ag.GroupScope = 'Local' AND ag.FoundOnTargetKey IS NOT NULL
    ),
    ReachableGroupKeys AS (
        -- Account: every Access Group it belongs to, for that group's own BaseRiskScore.
        SELECT DISTINCT ag.AccessGroupKey
        FROM web.account_access_group_map aagm
        JOIN web.dim_access_group ag ON ag.AccessGroupKey = aagm.AccessGroupKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey

        UNION
        -- Access Group: itself, for its own BaseRiskScore.
        SELECT @EntityKey WHERE @TargetSetKey = 'AccessGroup'
    )
    SELECT 'Target' AS EntityType, t.TargetKey AS EntityKey, t.TargetName AS EntityName, t.RiskScore AS RiskValue
    FROM ReachableTargetKeys rtk JOIN web.dim_target t ON t.TargetKey = rtk.TargetKey
    UNION ALL
    SELECT 'AccessGroup' AS EntityType, ag.AccessGroupKey AS EntityKey, ag.GroupName AS EntityName, ag.BaseRiskScore AS RiskValue
    FROM ReachableGroupKeys rgk JOIN web.dim_access_group ag ON ag.AccessGroupKey = rgk.AccessGroupKey
);
GO

PRINT '24_BlueTrack_RiskScoringReportDrilldown.sql complete.';
