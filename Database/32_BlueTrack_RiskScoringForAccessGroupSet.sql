/* ============================================================================
   32_BlueTrack_RiskScoringForAccessGroupSet.sql

   RUN THIS AFTER 01-31. Guarded (TYPE_ID check for the new type; CREATE OR
   ALTER for the function/procedures, pure logic with no data of their own) --
   safe to re-run, per D-58's normal incremental-script convention.

   AD Account Discovery feature (2026-09-16): the existing
   usp_CalculateRiskScore/web.ufn_ReachableRiskValues (Database/23) can't
   score a candidate AD account that isn't onboarded into CyberArk yet --
   for @TargetSetKey='Account' that function's reachability CTE joins
   through web.account_access_group_map/web.account_target_map, both
   NOT NULL FOREIGN KEY REFERENCES dbo.fact_account(AccountKey). A candidate
   that isn't in fact_account can't even be staged into those mapping
   tables, let alone scored through the existing function.

   This adds a parallel path that takes a set of AccessGroupKeys directly --
   the candidate's real AD group memberships, matched against
   web.dim_access_group by the discovery job (App/Api/AdDiscovery/), never
   written into account_access_group_map at all. web.ufn_ReachableRiskValues
   itself is untouched; this is purely additive, no risk to the existing
   account/access-group scoring path.

   web.AccessGroupKeyList is this codebase's first table-valued parameter
   type -- called out since it's a new pattern here, not an established one
   being copied.
   ============================================================================ */

USE $DatabaseName$;
GO

IF TYPE_ID(N'web.AccessGroupKeyList') IS NULL
BEGIN
    EXEC('CREATE TYPE web.AccessGroupKeyList AS TABLE (AccessGroupKey INT NOT NULL PRIMARY KEY)');
END
GO

-- Same flattening semantics as web.ufn_ReachableRiskValues's own
-- @TargetSetKey='AccessGroup' branch (Database/23) -- every Target each
-- group is mapped to, plus a Local-scope group's own host Target, plus each
-- group's own BaseRiskScore -- just unioned across every key in the passed
-- set instead of a single @EntityKey. Values still deduplicated by
-- real-world Target/Group identity, matching that file's own dedup reasoning
-- (a Target reachable through two different candidate-matched groups counts
-- once, not twice).
CREATE OR ALTER FUNCTION web.ufn_ReachableRiskValues_ForAccessGroupSet (@AccessGroupKeys web.AccessGroupKeyList READONLY)
RETURNS TABLE
AS
RETURN
(
    WITH ReachableTargetKeys AS (
        SELECT DISTINCT t.TargetKey
        FROM @AccessGroupKeys agk
        JOIN web.access_group_target_map agtm ON agtm.AccessGroupKey = agk.AccessGroupKey
        JOIN web.dim_target t ON t.TargetKey = agtm.TargetKey

        UNION
        SELECT DISTINCT t.TargetKey
        FROM @AccessGroupKeys agk
        JOIN web.dim_access_group ag ON ag.AccessGroupKey = agk.AccessGroupKey
        JOIN web.dim_target t ON t.TargetKey = ag.FoundOnTargetKey
        WHERE ag.GroupScope = 'Local' AND ag.FoundOnTargetKey IS NOT NULL
    ),
    ReachableGroupKeys AS (
        SELECT DISTINCT AccessGroupKey FROM @AccessGroupKeys
    )
    SELECT t.RiskScore AS RiskValue FROM ReachableTargetKeys rtk JOIN web.dim_target t ON t.TargetKey = rtk.TargetKey
    UNION ALL
    SELECT ag.BaseRiskScore FROM ReachableGroupKeys rgk JOIN web.dim_access_group ag ON ag.AccessGroupKey = rgk.AccessGroupKey
);
GO

-- Candidate A -- identical math to usp_CalculateRiskScore_DominantPlusTail
-- (Database/23), just sourced from the AccessGroupSet function above.
CREATE OR ALTER PROCEDURE usp_CalculateRiskScore_DominantPlusTail_ForAccessGroupSet
    @AccessGroupKeys web.AccessGroupKeyList READONLY,
    @Score INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Decay FLOAT = 0.4;

    ;WITH Ranked AS (
        SELECT RiskValue, ROW_NUMBER() OVER (ORDER BY RiskValue DESC) AS RankNum
        FROM web.ufn_ReachableRiskValues_ForAccessGroupSet(@AccessGroupKeys)
    )
    SELECT @Score = SUM(CASE WHEN RankNum = 1 THEN RiskValue ELSE CAST(ROUND(RiskValue * POWER(@Decay, RankNum - 1), 0) AS INT) END)
    FROM Ranked;

    SET @Score = ISNULL(@Score, 0);
    IF @Score > 1000 SET @Score = 1000;
    IF @Score < 0 SET @Score = 0;
END
GO

-- Candidate B -- identical math to usp_CalculateRiskScore_CombinedExposure (Database/23).
CREATE OR ALTER PROCEDURE usp_CalculateRiskScore_CombinedExposure_ForAccessGroupSet
    @AccessGroupKeys web.AccessGroupKeyList READONLY,
    @Score INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM web.ufn_ReachableRiskValues_ForAccessGroupSet(@AccessGroupKeys) WHERE RiskValue >= 1000)
    BEGIN
        SET @Score = 1000;
        RETURN;
    END

    DECLARE @LogProduct FLOAT;
    SELECT @LogProduct = SUM(LOG(1.0 - RiskValue / 1000.0))
    FROM web.ufn_ReachableRiskValues_ForAccessGroupSet(@AccessGroupKeys)
    WHERE RiskValue < 1000;

    IF @LogProduct IS NULL
    BEGIN
        SET @Score = 0;
        RETURN;
    END

    SET @Score = CAST(ROUND(1000.0 * (1.0 - EXP(@LogProduct)), 0) AS INT);
END
GO

-- Top-level dispatcher, mirroring usp_CalculateRiskScore's own structure --
-- same web.app_config.ActiveRiskAlgorithm switch, so a candidate account's
-- score always uses the same algorithm choice as every other scored entity.
CREATE OR ALTER PROCEDURE usp_CalculateRiskScoreForAccessGroupSet
    @AccessGroupKeys web.AccessGroupKeyList READONLY,
    @Score INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Algorithm NVARCHAR(50) = (SELECT ActiveRiskAlgorithm FROM web.app_config);

    IF @Algorithm = 'DominantPlusTail'
        EXEC usp_CalculateRiskScore_DominantPlusTail_ForAccessGroupSet @AccessGroupKeys, @Score OUTPUT;
    ELSE IF @Algorithm = 'CombinedExposure'
        EXEC usp_CalculateRiskScore_CombinedExposure_ForAccessGroupSet @AccessGroupKeys, @Score OUTPUT;
    ELSE
        SET @Score = 0;
END
GO

PRINT '32_BlueTrack_RiskScoringForAccessGroupSet.sql complete.';
