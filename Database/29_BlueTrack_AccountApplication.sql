/* ============================================================================
   29_BlueTrack_AccountApplication.sql

   RUN THIS AFTER 01-28. Guarded -- safe to re-run.

   D-238: an account can be assigned to an Application directly, as well as
   through its Safe (D-31). An application has a Safe mapping and an Account
   mapping; an account belongs to at most one application of its own.

   - dbo.fact_account_progress.ApplicationKey (INT NULL, FK to
     web.dim_application, plain index). It lives on the progress row, which
     BlueTrack owns, not on fact_account, which the nightly load rebuilds.
   - account_progress_field_metadata: the Application dropdown on the Account
     Progress edit form, after Source of Record.
   - web.vw_account_application_exception (D-81) now covers an account through
     its own application AND its Safe's application. If they differ, an Active
     exception on either one covers the account; if they are the same, the
     exception appears once.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('dbo.fact_account_progress', 'ApplicationKey') IS NULL
    ALTER TABLE dbo.fact_account_progress ADD ApplicationKey INT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_fact_account_progress_ApplicationKey')
    ALTER TABLE dbo.fact_account_progress ADD CONSTRAINT FK_fact_account_progress_ApplicationKey
        FOREIGN KEY (ApplicationKey) REFERENCES web.dim_application(ApplicationKey);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.fact_account_progress') AND name = 'IX_fact_account_progress_ApplicationKey'
)
    CREATE NONCLUSTERED INDEX IX_fact_account_progress_ApplicationKey
        ON dbo.fact_account_progress (ApplicationKey);
GO

INSERT INTO web.account_progress_field_metadata
    (FieldName, DisplayLabel, FieldType, ReferenceTable, IsRequired, RequiredPermission, DisplayOrder)
SELECT 'ApplicationKey', 'Application', 'Dropdown', 'dim_application', 0, NULL, 55
WHERE NOT EXISTS (SELECT 1 FROM web.account_progress_field_metadata WHERE FieldName = 'ApplicationKey');
GO

CREATE OR ALTER VIEW web.vw_account_application_exception AS
SELECT
    x.AccountKey,
    re.ExceptionKey,
    re.ExceptionID,
    re.ApplicationKey,
    da.ApplicationName,
    re.ReviewDate
FROM (
    SELECT fa.AccountKey, ds.ApplicationKey
    FROM dbo.fact_account fa
    JOIN dbo.dim_safe ds ON ds.SafeKey = fa.SafeKey
    WHERE fa.IsDeleted = 0 AND ds.ApplicationKey IS NOT NULL
    UNION
    SELECT fa.AccountKey, fap.ApplicationKey
    FROM dbo.fact_account fa
    JOIN dbo.fact_account_progress fap ON fap.AccountKey = fa.AccountKey
    WHERE fa.IsDeleted = 0 AND fap.ApplicationKey IS NOT NULL
) x
JOIN web.dim_application da         ON da.ApplicationKey = x.ApplicationKey
JOIN web.risk_exception re          ON re.ApplicationKey = da.ApplicationKey
JOIN web.dim_exception_status des   ON des.ExceptionStatusKey = re.ExceptionStatusKey
WHERE des.StatusName = 'Active';
GO
