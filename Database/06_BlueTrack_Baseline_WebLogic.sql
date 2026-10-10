/* ============================================================================
   06_BlueTrack_Baseline_WebLogic.sql

   BASELINE (D-195, 2026-10-09). One of the six scripts that replaced the
   numbered scripts 01-51 before the first release. It builds the schema those
   scripts left behind, in final form: later columns and constraints are part
   of each CREATE TABLE, and each procedure, view and function appears once,
   as its last version. The history of every change is in git and in
   Claude_Docs/Design_Decision-Register.md.

   Runs only against an EMPTY database (App/Migrator gives it one). A database
   built from the old scripts has these baseline scripts marked as applied by
   App/Migrator instead of running them (see its header).

   Procedures, functions and views over the web schema: risk scoring
   (Phase C's pending-safe links, the two scoring algorithms and their
   dispatcher, whole-database and single-account recalculation, scoring a set
   of Access Groups), the audit-log purge, the decommissioning views (D-186),
   Targets and links from account addresses (D-123, D-193), and
   usp_RunFullLoad -- the nightly orchestrator that runs every load step in
   order and then recalculates risk scores.
   ============================================================================ */

USE $DatabaseName$;
GO


CREATE OR ALTER PROCEDURE usp_DeriveAccountTargetMap_FromPendingSafes
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH BestMatch AS (
        SELECT
            fa.AccountKey,
            ti.TargetKey,
            ROW_NUMBER() OVER (PARTITION BY fa.AccountKey ORDER BY dt.MatchPriority ASC) AS MatchRank
        FROM fact_account fa
        JOIN dim_safe ds ON ds.SafeKey = fa.SafeKey
        JOIN web.target_identifier ti ON ti.IdentifierValue = fa.Address
        JOIN web.dim_target_identifier_type dt ON dt.IdentifierType = ti.IdentifierType
        WHERE ds.SafeName LIKE '%[_]Pending%'
          AND fa.IsDeleted = 0
          AND fa.Address IS NOT NULL
    )
    INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod, LoadTimestamp)
    SELECT bm.AccountKey, bm.TargetKey, 'PendingSafeDerived', SYSUTCDATETIME()
    FROM BestMatch bm
    WHERE bm.MatchRank = 1
      AND NOT EXISTS (
          SELECT 1 FROM web.account_target_map atm
          WHERE atm.AccountKey = bm.AccountKey AND atm.TargetKey = bm.TargetKey
      );

    -- Every account currently sitting in a _Pending safe gets its computed
    -- score marked stale (or a fresh row created if it has none yet) --
    -- conservative (may mark some accounts stale that didn't actually gain
    -- a new mapping this run) but simple and safe; Phase D's recalculation
    -- doing a little unnecessary work is harmless, unlike missing a real change.
    MERGE web.account_risk_score AS tgt
    USING (
        SELECT DISTINCT fa.AccountKey
        FROM fact_account fa
        JOIN dim_safe ds ON ds.SafeKey = fa.SafeKey
        WHERE ds.SafeName LIKE '%[_]Pending%' AND fa.IsDeleted = 0
    ) AS src (AccountKey)
    ON tgt.AccountKey = src.AccountKey
    WHEN MATCHED THEN UPDATE SET IsRiskScoreStale = 1
    WHEN NOT MATCHED THEN INSERT (AccountKey, IsRiskScoreStale) VALUES (src.AccountKey, 1);
END
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


CREATE OR ALTER PROCEDURE dbo.usp_RecalculateRiskScoreForAccount
    @AccountKey BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AccountScore INT;
    EXEC usp_CalculateRiskScore 'Account', @AccountKey, @AccountScore OUTPUT;

    MERGE web.account_risk_score AS tgt
    USING (SELECT @AccountKey AS AccountKey) AS src
    ON tgt.AccountKey = src.AccountKey
    WHEN MATCHED THEN
        UPDATE SET ComputedRiskScore = @AccountScore, RiskScoreCalculatedDate = SYSUTCDATETIME(), IsRiskScoreStale = 0
    WHEN NOT MATCHED THEN
        INSERT (AccountKey, ComputedRiskScore, RiskScoreCalculatedDate, IsRiskScoreStale)
        VALUES (@AccountKey, @AccountScore, SYSUTCDATETIME(), 0);
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


