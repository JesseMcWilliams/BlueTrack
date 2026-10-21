/* ============================================================================
   43_BlueTrack_ShadowComparisonRules.sql

   RUN THIS AFTER 01-42. Guarded -- safe to re-run.

   D-272 (Claude_Docs/Design_CyberArk-Direct-Integration.md, "Shadow pull and
   comparison"): the shadow comparison explains, by itself, the differences
   every Vault has, so an admin only marks what is particular to the site.
   From the lab trial of 2026-10-20, confirmed by the user:

   - Internal Safes (automatic Internal mark): the built-in internal Safe
     names below, and any Safe whose name starts with a CPM's name. Covers
     the Safe, its accounts and its members. PVWA doesn't return them.
   - The PVWA gateway members (automatic Expected mark): the PVWAGWAccounts
     group and each PVWA server's gateway user. REST doesn't list them as
     Safe members.
   - Directory mappings are left out of the user comparison (REST has no
     user for them).
   - Dependent accounts (MasterPassName set) are left out of the EVD side,
     as the Load will leave them out after the switch (plan P3.3).

   An admin's mark wins over an automatic one. Automatic marks aren't rows
   of web.shd_comparison_mark; dbo.shd_comparison_difference keeps the
   automatic category (AutoMarkCategory) so re-applying marks keeps it.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('dbo.shd_comparison_difference', 'AutoMarkCategory') IS NULL
    ALTER TABLE dbo.shd_comparison_difference ADD AutoMarkCategory NVARCHAR(20) NULL;
GO

IF COL_LENGTH('dbo.shd_comparison_difference', 'MarkAuto') IS NULL
    ALTER TABLE dbo.shd_comparison_difference ADD MarkAuto BIT NOT NULL
        CONSTRAINT DF_shd_comparison_difference_MarkAuto DEFAULT 0;
GO

/* As in 42_BlueTrack_SelfHostedShadowPull.sql, plus the D-272 rules above. MarkCategory is the admin's mark, else the
   automatic one (MarkAuto = 1); AutoMarkCategory is the automatic one alone. */
