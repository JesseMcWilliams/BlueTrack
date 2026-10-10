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
   - It only fills the Target inventory. It does NOT link accounts to these
     Targets (web.account_target_map): direct grants stay the exception in
     the risk model; CSV import, Phase C and manual links are the only ways
     an account connects to a Target.
   Idempotent: a NOT EXISTS guard on web.target_identifier.IdentifierValue.

   usp_RunFullLoad (last defined in 23_BlueTrack_RiskScoringCalculation.sql)
   is redefined to call it just before Phase C's derivation, so a new
   Target's identifier is available to that same run's account matching.
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