CREATE OR ALTER PROCEDURE dbo.usp_PurgeAuditLog
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RetentionDays INT = (SELECT RetentionDays FROM web.audit_config);
    DECLARE @PurgeBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @StartedAt DATETIME2 = SYSUTCDATETIME();

    IF @RetentionDays IS NULL
    BEGIN
        INSERT INTO web.audit_purge_log (PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage)
        VALUES (@PurgeBatchId, CAST(SYSUTCDATETIME() AS DATE), NULL, @StartedAt, SYSUTCDATETIME(), 'Skipped', 'No RetentionDays configured on web.audit_config -- nothing purged.');
        RETURN;
    END

    DECLARE @CutoffDate DATE = DATEADD(DAY, -@RetentionDays, CAST(SYSUTCDATETIME() AS DATE));

    BEGIN TRY
        DELETE fc
        FROM web.audit_field_change fc
        JOIN web.audit_event ae ON ae.AuditEventKey = fc.AuditEventKey
        WHERE ae.OccurredAt < @CutoffDate;

        DELETE FROM web.audit_event WHERE OccurredAt < @CutoffDate;
        DECLARE @RowsPurged INT = @@ROWCOUNT;

        INSERT INTO web.audit_purge_log (PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage)
        VALUES (@PurgeBatchId, @CutoffDate, @RowsPurged, @StartedAt, SYSUTCDATETIME(), 'Succeeded', NULL);
    END TRY
    BEGIN CATCH
        INSERT INTO web.audit_purge_log (PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage)
        VALUES (@PurgeBatchId, @CutoffDate, NULL, @StartedAt, SYSUTCDATETIME(), 'Failed', ERROR_MESSAGE());
        THROW;
    END CATCH
END
GO


CREATE OR ALTER VIEW web.vw_decom_safe
AS
SELECT ds.SafeKey, ds.SafeName, ss.SourceSystemName, da.ApplicationName,
       (SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.SafeKey = ds.SafeKey AND fa.IsDeleted = 0) AS ActiveAccountCount,
       (SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.SafeKey = ds.SafeKey AND fa.IsDeleted = 1) AS DeletedAccountCount
FROM dbo.dim_safe ds
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = ds.SourceSystemKey
LEFT JOIN web.dim_application da ON da.ApplicationKey = ds.ApplicationKey
CROSS JOIN web.app_config ac
WHERE dbo.fn_MatchesNamePattern(ds.SafeName, ac.SafeDecomMode, ac.SafeDecomValue) = 1
  AND dbo.fn_MatchesNamePattern(ds.SafeName, ac.SafeIgnoreMode, ac.SafeIgnoreValue) = 0;
GO


CREATE OR ALTER VIEW web.vw_decom_account
AS
SELECT fa.AccountKey, fa.AccountName, fa.UserName, fa.Address, fa.IsDeleted,
       ds.SafeName, ss.SourceSystemName,
       CAST(dbo.fn_MatchesNamePattern(ds.SafeName, ac.SafeDecomMode, ac.SafeDecomValue) AS BIT) AS InFlaggedSafe,
       CAST(dbo.fn_MatchesNamePattern(fa.AccountName, ac.AccountDecomMode, ac.AccountDecomValue) AS BIT) AS NameFlagged,
       (SELECT STRING_AGG(o.SafeName, ', ') WITHIN GROUP (ORDER BY o.SafeName)
        FROM (SELECT DISTINCT ods.SafeName
              FROM dbo.fact_account other
              JOIN dbo.dim_safe ods ON ods.SafeKey = other.SafeKey
              WHERE other.AccountKey <> fa.AccountKey AND other.IsDeleted = 0
                AND fa.UserName IS NOT NULL AND fa.Address IS NOT NULL
                AND UPPER(LTRIM(RTRIM(other.UserName))) = UPPER(LTRIM(RTRIM(fa.UserName)))
                AND UPPER(LTRIM(RTRIM(other.Address))) = UPPER(LTRIM(RTRIM(fa.Address)))
                AND dbo.fn_MatchesNamePattern(ods.SafeName, ac.SafeDecomMode, ac.SafeDecomValue) = 0) o) AS OtherSafes
FROM dbo.fact_account fa
LEFT JOIN dbo.dim_safe ds ON ds.SafeKey = fa.SafeKey
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
CROSS JOIN web.app_config ac
WHERE (dbo.fn_MatchesNamePattern(ds.SafeName, ac.SafeDecomMode, ac.SafeDecomValue) = 1
       AND dbo.fn_MatchesNamePattern(ds.SafeName, ac.SafeIgnoreMode, ac.SafeIgnoreValue) = 0)
   OR dbo.fn_MatchesNamePattern(fa.AccountName, ac.AccountDecomMode, ac.AccountDecomValue) = 1;
GO


