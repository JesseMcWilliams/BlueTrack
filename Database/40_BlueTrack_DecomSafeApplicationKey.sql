/* ============================================================================
   40_BlueTrack_DecomSafeApplicationKey.sql

   RUN THIS AFTER 01-39. Guarded -- safe to re-run (CREATE OR ALTER).

   D-267: web.vw_decom_safe gains ApplicationKey, so Reports > Safes Flagged for
   Deletion can link a Safe's application name to that application's edit page
   (the view already returned the name). Same rows and columns as before, plus
   ApplicationKey at the end.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER VIEW web.vw_decom_safe
AS
SELECT ds.SafeKey, ds.SafeName, ss.SourceSystemName, da.ApplicationName,
       (SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.SafeKey = ds.SafeKey AND fa.IsDeleted = 0) AS ActiveAccountCount,
       (SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.SafeKey = ds.SafeKey AND fa.IsDeleted = 1) AS DeletedAccountCount,
       ds.ApplicationKey
FROM dbo.dim_safe ds
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = ds.SourceSystemKey
LEFT JOIN web.dim_application da ON da.ApplicationKey = ds.ApplicationKey
CROSS JOIN web.app_config ac
WHERE dbo.fn_MatchesNamePattern(ds.SafeName, ac.SafeDecomMode, ac.SafeDecomValue) = 1
  AND dbo.fn_MatchesNamePattern(ds.SafeName, ac.SafeIgnoreMode, ac.SafeIgnoreValue) = 0;
GO

PRINT '40_BlueTrack_DecomSafeApplicationKey.sql complete.';
GO
