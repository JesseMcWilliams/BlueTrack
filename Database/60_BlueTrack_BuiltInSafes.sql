/* ============================================================================
   60_BlueTrack_BuiltInSafes.sql

   RUN THIS AFTER 01-59. Safe to re-run (CREATE OR ALTER).

   - D-319: dbo.vw_selfhosted_internal_safe (script 45) names CyberArk's
     built-in Safes, so accounts and Safe pages there are marked internal.
     Its list missed built-in Safes that CyberArk's PAM Self-Hosted 15.2
     documentation names ("The PVWA environment", "CPM post-installation
     tasks"): AccountsFeedADAccounts, AccountsFeedDiscoveryLogs (the user's
     example), the PasswordManager Safes (a CPM whose user isn't named
     PasswordManager wasn't caught by the CPM-name rule), PVWAPublicData,
     PVWAReports, PVWATicketingSystem and SharedAuth_Internal. The rest of
     the view is unchanged: a Safe marked Internal, or a name starting with a
     CPM user's name, still counts.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER VIEW dbo.vw_selfhosted_internal_safe
AS
WITH BuiltIn (SafeName) AS (
    SELECT v.SafeName FROM (VALUES
        -- Kept from script 45.
        (N'AppProviderCacheSafe'), (N'AppProviderConf'), (N'PasswordManagerShared'), (N'PasswordManagerTemp'), (N'Pictures'),
        (N'PSMLiveSessions'), (N'PSMNotifications'), (N'PSMSessions'), (N'PSMUnmanagedSessionAccounts'),
        (N'PVWAConfig'), (N'PVWAPrivateUserPrefs'), (N'PVWATaskDefinitions'), (N'PVWAUserPrefs'), (N'System'),
        -- D-319: from the PAM Self-Hosted 15.2 documentation.
        (N'AccountsFeedADAccounts'), (N'AccountsFeedDiscoveryLogs'),
        (N'PasswordManager'), (N'PasswordManager_ADInternal'), (N'PasswordManager_Accounts'), (N'PasswordManager_Info'),
        (N'PasswordManager_Pending'), (N'PasswordManager_workspace'),
        (N'PVWAPublicData'), (N'PVWAReports'), (N'PVWATicketingSystem'), (N'SharedAuth_Internal')
    ) v (SafeName)
)
SELECT s.SafeKey,
       CASE WHEN mk.Category = N'Internal' THEN N'Marked'
            WHEN s.SafeName IN (SELECT SafeName FROM BuiltIn) THEN N'BuiltIn'
            ELSE N'CpmSafe' END AS InternalReason
FROM dbo.dim_safe s
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = s.SourceSystemKey AND ss.SourceSystemCode = 'SELFHOSTED'
LEFT JOIN web.shd_comparison_mark mk ON mk.ItemKind = N'Safe' AND mk.ItemKey = s.SafeUrlId
WHERE (mk.Category = N'Internal')
   OR (mk.Category IS NULL
       AND (s.SafeName IN (SELECT SafeName FROM BuiltIn)
            OR EXISTS (SELECT 1 FROM dbo.dim_user u
                       WHERE u.SourceSystemKey = s.SourceSystemKey AND u.UserType = N'31'
                         AND LEN(u.Username) > 0 AND LEFT(s.SafeName, LEN(u.Username)) = u.Username)));
GO

PRINT '60_BlueTrack_BuiltInSafes.sql complete.';
GO
