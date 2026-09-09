/* ============================================================================
   22_BlueTrack_RiskScoringPendingSafeDerivation.sql

   RUN THIS AFTER 01-21. Safe to re-run (CREATE OR ALTER for the two
   procedures -- pure logic, no data of its own, unlike a table).

   Design_Risk_Scoring.md, D-101-105, D-119 Phase C: the PendingSafeDerived
   mechanism for direct Account->Target links -- the one of three
   confirmed mechanisms (see "Direct Account->Target Access" in that doc)
   that needs no external file at all. CyberArk's own Account Discovery
   already drops discovered accounts into a safe named like
   "PasswordManager_Pending" -- fact_account/dim_safe already carry this
   data via the exact same SafeName LIKE '%[_]Pending%' match
   usp_Load_AccountProgressAutoAdvance (D-91) already uses.

   Unlike Phase B's target-matching logic (built as a C# service,
   TargetMatchingService -- see that phase's own note on why), this piece
   genuinely needs to be T-SQL: it must run automatically as part of the
   existing usp_RunFullLoad SQL Agent job, which is a pure T-SQL pipeline
   with no API/C# process involved at all. A simpler match-only (never
   create, never route to review) version of the same priority-ordered
   identifier lookup suffices here -- an Address with no matching Target
   simply gets no row yet, consistent with "gaps are expected, not
   auto-filled" (this doc's own phrasing).

   usp_RunFullLoad itself (06_BlueTrack_PowerBI_Support.sql) is redefined
   here via CREATE OR ALTER to add the new call -- a stored procedure body
   holds no data of its own, so redefining it in a later incremental
   script is safe under D-58 the same way any other pure-logic object is,
   unlike a table already holding real rows.
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

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

PRINT '22_BlueTrack_RiskScoringPendingSafeDerivation.sql complete.';
