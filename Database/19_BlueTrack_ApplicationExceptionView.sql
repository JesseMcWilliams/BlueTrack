/* ============================================================================
   19_BlueTrack_ApplicationExceptionView.sql

   RUN THIS AFTER 01-18.

   D-81 (Design_Risk_Exception_Tracking.md): application-scoped exceptions
   were explicitly left with an undecided propagation mechanism -- "a view
   vs. a batch update... not decided here." The user resolved this
   directly, 2026-09-04: a view. Computes "is this account currently
   covered by an application-scoped exception" live, at query time,
   through fact_account.SafeKey -> dim_safe.ApplicationKey ->
   web.risk_exception, rather than writing fact_account_progress.ExceptionKey
   for every account under the application (which D-77 only does for the
   account-scoped case).

   Deliberately does NOT write to fact_account_progress.ExceptionKey --
   that column stays exactly what D-77 already made it (the account-scoped
   pointer). This view is a second, independent source of "is this account
   excepted," not a replacement for that column. Anything that needs the
   full picture (is this account covered by ANY exception, account- or
   application-scoped) needs to check both.

   Can return more than one row per account if more than one Active
   application-scoped exception exists for the same Application -- nothing
   in the app prevents creating two, so this is a live computation, not an
   assumption of exactly one.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER VIEW web.vw_account_application_exception AS
SELECT
    fa.AccountKey,
    re.ExceptionKey,
    re.ExceptionID,
    re.ApplicationKey,
    da.ApplicationName,
    re.ReviewDate
FROM dbo.fact_account fa
JOIN dbo.dim_safe ds           ON ds.SafeKey = fa.SafeKey
JOIN web.dim_application da     ON da.ApplicationKey = ds.ApplicationKey
JOIN web.risk_exception re       ON re.ApplicationKey = da.ApplicationKey
JOIN web.dim_exception_status des ON des.ExceptionStatusKey = re.ExceptionStatusKey
WHERE des.StatusName = 'Active'
  AND fa.IsDeleted = 0;
GO

PRINT 'web.vw_account_application_exception created (D-81).';