CREATE OR ALTER VIEW dbo.vw_shd_comparison
AS
WITH cfg AS (
    SELECT TOP (1) SafeIgnoreMode AS M, SafeIgnoreValue AS V FROM web.app_config
),
-- CPM names: REST component users of type CPM, the Safes' managing CPMs, and EVD users of type 31 (CPM).
cpm AS (
    SELECT UserName AS Name FROM dbo.stg_shd_users WHERE UserType = N'CPM'
    UNION SELECT ManagingCpm FROM dbo.stg_shd_safes
    UNION SELECT CAUUserName FROM dbo.stg_sh_users WHERE CAUUserTypeID = 31
),
cpm_name AS (
    SELECT DISTINCT LTRIM(RTRIM(Name)) AS Name FROM cpm WHERE NULLIF(LTRIM(RTRIM(Name)), N'') IS NOT NULL
),
-- PVWA gateway members: EVD users of type 32 (PVWA) with gateway account authorizations.
gateway_user AS (
    SELECT CAUUserID AS Id FROM dbo.stg_sh_users WHERE CAUUserTypeID = 32 AND ISNULL(CAUGatewayAccountAuthorizations, 0) <> 0
),
-- Directory mappings: internal (1) users of type 34 with no authentication method. They can't log on; REST has no user for them.
directory_mapping AS (
    SELECT CAUUserID AS Id FROM dbo.stg_sh_users WHERE CAUUserTypeID = 34 AND CAUExternalInternal = 1 AND CAUAuthenticationMethods = 0
),
evd_safe AS (
    SELECT s.CASSafeID AS SafeNumber, s.CASSafeName AS SafeName
    FROM dbo.stg_sh_safes s CROSS JOIN cfg
    WHERE dbo.fn_MatchesNamePattern(s.CASSafeName, cfg.M, cfg.V) = 0
),
shd_safe AS (
    SELECT s.SafeNumber, s.SafeName
    FROM dbo.stg_shd_safes s CROSS JOIN cfg
    WHERE dbo.fn_MatchesNamePattern(s.SafeName, cfg.M, cfg.V) = 0
),
evd_acct AS (
    SELECT CAST(f.CAFSafeID AS NVARCHAR(30)) + N'_' + CAST(f.CAFFileID AS NVARCHAR(30)) AS AccountId,
           f.CAFSafeID AS SafeNumber, f.CAFSafeName AS SafeName, f.CAFFileName AS Name,
           MAX(CASE WHEN op.CAOPObjectPropertyName = N'Address' THEN op.CAOPObjectPropertyValue END) AS Address,
           MAX(CASE WHEN op.CAOPObjectPropertyName = N'UserName' THEN op.CAOPObjectPropertyValue END) AS UserName,
           MAX(CASE WHEN op.CAOPObjectPropertyName = N'PolicyID' THEN op.CAOPObjectPropertyValue END) AS PlatformId,
           MAX(CASE WHEN op.CAOPObjectPropertyName = N'LogonDomain' THEN op.CAOPObjectPropertyValue END) AS LogonDomain,
           MAX(CASE WHEN op.CAOPObjectPropertyName = N'MasterPassName' THEN NULLIF(LTRIM(RTRIM(op.CAOPObjectPropertyValue)), N'') END) AS MasterPassName
    FROM dbo.stg_sh_files f
    CROSS JOIN cfg
    LEFT JOIN dbo.stg_sh_objectproperties op ON op.CAOPSafeId = f.CAFSafeID AND op.CAOPFileId = f.CAFFileID
    WHERE f.CAFType = 2
      AND CASE WHEN f.CAFDeletionDate >= '19700102' AND (f.CAFCreationDate IS NULL OR f.CAFDeletionDate >= f.CAFCreationDate) THEN 1 ELSE 0 END = 0
      AND dbo.fn_MatchesNamePattern(f.CAFSafeName, cfg.M, cfg.V) = 0
    GROUP BY f.CAFSafeID, f.CAFFileID, f.CAFSafeName, f.CAFFileName
),
-- Dependent accounts (a usage of another account) are left out (plan P3.3).
evd_acct_kept AS (
    SELECT * FROM evd_acct WHERE MasterPassName IS NULL
),
shd_acct AS (
    SELECT a.AccountId, a.SafeNumber, a.SafeName, a.Name, a.Address, a.UserName, a.PlatformId, ld.LogonDomain
    FROM dbo.stg_shd_accounts a
    CROSS JOIN cfg
    LEFT JOIN (SELECT AccountId, MAX(Value) AS LogonDomain FROM dbo.stg_shd_account_properties
               WHERE Name = N'LogonDomain' GROUP BY AccountId) ld ON ld.AccountId = a.AccountId
    WHERE dbo.fn_MatchesNamePattern(a.SafeName, cfg.M, cfg.V) = 0
),
evd_member AS (
    SELECT o.*
    FROM dbo.stg_sh_owners o CROSS JOIN cfg
    WHERE dbo.fn_MatchesNamePattern(o.CAOSafeName, cfg.M, cfg.V) = 0
),
shd_member AS (
    SELECT m.*
    FROM dbo.stg_shd_safe_members m CROSS JOIN cfg
    WHERE dbo.fn_MatchesNamePattern(m.SafeName, cfg.M, cfg.V) = 0
),
diff AS (
    -- Missing from Direct
    SELECT N'MissingFromDirect' AS Category, N'Safe' AS ItemKind, CAST(e.SafeNumber AS NVARCHAR(100)) AS ItemKey, e.SafeName AS ItemName,
           e.SafeName, CAST(NULL AS NVARCHAR(100)) AS Field, CAST(NULL AS NVARCHAR(1000)) AS EvdValue, CAST(NULL AS NVARCHAR(1000)) AS DirectValue,
           e.SafeNumber AS SafeNumber, CAST(NULL AS BIGINT) AS PrincipalId
    FROM evd_safe e WHERE NOT EXISTS (SELECT 1 FROM shd_safe d WHERE d.SafeNumber = e.SafeNumber)
    UNION ALL
    SELECT N'MissingFromDirect', N'Account', e.AccountId, e.Name, e.SafeName, NULL, NULL, NULL, e.SafeNumber, NULL
    FROM evd_acct_kept e WHERE NOT EXISTS (SELECT 1 FROM shd_acct d WHERE d.AccountId = e.AccountId)
    UNION ALL
    SELECT N'MissingFromDirect', N'User', CAST(e.CAUUserID AS NVARCHAR(100)), e.CAUUserName, NULL, NULL, NULL, NULL, NULL, e.CAUUserID
    FROM dbo.stg_sh_users e
    WHERE NOT EXISTS (SELECT 1 FROM dbo.stg_shd_users d WHERE d.UserId = e.CAUUserID)
      AND NOT EXISTS (SELECT 1 FROM directory_mapping dm WHERE dm.Id = e.CAUUserID)
    UNION ALL
    SELECT N'MissingFromDirect', N'Group', CAST(e.CAGGroupID AS NVARCHAR(100)), e.CAGGroupName, NULL, NULL, NULL, NULL, NULL, e.CAGGroupID
    FROM dbo.stg_sh_groups e WHERE NOT EXISTS (SELECT 1 FROM dbo.stg_shd_groups d WHERE d.GroupId = e.CAGGroupID)
    UNION ALL
    SELECT N'MissingFromDirect', N'Member', CAST(e.CAOSafeID AS NVARCHAR(30)) + N':' + CAST(e.CAOOwnerID AS NVARCHAR(30)), e.CAOOwnerName, e.CAOSafeName,
           NULL, NULL, NULL, e.CAOSafeID, e.CAOOwnerID
    FROM evd_member e WHERE NOT EXISTS (SELECT 1 FROM shd_member d WHERE d.SafeNumber = e.CAOSafeID AND d.MemberId = e.CAOOwnerID)

    -- Only in Direct (shown, never blocking)
    UNION ALL
    SELECT N'OnlyInDirect', N'Safe', CAST(d.SafeNumber AS NVARCHAR(100)), d.SafeName, d.SafeName, NULL, NULL, NULL, d.SafeNumber, NULL
    FROM shd_safe d WHERE NOT EXISTS (SELECT 1 FROM evd_safe e WHERE e.SafeNumber = d.SafeNumber)
    UNION ALL
    SELECT N'OnlyInDirect', N'Account', d.AccountId, d.Name, d.SafeName, NULL, NULL, NULL, d.SafeNumber, NULL
    FROM shd_acct d WHERE NOT EXISTS (SELECT 1 FROM evd_acct e WHERE e.AccountId = d.AccountId)
    UNION ALL
    SELECT N'OnlyInDirect', N'User', CAST(d.UserId AS NVARCHAR(100)), d.UserName, NULL, NULL, NULL, NULL, NULL, d.UserId
    FROM dbo.stg_shd_users d WHERE NOT EXISTS (SELECT 1 FROM dbo.stg_sh_users e WHERE e.CAUUserID = d.UserId)
    UNION ALL
    SELECT N'OnlyInDirect', N'Group', CAST(d.GroupId AS NVARCHAR(100)), d.GroupName, NULL, NULL, NULL, NULL, NULL, d.GroupId
    FROM dbo.stg_shd_groups d WHERE NOT EXISTS (SELECT 1 FROM dbo.stg_sh_groups e WHERE e.CAGGroupID = d.GroupId)
    UNION ALL
    SELECT N'OnlyInDirect', N'Member', CAST(d.SafeNumber AS NVARCHAR(30)) + N':' + CAST(d.MemberId AS NVARCHAR(30)), d.MemberName, d.SafeName,
           NULL, NULL, NULL, d.SafeNumber, d.MemberId
    FROM shd_member d WHERE NOT EXISTS (SELECT 1 FROM evd_member e WHERE e.CAOSafeID = d.SafeNumber AND e.CAOOwnerID = d.MemberId)

    -- Account fields, for accounts in both
    UNION ALL
    SELECT CASE WHEN f.Field = N'LogonDomain' THEN N'LogonDomain' ELSE N'AccountField' END, N'Account', e.AccountId, e.Name, e.SafeName,
           f.Field, f.EvdValue, f.DirectValue, e.SafeNumber, NULL
    FROM evd_acct_kept e
    JOIN shd_acct d ON d.AccountId = e.AccountId
    -- Blank and missing are the same thing (REST returns "" where the EVD has no property).
    CROSS APPLY (VALUES
        (N'Name', NULLIF(LTRIM(RTRIM(CAST(e.Name AS NVARCHAR(1000)))), N''), NULLIF(LTRIM(RTRIM(CAST(d.Name AS NVARCHAR(1000)))), N'')),
        (N'Address', NULLIF(LTRIM(RTRIM(CAST(e.Address AS NVARCHAR(1000)))), N''), NULLIF(LTRIM(RTRIM(CAST(d.Address AS NVARCHAR(1000)))), N'')),
        (N'UserName', NULLIF(LTRIM(RTRIM(CAST(e.UserName AS NVARCHAR(1000)))), N''), NULLIF(LTRIM(RTRIM(CAST(d.UserName AS NVARCHAR(1000)))), N'')),
        (N'Platform', NULLIF(LTRIM(RTRIM(CAST(e.PlatformId AS NVARCHAR(1000)))), N''), NULLIF(LTRIM(RTRIM(CAST(d.PlatformId AS NVARCHAR(1000)))), N'')),
        (N'LogonDomain', NULLIF(LTRIM(RTRIM(CAST(e.LogonDomain AS NVARCHAR(1000)))), N''), NULLIF(LTRIM(RTRIM(CAST(d.LogonDomain AS NVARCHAR(1000)))), N''))
    ) f(Field, EvdValue, DirectValue)
    WHERE EXISTS (SELECT f.EvdValue EXCEPT SELECT f.DirectValue)

    -- Permissions, for members in both (REST name; the EVD column in brackets where two share one REST permission)
    UNION ALL
    SELECT N'Permission', N'Member', CAST(e.CAOSafeID AS NVARCHAR(30)) + N':' + CAST(e.CAOOwnerID AS NVARCHAR(30)), e.CAOOwnerName, e.CAOSafeName,
           p.Field, p.EvdValue, CASE p.DirectValue WHEN 1 THEN N'YES' WHEN 0 THEN N'NO' END, e.CAOSafeID, e.CAOOwnerID
    FROM evd_member e
    JOIN shd_member d ON d.SafeNumber = e.CAOSafeID AND d.MemberId = e.CAOOwnerID
    CROSS APPLY (VALUES
        (N'ListAccounts', e.CAOList, d.ListAccounts),
        (N'RetrieveAccounts', e.CAORetrieve, d.RetrieveAccounts),
        (N'AddAccounts', e.CAOCreateObject, d.AddAccounts),
        (N'UpdateAccountContent', e.CAOUpdateObject, d.UpdateAccountContent),
        (N'UpdateAccountProperties', e.CAOUpdateObjectProperties, d.UpdateAccountProperties),
        (N'RenameAccounts', e.CAORenameObject, d.RenameAccounts),
        (N'DeleteAccounts', e.CAODelete, d.DeleteAccounts),
        (N'ViewAuditLog', e.CAOViewAudit, d.ViewAuditLog),
        (N'ViewSafeMembers', e.CAOViewOwners, d.ViewSafeMembers),
        (N'UseAccounts', e.CAOUsePassword, d.UseAccounts),
        (N'InitiateCPMAccountManagementOperations', e.CAOInitiateCPMChange, d.InitiateCPMAccountManagementOperations),
        (N'SpecifyNextAccountContent', e.CAOInitiateCPMChangeWithManualPassword, d.SpecifyNextAccountContent),
        (N'CreateFolders', e.CAOCreateFolder, d.CreateFolders),
        (N'DeleteFolders', e.CAODeleteFolder, d.DeleteFolders),
        (N'UnlockAccounts', e.CAOUnlockObject, d.UnlockAccounts),
        (N'MoveAccountsAndFolders (MoveFrom)', e.CAOMoveFrom, d.MoveAccountsAndFolders),
        (N'MoveAccountsAndFolders (MoveInto)', e.CAOMoveInto, d.MoveAccountsAndFolders),
        (N'ManageSafe', e.CAOManageSafe, d.ManageSafe),
        (N'ManageSafeMembers', e.CAOManageSafeOwners, d.ManageSafeMembers),
        (N'BackupSafe', e.CAOBackup, d.BackupSafe),
        (N'AccessWithoutConfirmation', e.CAONoConfirmRequired, d.AccessWithoutConfirmation),
        (N'RequestsAuthorizationLevel1', e.CAOConfirm, d.RequestsAuthorizationLevel1)
    ) p(Field, EvdValue, DirectValue)
    WHERE p.EvdValue IN (N'YES', N'NO')
      AND EXISTS (SELECT CASE p.EvdValue WHEN N'YES' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END EXCEPT SELECT p.DirectValue)
),
auto AS (
    SELECT d.*,
           CASE
               -- Internal Safes: the built-in names (confirmed in the lab, 2026-10-20), or a name that starts with a CPM's name.
               WHEN d.ItemKind IN (N'Safe', N'Account', N'Member')
                    AND (d.SafeName IN (N'AppProviderCacheSafe', N'AppProviderConf', N'PasswordManagerShared', N'PasswordManagerTemp', N'Pictures',
                                        N'PSMLiveSessions', N'PSMNotifications', N'PSMSessions', N'PSMUnmanagedSessionAccounts',
                                        N'PVWAConfig', N'PVWAPrivateUserPrefs', N'PVWATaskDefinitions', N'PVWAUserPrefs', N'System')
                         OR EXISTS (SELECT 1 FROM cpm_name c WHERE LEFT(d.SafeName, LEN(c.Name)) = c.Name))
                   THEN N'Internal'
               -- PVWA gateway members.
               WHEN d.ItemKind = N'Member'
                    AND (d.ItemName = N'PVWAGWAccounts' OR d.ItemName LIKE N'PVWAGWUser%'
                         OR EXISTS (SELECT 1 FROM gateway_user g WHERE g.Id = d.PrincipalId))
                   THEN N'Expected'
           END AS AutoMarkCategory
    FROM diff d
)
SELECT d.Category, d.ItemKind, d.ItemKey, d.ItemName, d.SafeName, d.Field, d.EvdValue, d.DirectValue, d.SafeNumber, d.PrincipalId,
       COALESCE(mk.Category, d.AutoMarkCategory) AS MarkCategory,
       d.AutoMarkCategory,
       CAST(CASE WHEN mk.Category IS NULL AND d.AutoMarkCategory IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS MarkAuto,
       CAST(CASE WHEN d.Category IN (N'MissingFromDirect', N'AccountField') AND COALESCE(mk.Category, d.AutoMarkCategory) IS NULL THEN 1 ELSE 0 END AS BIT) AS Blocking
FROM auto d
-- The most specific mark wins: the item itself, then its Safe, then (for a member) the user or group.
OUTER APPLY (
    SELECT TOP (1) m.Category
    FROM web.shd_comparison_mark m
    WHERE (m.ItemKind = d.ItemKind AND m.ItemKey = d.ItemKey)
       OR (m.ItemKind = N'Safe' AND d.SafeNumber IS NOT NULL AND m.ItemKey = CAST(d.SafeNumber AS NVARCHAR(100)))
       OR (m.ItemKind IN (N'User', N'Group') AND d.ItemKind = N'Member' AND m.ItemKey = CAST(d.PrincipalId AS NVARCHAR(100)))
    ORDER BY CASE WHEN m.ItemKind = d.ItemKind THEN 0 WHEN m.ItemKind = N'Safe' THEN 1 ELSE 2 END
) mk;
GO

/* As in 42, storing the automatic mark too. */
CREATE OR ALTER PROCEDURE dbo.usp_Compare_SelfHostedShadow
    @PullKey BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @EvdStagedAt DATETIME2, @EvdSource NVARCHAR(260), @EvdUsable BIT = 1, @EvdNote NVARCHAR(400);
    SELECT @EvdStagedAt = MAX(LoadTimestamp), @EvdSource = MAX(SourceFileName) FROM dbo.stg_sh_safes;

    IF @EvdStagedAt IS NULL
        SELECT @EvdUsable = 0, @EvdNote = N'No EVD data is staged; the nightly import hasn''t staged Self-Hosted yet.';
    ELSE IF @EvdSource NOT LIKE N'%(EVD)'
        SELECT @EvdUsable = 0, @EvdNote = N'The staged Self-Hosted data came from a Direct pull, not the EVD. Wait for the next nightly EVD import.';
    ELSE IF @EvdStagedAt < DATEADD(HOUR, -26, SYSUTCDATETIME())
        SELECT @EvdUsable = 0, @EvdNote = N'The EVD data was staged more than 26 hours ago (' + CONVERT(NVARCHAR(30), @EvdStagedAt, 120) + N' UTC); check the nightly import.';

    SELECT Category, ItemKind, ItemKey, ItemName, SafeName, Field, EvdValue, DirectValue, MarkCategory, AutoMarkCategory, MarkAuto, Blocking, SafeNumber, PrincipalId
    INTO #diff
    FROM dbo.vw_shd_comparison;

    DECLARE @Blocking INT = (SELECT COUNT(*) FROM #diff WHERE Blocking = 1);

    BEGIN TRANSACTION;
    DELETE FROM dbo.shd_comparison_difference;
    DELETE FROM dbo.shd_comparison_run WHERE PullKey = @PullKey;

    INSERT INTO dbo.shd_comparison_run (PullKey, EvdStagedAt, EvdUsable, EvdNote, MissingFromDirect, MissingExplained, OnlyInDirect,
                                        AccountFieldDiffs, AccountFieldExplained, PermissionDiffs, LogonDomainDiffs, BlockingCount, IsClean)
    SELECT @PullKey, @EvdStagedAt, @EvdUsable, @EvdNote,
           COUNT(CASE WHEN Category = N'MissingFromDirect' THEN 1 END),
           COUNT(CASE WHEN Category = N'MissingFromDirect' AND MarkCategory IS NOT NULL THEN 1 END),
           COUNT(CASE WHEN Category = N'OnlyInDirect' THEN 1 END),
           COUNT(CASE WHEN Category = N'AccountField' THEN 1 END),
           COUNT(CASE WHEN Category = N'AccountField' AND MarkCategory IS NOT NULL THEN 1 END),
           COUNT(CASE WHEN Category = N'Permission' THEN 1 END),
           COUNT(CASE WHEN Category = N'LogonDomain' THEN 1 END),
           @Blocking,
           CASE WHEN @EvdUsable = 1 AND @Blocking = 0 THEN 1 ELSE 0 END
    FROM #diff;
    DECLARE @RunKey BIGINT = SCOPE_IDENTITY();

    INSERT INTO dbo.shd_comparison_difference (RunKey, Category, ItemKind, ItemKey, ItemName, SafeName, Field, EvdValue, DirectValue, MarkCategory,
                                               AutoMarkCategory, MarkAuto, Blocking, SafeNumber, PrincipalId)
    SELECT @RunKey, Category, ItemKind, ItemKey, LEFT(ItemName, 512), LEFT(SafeName, 300), Field, LEFT(EvdValue, 1000), LEFT(DirectValue, 1000), MarkCategory,
           AutoMarkCategory, MarkAuto, Blocking, SafeNumber, PrincipalId
    FROM #diff;
    COMMIT TRANSACTION;

    SELECT @RunKey AS RunKey;
END
GO

/* As in 42; a difference with no admin mark keeps its automatic one. */
CREATE OR ALTER PROCEDURE dbo.usp_Apply_ShadowComparisonMarks
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RunKey BIGINT = (SELECT MAX(RunKey) FROM dbo.shd_comparison_difference);
    IF @RunKey IS NULL RETURN;

    BEGIN TRANSACTION;
    UPDATE d
    SET MarkCategory = COALESCE(mk.Category, d.AutoMarkCategory),
        MarkAuto = CASE WHEN mk.Category IS NULL AND d.AutoMarkCategory IS NOT NULL THEN 1 ELSE 0 END,
        Blocking = CASE WHEN d.Category IN (N'MissingFromDirect', N'AccountField') AND COALESCE(mk.Category, d.AutoMarkCategory) IS NULL THEN 1 ELSE 0 END
    FROM dbo.shd_comparison_difference d
    OUTER APPLY (
        SELECT TOP (1) m.Category
        FROM web.shd_comparison_mark m
        WHERE (m.ItemKind = d.ItemKind AND m.ItemKey = d.ItemKey)
           OR (m.ItemKind = N'Safe' AND d.SafeNumber IS NOT NULL AND m.ItemKey = CAST(d.SafeNumber AS NVARCHAR(100)))
           OR (m.ItemKind IN (N'User', N'Group') AND d.ItemKind = N'Member' AND m.ItemKey = CAST(d.PrincipalId AS NVARCHAR(100)))
        ORDER BY CASE WHEN m.ItemKind = d.ItemKind THEN 0 WHEN m.ItemKind = N'Safe' THEN 1 ELSE 2 END
    ) mk
    WHERE d.RunKey = @RunKey;

    UPDATE r
    SET MissingExplained = c.MissingExplained, AccountFieldExplained = c.AccountFieldExplained, BlockingCount = c.BlockingCount,
        IsClean = CASE WHEN r.EvdUsable = 1 AND c.BlockingCount = 0 THEN 1 ELSE 0 END
    FROM dbo.shd_comparison_run r
    CROSS APPLY (
        SELECT COUNT(CASE WHEN Category = N'MissingFromDirect' AND MarkCategory IS NOT NULL THEN 1 END) AS MissingExplained,
               COUNT(CASE WHEN Category = N'AccountField' AND MarkCategory IS NOT NULL THEN 1 END) AS AccountFieldExplained,
               COUNT(CASE WHEN Blocking = 1 THEN 1 END) AS BlockingCount
        FROM dbo.shd_comparison_difference WHERE RunKey = @RunKey
    ) c
    WHERE r.RunKey = @RunKey;
    COMMIT TRANSACTION;
END
GO

PRINT '43_BlueTrack_ShadowComparisonRules.sql complete.';
GO
