/* ============================================================================
   50_BlueTrack_CpmHealth.sql

   RUN THIS AFTER 01-49. Guarded -- safe to re-run.

   D-288: CPM health.
   - fact_account.CpmFailureSince: when (UTC) the load first saw the
     account's CPM status as failure (cleared when it isn't), so the
     CpmHealth digest can name the accounts that failed since the last one.
   - dim_platform.RequirePsm: whether the platform requires PSM (session
     isolation), from a Self-Hosted Direct pull's platform policy
     (stg_shd_platforms); Privilege Cloud's export has no such column, so
     it stays NULL there. A new Progress Rule field, RequiresPsm (Yes/No).
   - usp_Load_CpmHealth fills both, in the load before the Progress Rules.
   - dbo.vw_cpm_health: the accounts whose CPM status is failure (the
     user's choice for "needs attention"), for Reports > CPM Health.
   - The CpmHealth notification type (off).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('dbo.fact_account', 'CpmFailureSince') IS NULL
    ALTER TABLE dbo.fact_account ADD CpmFailureSince DATETIME2 NULL;   -- full precision: compared with notification_log.SentDate
GO
IF COL_LENGTH('dbo.dim_platform', 'RequirePsm') IS NULL
    ALTER TABLE dbo.dim_platform ADD RequirePsm BIT NULL;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Load_CpmHealth
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    DECLARE @Now DATETIME2 = SYSUTCDATETIME();

    IF dbo.fn_SelfHostedStagedByDirect() = 1
        UPDATE p SET RequirePsm = s.RequirePrivilegedSessionMonitoringAndIsolation
        FROM dbo.dim_platform p
        JOIN dbo.stg_shd_platforms s ON p.SourceSystemKey = @SelfHostedKey AND p.PlatformID = s.PlatformId;

    UPDATE dbo.fact_account SET CpmFailureSince = @Now
    WHERE CpmFailureSince IS NULL AND LOWER(LTRIM(RTRIM(CPMStatus))) = 'failure';
    UPDATE dbo.fact_account SET CpmFailureSince = NULL
    WHERE CpmFailureSince IS NOT NULL AND ISNULL(LOWER(LTRIM(RTRIM(CPMStatus))), '') <> 'failure';
END
GO

/* Reports > CPM Health: accounts BlueTrack tracks (not deleted, not a feed account linked to a CyberArk one) whose CPM
   status is failure, with what the CPM last did and what is known about verifying and changing it. */
CREATE OR ALTER VIEW dbo.vw_cpm_health AS
SELECT fa.AccountKey, fa.AccountName, fa.UserName, fa.Address, sf.SafeName, pl.PlatformID, ss.SourceSystemName,
       fa.CPMStatus, fa.CpmFailureSince, fa.AutoManaged, fa.ManualReason,
       fa.LastCPMAction, fa.LastCPMActionResult, fa.LastCPMActionTime,
       fa.LastVerifiedDate, fa.LastReconciledDate, fa.LastCPMModifiedDate,
       fap.OwnerName, stg.StageName, sts.StatusName
FROM dbo.fact_account fa
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
LEFT JOIN dbo.dim_safe sf ON sf.SafeKey = fa.SafeKey
LEFT JOIN dbo.dim_platform pl ON pl.PlatformKey = fa.PlatformKey
LEFT JOIN dbo.fact_account_progress fap ON fap.AccountKey = fa.AccountKey
LEFT JOIN dbo.dim_blueprint_stage stg ON stg.StageKey = fap.CurrentStageKey
LEFT JOIN dbo.dim_progress_status sts ON sts.StatusKey = fap.CurrentStatusKey
WHERE fa.IsDeleted = 0
  AND LOWER(LTRIM(RTRIM(fa.CPMStatus))) = 'failure'
  AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey);
GO