CREATE OR ALTER PROCEDURE dbo.usp_DeriveTargetsFromAccountAddress
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @OtherTypeKey INT = (SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Other');
    IF @OtherTypeKey IS NULL
    BEGIN
        RAISERROR('usp_DeriveTargetsFromAccountAddress: web.dim_target_type has no ''Other'' type (29_BlueTrack_TargetTypeDimension.sql seeds it).', 16, 1);
        RETURN;
    END

    -- Distinct, non-blank Addresses from active accounts, classified once as
    -- IPAddress (dotted-quad shape) or Hostname (everything else).
    IF OBJECT_ID('tempdb..#NewTargetAddresses') IS NOT NULL DROP TABLE #NewTargetAddresses;
    CREATE TABLE #NewTargetAddresses (
        Address        NVARCHAR(300) NOT NULL PRIMARY KEY,
        IdentifierType NVARCHAR(50)  NOT NULL
    );

    INSERT INTO #NewTargetAddresses (Address, IdentifierType)
    SELECT DISTINCT
        addr.Address,
        CASE
            WHEN addr.Address NOT LIKE '%[^0-9.]%'
                 AND LEN(addr.Address) - LEN(REPLACE(addr.Address, '.', '')) = 3
            THEN 'IPAddress'
            ELSE 'Hostname'
        END
    FROM (
        SELECT LTRIM(RTRIM(fa.Address)) AS Address
        FROM fact_account fa
        WHERE fa.IsDeleted = 0
          AND fa.Address IS NOT NULL
          AND LTRIM(RTRIM(fa.Address)) <> ''
    ) addr
    WHERE NOT EXISTS (SELECT 1 FROM web.target_identifier ti WHERE ti.IdentifierValue = addr.Address);

    DECLARE @NewTargets TABLE (TargetKey INT NOT NULL, TargetName NVARCHAR(300) NOT NULL);

    INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore, Description, DiscoverySource, ModifiedDate)
    OUTPUT inserted.TargetKey, inserted.TargetName INTO @NewTargets (TargetKey, TargetName)
    SELECT
        @OtherTypeKey,
        nta.Address,
        0,
        'Auto-generated from CyberArk account data (fact_account.Address). RiskScore is a placeholder (0) -- not yet analyst-reviewed.',
        'CyberArk ETL',
        SYSUTCDATETIME()
    FROM #NewTargetAddresses nta;

    INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue)
    SELECT nt.TargetKey, nta.IdentifierType, nt.TargetName
    FROM @NewTargets nt
    JOIN #NewTargetAddresses nta ON nta.Address = nt.TargetName;

    DROP TABLE #NewTargetAddresses;
END
GO


/* usp_DeriveAccountTargetMap_FromAddress (D-193, agreed 2026-10-09): a
   CyberArk account whose Address is server X is a credential for X, so it
   is linked directly to every Target with an identifier equal to that
   Address -- auto-created above or entered by hand, domain addresses
   included. An account can reach many Targets: these links sit alongside
   the ones its group memberships give it. Links are marked
   SourceMethod = 'AddressDerived' and kept in step with the data on every
   run: new matches are added; an AddressDerived link whose account was
   deleted, or whose Address no longer matches, is removed. Links made any
   other way (CSV import, Phase C's 'PendingSafeDerived', manual) are never
   touched. Every account whose links changed is marked stale for
   usp_RecalculateRiskScores. */
CREATE OR ALTER PROCEDURE dbo.usp_DeriveAccountTargetMap_FromAddress
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Changed TABLE (AccountKey BIGINT NOT NULL);

    IF OBJECT_ID('tempdb..#DesiredLinks') IS NOT NULL DROP TABLE #DesiredLinks;
    CREATE TABLE #DesiredLinks (
        AccountKey BIGINT NOT NULL,
        TargetKey  INT    NOT NULL,
        PRIMARY KEY (AccountKey, TargetKey)
    );

    INSERT INTO #DesiredLinks (AccountKey, TargetKey)
    SELECT DISTINCT fa.AccountKey, ti.TargetKey
    FROM fact_account fa
    JOIN web.target_identifier ti ON ti.IdentifierValue = LTRIM(RTRIM(fa.Address))
    WHERE fa.IsDeleted = 0
      AND fa.Address IS NOT NULL
      AND LTRIM(RTRIM(fa.Address)) <> '';

    INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod, LoadTimestamp)
    OUTPUT inserted.AccountKey INTO @Changed (AccountKey)
    SELECT d.AccountKey, d.TargetKey, 'AddressDerived', SYSUTCDATETIME()
    FROM #DesiredLinks d
    WHERE NOT EXISTS (
        SELECT 1 FROM web.account_target_map atm
        WHERE atm.AccountKey = d.AccountKey AND atm.TargetKey = d.TargetKey
    );

    DELETE atm
    OUTPUT deleted.AccountKey INTO @Changed (AccountKey)
    FROM web.account_target_map atm
    WHERE atm.SourceMethod = 'AddressDerived'
      AND NOT EXISTS (
          SELECT 1 FROM #DesiredLinks d
          WHERE d.AccountKey = atm.AccountKey AND d.TargetKey = atm.TargetKey
      );

    MERGE web.account_risk_score AS tgt
    USING (SELECT DISTINCT AccountKey FROM @Changed) AS src (AccountKey)
    ON tgt.AccountKey = src.AccountKey
    WHEN MATCHED THEN UPDATE SET IsRiskScoreStale = 1
    WHEN NOT MATCHED THEN INSERT (AccountKey, IsRiskScoreStale) VALUES (src.AccountKey, 1);

    DROP TABLE #DesiredLinks;
END
GO


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
        EXEC usp_DeriveTargetsFromAccountAddress;   -- D-123 (51_)
        EXEC usp_DeriveAccountTargetMap_FromPendingSafes;
        EXEC usp_DeriveAccountTargetMap_FromAddress;    -- D-193 (51_)
        EXEC usp_RecalculateRiskScores;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

PRINT '06_BlueTrack_Baseline_WebLogic.sql complete.';
GO
