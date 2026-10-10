/* ============================================================================
   51_BlueTrack_TargetsFromCyberArkAddress.sql

   RUN THIS AFTER 01-50. Safe to re-run (CREATE OR ALTER -- pure logic, no
   data of its own).

   D-123, rebuilt 2026-10-09 from the never-merged branch
   feature/target-generation-from-cyberark. That branch's script 28 was
   applied by hand to the dev host's BlueTrack database in 2026-09 (numbers
   27 and 28 stay unused on main); since 29_BlueTrack_TargetTypeDimension.sql
   replaced dim_target.TargetType with TargetTypeKey, its procedure failed
   with "Invalid column name 'TargetType'" -- and with it, the whole nightly
   usp_RunFullLoad transaction on that host. This script replaces it there
   and adds it everywhere else.

   usp_DeriveTargetsFromAccountAddress creates a web.dim_target (and its
   web.target_identifier) for every distinct Address of an active
   (IsDeleted = 0) CyberArk account that no Target identifier already has.
   Decisions confirmed with the user in 2026-09 and again 2026-10-09:
   - Every active account with an Address is a source, not just Phase C's
     narrower _Pending-safe subset.
   - The Address is classified IPAddress when it has the dotted-quad shape
     (digits and dots only, exactly three dots), else Hostname. Not a full
     IPv4 validator, but a real pattern check.
   - RiskScore is a placeholder 0 (a confirmed exception to dim_target's
     "always analyst-set" rule); DiscoverySource = 'CyberArk ETL' and the
     Description flag the row for an analyst to re-score.
   - Target type is 'Other' (web.dim_target_type TypeCode), since nothing in
     the CyberArk data maps cleanly to Server/Database/etc.
   - Linking (D-193, decided 2026-10-09, reversing the 2026-09 "inventory
     only" choice): usp_DeriveAccountTargetMap_FromAddress below links each
     active account to every Target whose identifier equals its Address,
     marked SourceMethod = 'AddressDerived' and kept up to date each run.
   Idempotent: a NOT EXISTS guard on web.target_identifier.IdentifierValue.

   usp_RunFullLoad (last defined in 23_BlueTrack_RiskScoringCalculation.sql)
   is redefined to create Targets just before Phase C's derivation (so a new
   Target's identifier is available to that same run's matching) and to run
   the address linking just after it, before risk scores are recalculated.
   ============================================================================ */

USE $DatabaseName$;
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

-- D-193: allow the new link kind. The constraint
-- (20_BlueTrack_RiskScoringSchema.sql) only listed 'PendingSafeDerived',
-- 'ManualImport' and 'ETL'. Rebuilt only when 'AddressDerived' is missing.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_account_target_map_SourceMethod' AND definition LIKE '%AddressDerived%')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_account_target_map_SourceMethod')
        ALTER TABLE web.account_target_map DROP CONSTRAINT CK_account_target_map_SourceMethod;
    ALTER TABLE web.account_target_map ADD CONSTRAINT CK_account_target_map_SourceMethod
        CHECK (SourceMethod IN ('PendingSafeDerived', 'ManualImport', 'ETL', 'AddressDerived'));
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

PRINT '51_BlueTrack_TargetsFromCyberArkAddress.sql complete.';