/* As in 49_BlueTrack_ProgressRulesAndStages.sql, plus RequiresPsm (D-288). */
CREATE OR ALTER FUNCTION dbo.fn_ProgressAccountFields(@AccountKey BIGINT)
RETURNS TABLE
AS
RETURN
SELECT v.FieldName, v.TextValue, v.DateValue
FROM dbo.fact_account fa
JOIN dbo.fact_account_progress fap ON fap.AccountKey = fa.AccountKey
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
LEFT JOIN dbo.dim_safe sf ON sf.SafeKey = fa.SafeKey
LEFT JOIN dbo.dim_platform pl ON pl.PlatformKey = fa.PlatformKey
LEFT JOIN dbo.dim_account_type at ON at.AccountTypeKey = fap.AccountTypeKey
LEFT JOIN dbo.dim_source_of_record sor ON sor.SORKey = fap.SORKey
LEFT JOIN dbo.dim_risk_level rl ON rl.RiskLevelKey = fap.RiskLevelKey
LEFT JOIN dbo.dim_progress_status st ON st.StatusKey = fap.CurrentStatusKey
LEFT JOIN web.dim_application app ON app.ApplicationKey = fap.ApplicationKey
CROSS APPLY (VALUES
    (N'Source',              CAST(ss.SourceSystemCode AS NVARCHAR(4000)), CAST(NULL AS DATETIME2)),
    (N'SafeName',            sf.SafeName, NULL),
    (N'PlatformId',          pl.PlatformID, NULL),
    (N'PlatformName',        pl.PlatformName, NULL),
    (N'AccountName',         fa.AccountName, NULL),
    (N'UserName',            fa.UserName, NULL),
    (N'Address',             fa.Address, NULL),
    (N'AutoManaged',         CASE fa.AutoManaged WHEN 1 THEN N'Yes' WHEN 0 THEN N'No' END, NULL),
    (N'RequiresPsm',         CASE pl.RequirePsm WHEN 1 THEN N'Yes' WHEN 0 THEN N'No' END, NULL),
    (N'CPMStatus',           fa.CPMStatus, NULL),
    (N'SecretType',          fa.SecretType, NULL),
    (N'LastVerified',        NULL, CAST(fa.LastVerifiedDate AS DATETIME2)),
    (N'LastReconciled',      NULL, CAST(fa.LastReconciledDate AS DATETIME2)),
    (N'LastChanged',         NULL, CAST(fa.LastCPMModifiedDate AS DATETIME2)),
    (N'LastCPMAction',       fa.LastCPMAction, NULL),
    (N'LastCPMActionResult', fa.LastCPMActionResult, NULL),
    (N'LastCPMActionTime',   NULL, fa.LastCPMActionTime),
    (N'DeviceType',          fa.DeviceType, NULL),
    (N'CreationMethod',      fa.CreationMethod, NULL),
    (N'AccountType',         at.AccountTypeName, NULL),
    (N'SourceOfRecord',      sor.SORName, NULL),
    (N'RiskLevel',           rl.RiskLevelName, NULL),
    (N'Status',              st.StatusName, NULL),
    (N'OwnerName',           fap.OwnerName, NULL),
    (N'BusinessUnit',        fap.BusinessUnit, NULL),
    (N'Application',         app.ApplicationName, NULL)
) v(FieldName, TextValue, DateValue)
WHERE fa.AccountKey = @AccountKey;
GO

/* As in 48_BlueTrack_SelfHostedDirectExtras.sql, plus usp_Load_CpmHealth (D-288). */
CREATE OR ALTER PROCEDURE usp_RunFullLoad
    @TakeSnapshot BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Load');
    SET @EtlRunKey = SCOPE_IDENTITY();

    BEGIN TRY
        BEGIN TRANSACTION;

        EXEC usp_Load_DimLocation;
        EXEC usp_Load_DimPlatform;
        EXEC usp_Load_DimSafe;
        EXEC usp_Load_DimUser;
        EXEC usp_Load_DimGroup;
        EXEC usp_Load_GroupMembership;
        EXEC dbo.usp_Load_AdGroupMembers;             -- D-281
        EXEC dbo.usp_Load_DimCyberArkApplication;     -- D-282
        EXEC usp_Load_FactAccount;
        EXEC dbo.usp_Load_FactAccountClassicDetail;   -- D-280
        EXEC dbo.usp_Load_CpmHealth;                  -- D-288: before the Progress Rules, which can test RequiresPsm
        EXEC usp_Load_FactAccountProgress;
        EXEC usp_Load_AccountProgressAutoAdvance;
        EXEC usp_Load_FactSafeEntitlement;
        EXEC usp_Load_AccountReconciliation;
        -- D-224: usp_Load_FactAccountProgressHistory moved out, to usp_RunDailySnapshot below.
        EXEC usp_DeriveTargetsFromAccountAddress;   -- D-123 (51_)
        EXEC usp_DeriveAccountTargetMap_FromPendingSafes;
        EXEC usp_DeriveAccountTargetMap_FromAddress;    -- D-193 (51_)
        EXEC usp_RecalculateRiskScores;

        COMMIT TRANSACTION;

        UPDATE dbo.etl_run_log SET Status = 'Succeeded', CompletedAt = SYSUTCDATETIME() WHERE EtlRunKey = @EtlRunKey;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        UPDATE dbo.etl_run_log
        SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(ERROR_MESSAGE(), 2000),
            ErrorProcedure = ERROR_PROCEDURE(), ErrorLine = ERROR_LINE()
        WHERE EtlRunKey = @EtlRunKey;

        -- D-224: snapshot the unchanged state; a snapshot failure is in its own etl_run_log row.
        IF @TakeSnapshot = 1
        BEGIN
            BEGIN TRY
                EXEC dbo.usp_RunDailySnapshot;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            END CATCH
        END;

        THROW;
    END CATCH

    IF @TakeSnapshot = 1
        EXEC dbo.usp_RunDailySnapshot;
END
GO

/* Filled once now, so the first digest doesn't report every account failing today as new. */
UPDATE dbo.fact_account SET CpmFailureSince = DATEADD(DAY, -1, SYSUTCDATETIME())
WHERE CpmFailureSince IS NULL AND LOWER(LTRIM(RTRIM(CPMStatus))) = 'failure';
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'CpmHealth', 'Once a day, from the daily digest time: the accounts whose CPM status became failure since the last digest (Reports > CPM Health lists them all). Not sent when there are none (D-288).', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'CpmHealth');
GO

PRINT '50_BlueTrack_CpmHealth.sql complete.';
GO
