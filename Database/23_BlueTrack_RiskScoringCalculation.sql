/* ============================================================================
   23_BlueTrack_RiskScoringCalculation.sql

   RUN THIS AFTER 01-22. Safe to re-run (CREATE OR ALTER throughout -- pure
   logic, no data of its own).

   Design_Risk_Scoring.md, D-101-105, D-119 Phase D: the scoring algorithm
   itself. Like Phase C (and unlike Phase B's per-import matching logic),
   this genuinely needs to be T-SQL -- usp_RecalculateRiskScores runs
   automatically inside the existing usp_RunFullLoad SQL Agent job, a pure
   T-SQL pipeline with no API/C# process involved.

   web.ufn_ReachableRiskValues resolves an interpretive detail the design
   doc's own prose left implicit: an Account's reachable-value set is built
   by flattening all the way down to real Targets/Access-Group-base-scores,
   deduplicated by the real-world entity they came from (a Target reached
   through two different group memberships counts once; two different
   groups' own BaseRiskScore each count once, since those are distinct
   signals, not the same reachable item) -- not by first collapsing each
   group to its own already-computed score and treating that as one more
   opaque value. This reading is the only one consistent with the design
   doc's own worked example: "if an account reaches the same high-risk
   domain controller through two different group memberships, that
   target's risk must count once, not twice" only makes sense if dedup
   happens at the Target level, which requires flattening through groups
   to their underlying Targets rather than stopping at each group's own
   pre-computed score.

   Candidate A's decay constant (0.4, the doc's own "0.3-0.5" range's
   midpoint) is not admin-configurable -- only the algorithm CHOICE is
   (web.app_config.ActiveRiskAlgorithm, added in Phase A's schema script).
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
    SELECT t.RiskScore AS RiskValue FROM ReachableTargetKeys rtk JOIN web.dim_target t ON t.TargetKey = rtk.TargetKey
    UNION ALL
    SELECT ag.BaseRiskScore FROM ReachableGroupKeys rgk JOIN web.dim_access_group ag ON ag.AccessGroupKey = rgk.AccessGroupKey
);
GO

-- Candidate A -- "dominant risk plus a shrinking tail". Sort descending,
-- score = min(1000, r1 + sum(r_i * decay^(i-1)) for i >= 2).
CREATE OR ALTER PROCEDURE usp_CalculateRiskScore_DominantPlusTail
    @TargetSetKey NVARCHAR(20),
    @EntityKey BIGINT,
    @Score INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Decay FLOAT = 0.4;

    ;WITH Ranked AS (
        SELECT RiskValue, ROW_NUMBER() OVER (ORDER BY RiskValue DESC) AS RankNum
        FROM web.ufn_ReachableRiskValues(@TargetSetKey, @EntityKey)
    )
    SELECT @Score = SUM(CASE WHEN RankNum = 1 THEN RiskValue ELSE CAST(ROUND(RiskValue * POWER(@Decay, RankNum - 1), 0) AS INT) END)
    FROM Ranked;

    SET @Score = ISNULL(@Score, 0);
    IF @Score > 1000 SET @Score = 1000;
    IF @Score < 0 SET @Score = 0;
END
GO

-- Candidate B -- "combined exposure probability". Each value/1000 treated
-- as an independent exposure probability; score = 1000 * (1 - product of
-- (1 - r_i/1000)). Self-bounding by construction, no cap needed.
CREATE OR ALTER PROCEDURE usp_CalculateRiskScore_CombinedExposure
    @TargetSetKey NVARCHAR(20),
    @EntityKey BIGINT,
    @Score INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM web.ufn_ReachableRiskValues(@TargetSetKey, @EntityKey) WHERE RiskValue >= 1000)
    BEGIN
        SET @Score = 1000;
        RETURN;
    END

    DECLARE @LogProduct FLOAT;
    SELECT @LogProduct = SUM(LOG(1.0 - RiskValue / 1000.0))
    FROM web.ufn_ReachableRiskValues(@TargetSetKey, @EntityKey)
    WHERE RiskValue < 1000;

    IF @LogProduct IS NULL
    BEGIN
        SET @Score = 0;
        RETURN;
    END

    SET @Score = CAST(ROUND(1000.0 * (1.0 - EXP(@LogProduct)), 0) AS INT);
END
GO

-- Top-level dispatcher -- callers never call a candidate directly, so
-- comparing algorithms live is an admin-configurable switch, not a
-- deployment (web.app_config.ActiveRiskAlgorithm).
CREATE OR ALTER PROCEDURE usp_CalculateRiskScore
    @TargetSetKey NVARCHAR(20),
    @EntityKey BIGINT,
    @Score INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Algorithm NVARCHAR(50) = (SELECT ActiveRiskAlgorithm FROM web.app_config);

    IF @Algorithm = 'DominantPlusTail'
        EXEC usp_CalculateRiskScore_DominantPlusTail @TargetSetKey, @EntityKey, @Score OUTPUT;
    ELSE IF @Algorithm = 'CombinedExposure'
        EXEC usp_CalculateRiskScore_CombinedExposure @TargetSetKey, @EntityKey, @Score OUTPUT;
    ELSE
        SET @Score = 0;
END
GO

-- Recomputes every stale Access Group first (Accounts depend on their
-- current ComputedRiskScore... actually Accounts flatten straight through
-- to Targets/BaseRiskScore per ufn_ReachableRiskValues above, not through
-- a group's own ComputedRiskScore -- but Access Groups still go first
-- so their own ComputedRiskScore/staleness stays current for direct
-- display on the Access Groups admin page regardless). Never touches
-- OverrideRiskScore -- an analyst override stays put until explicitly
-- changed, regardless of how often the computed value refreshes.
CREATE OR ALTER PROCEDURE usp_RecalculateRiskScores
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AccessGroupKey INT, @GroupScore INT;
    DECLARE GroupCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT AccessGroupKey FROM web.dim_access_group WHERE IsRiskScoreStale = 1;
    OPEN GroupCursor;
    FETCH NEXT FROM GroupCursor INTO @AccessGroupKey;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC usp_CalculateRiskScore 'AccessGroup', @AccessGroupKey, @GroupScore OUTPUT;
        UPDATE web.dim_access_group
        SET ComputedRiskScore = @GroupScore, RiskScoreCalculatedDate = SYSUTCDATETIME(), IsRiskScoreStale = 0
        WHERE AccessGroupKey = @AccessGroupKey;
        FETCH NEXT FROM GroupCursor INTO @AccessGroupKey;
    END
    CLOSE GroupCursor;
    DEALLOCATE GroupCursor;

    DECLARE @AccountKey BIGINT, @AccountScore INT;
    DECLARE AccountCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT AccountKey FROM web.account_risk_score WHERE IsRiskScoreStale = 1;
    OPEN AccountCursor;
    FETCH NEXT FROM AccountCursor INTO @AccountKey;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC usp_CalculateRiskScore 'Account', @AccountKey, @AccountScore OUTPUT;
        UPDATE web.account_risk_score
        SET ComputedRiskScore = @AccountScore, RiskScoreCalculatedDate = SYSUTCDATETIME(), IsRiskScoreStale = 0
        WHERE AccountKey = @AccountKey;
        FETCH NEXT FROM AccountCursor INTO @AccountKey;
    END
    CLOSE AccountCursor;
    DEALLOCATE AccountCursor;
END
GO

-- Wire into the existing nightly orchestration, after the mapping tables
-- (including Phase C's PendingSafeDerived step) have had a chance to change.
CREATE OR ALTER PROCEDURE usp_RunFullLoad
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        EXEC usp_Load_DimLocation;
        EXEC usp_Load_DimPlatform;
        EXEC usp_Load_DimSafe;
        EXEC usp_Load_DimUser;
        EXEC usp_Load_DimGroup;
        EXEC usp_Load_GroupMembership;
        EXEC usp_Load_FactAccount;
        EXEC usp_Load_FactAccountProgress;
        EXEC usp_Load_AccountProgressAutoAdvance;
        EXEC usp_Load_FactSafeEntitlement;
        EXEC usp_Load_AccountReconciliation;
        EXEC usp_Load_FactAccountProgressHistory;
        EXEC usp_DeriveAccountTargetMap_FromPendingSafes;
        EXEC usp_RecalculateRiskScores;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

PRINT '23_BlueTrack_RiskScoringCalculation.sql complete.';
