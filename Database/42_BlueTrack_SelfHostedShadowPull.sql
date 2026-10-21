/* ============================================================================
   42_BlueTrack_SelfHostedShadowPull.sql

   RUN THIS AFTER 01-41. Guarded -- safe to re-run.

   D-271 (Claude_Docs/Design_CyberArk-Direct-Integration.md, "Shadow pull and
   comparison"): phase 2 of moving a site off the EVD. While the EVD stays the
   Self-Hosted source, the API can pull PVWA each night after the EVD import
   (a shadow pull) into the stg_shd_* tables only, and compare them with what
   the EVD import staged in stg_sh_*. Nothing the Load reads changes.

   - web.cyberark_source_config: ShadowPullEnabled and ShadowPullTime.
   - dbo.cyberark_direct_pull: IsShadow. A shadow pull never gates the import.
   - web.shd_comparison_mark: differences an admin marked as expected
     (Internal, or Expected with a note). A marked Safe covers its accounts
     and members; a marked user or group covers its Safe memberships.
   - dbo.vw_shd_comparison: the differences, live, with the marks applied.
   - dbo.shd_comparison_run: one row per compared shadow pull (history, for
     counting clean nights). dbo.shd_comparison_difference: the latest run's
     differences.
   - dbo.usp_Compare_SelfHostedShadow: stores a comparison for a pull.
   - dbo.usp_Import_Configured: the Direct gate ignores shadow pulls.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.cyberark_source_config', 'ShadowPullEnabled') IS NULL
    ALTER TABLE web.cyberark_source_config ADD ShadowPullEnabled BIT NOT NULL
        CONSTRAINT DF_cyberark_source_config_ShadowPullEnabled DEFAULT 0;
GO

IF COL_LENGTH('web.cyberark_source_config', 'ShadowPullTime') IS NULL
    ALTER TABLE web.cyberark_source_config ADD ShadowPullTime NVARCHAR(5) NOT NULL
        CONSTRAINT DF_cyberark_source_config_ShadowPullTime DEFAULT N'04:00';
GO

IF COL_LENGTH('dbo.cyberark_direct_pull', 'IsShadow') IS NULL
    ALTER TABLE dbo.cyberark_direct_pull ADD IsShadow BIT NOT NULL
        CONSTRAINT DF_cyberark_direct_pull_IsShadow DEFAULT 0;
GO

IF OBJECT_ID('web.shd_comparison_mark') IS NULL
BEGIN
    CREATE TABLE web.shd_comparison_mark (
        MarkKey   INT IDENTITY(1,1) CONSTRAINT PK_shd_comparison_mark PRIMARY KEY,
        ItemKind  NVARCHAR(20)  NOT NULL CONSTRAINT CK_shd_comparison_mark_ItemKind CHECK (ItemKind IN (N'Safe', N'Account', N'User', N'Group', N'Member')),
        ItemKey   NVARCHAR(100) NOT NULL,   -- Safe: SafeNumber; Account: SafeNumber_FileId; User/Group: id; Member: SafeNumber:MemberId
        ItemName  NVARCHAR(512) NULL,       -- as shown when it was marked
        Category  NVARCHAR(20)  NOT NULL CONSTRAINT CK_shd_comparison_mark_Category CHECK (Category IN (N'Internal', N'Expected')),
        Note      NVARCHAR(500) NULL,
        MarkedBy  INT           NULL CONSTRAINT FK_shd_comparison_mark_MarkedBy REFERENCES web.app_user(UserKey),
        MarkedAt  DATETIME2     NOT NULL CONSTRAINT DF_shd_comparison_mark_MarkedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_shd_comparison_mark UNIQUE (ItemKind, ItemKey)
    );
END
GO

IF OBJECT_ID('dbo.shd_comparison_run') IS NULL
BEGIN
    CREATE TABLE dbo.shd_comparison_run (
        RunKey                BIGINT IDENTITY(1,1) CONSTRAINT PK_shd_comparison_run PRIMARY KEY,
        PullKey               BIGINT        NOT NULL CONSTRAINT UQ_shd_comparison_run_PullKey UNIQUE,   -- not a foreign key: the pull log is purged after 30 days
        ComparedAt            DATETIME2     NOT NULL CONSTRAINT DF_shd_comparison_run_ComparedAt DEFAULT SYSUTCDATETIME(),
        EvdStagedAt           DATETIME2     NULL,      -- when the EVD import staged the stg_sh_* rows compared
        EvdUsable             BIT           NOT NULL,  -- 0: no EVD data, data staged by a Direct pull, or stale; the night doesn't count
        EvdNote               NVARCHAR(400) NULL,
        MissingFromDirect     INT           NOT NULL,  -- every kind, marked ones included
        MissingExplained      INT           NOT NULL,
        OnlyInDirect          INT           NOT NULL,
        AccountFieldDiffs     INT           NOT NULL,
        AccountFieldExplained INT           NOT NULL,
        PermissionDiffs       INT           NOT NULL,
        LogonDomainDiffs      INT           NOT NULL,
        BlockingCount         INT           NOT NULL,  -- unmarked MissingFromDirect and AccountField rows
        IsClean               BIT           NOT NULL
    );
END
GO

IF OBJECT_ID('dbo.shd_comparison_difference') IS NULL
BEGIN
    CREATE TABLE dbo.shd_comparison_difference (
        DifferenceKey BIGINT IDENTITY(1,1) CONSTRAINT PK_shd_comparison_difference PRIMARY KEY,
        RunKey        BIGINT         NOT NULL,
        Category      NVARCHAR(30)   NOT NULL,   -- MissingFromDirect, OnlyInDirect, AccountField, Permission, LogonDomain
        ItemKind      NVARCHAR(20)   NOT NULL,
        ItemKey       NVARCHAR(100)  NOT NULL,
        ItemName      NVARCHAR(512)  NULL,
        SafeName      NVARCHAR(300)  NULL,
        Field         NVARCHAR(100)  NULL,
        EvdValue      NVARCHAR(1000) NULL,
        DirectValue   NVARCHAR(1000) NULL,
        MarkCategory  NVARCHAR(20)   NULL,       -- the mark that explains it, if any
        Blocking      BIT            NOT NULL,
        SafeNumber    BIGINT         NULL,       -- for marking the whole Safe
        PrincipalId   BIGINT         NULL        -- a member's user or group id, for marking all its memberships
    );
    CREATE NONCLUSTERED INDEX IX_shd_comparison_difference_RunKey ON dbo.shd_comparison_difference (RunKey, Category);
END
GO

/* The differences between the EVD-staged stg_sh_* tables and the shadow pull's stg_shd_* tables, by key. Uses the
   Load's own filters on the EVD side (password objects, not deleted, D-184) and leaves out ignored Safes on both.
   Values compare as the database collation does (case-insensitive). A permission the EVD import leaves NULL isn't
   compared. Blocking: an unmarked MissingFromDirect or AccountField row (D-271). */
CREATE OR ALTER VIEW dbo.vw_shd_comparison
AS
WITH cfg AS (
    SELECT TOP (1) SafeIgnoreMode AS M, SafeIgnoreValue AS V FROM web.app_config
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
           MAX(CASE WHEN op.CAOPObjectPropertyName = N'LogonDomain' THEN op.CAOPObjectPropertyValue END) AS LogonDomain
    FROM dbo.stg_sh_files f
    CROSS JOIN cfg
    LEFT JOIN dbo.stg_sh_objectproperties op ON op.CAOPSafeId = f.CAFSafeID AND op.CAOPFileId = f.CAFFileID
    WHERE f.CAFType = 2
      AND CASE WHEN f.CAFDeletionDate >= '19700102' AND (f.CAFCreationDate IS NULL OR f.CAFDeletionDate >= f.CAFCreationDate) THEN 1 ELSE 0 END = 0
      AND dbo.fn_MatchesNamePattern(f.CAFSafeName, cfg.M, cfg.V) = 0
    GROUP BY f.CAFSafeID, f.CAFFileID, f.CAFSafeName, f.CAFFileName
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
    FROM evd_acct e WHERE NOT EXISTS (SELECT 1 FROM shd_acct d WHERE d.AccountId = e.AccountId)
    UNION ALL
    SELECT N'MissingFromDirect', N'User', CAST(e.CAUUserID AS NVARCHAR(100)), e.CAUUserName, NULL, NULL, NULL, NULL, NULL, e.CAUUserID
    FROM dbo.stg_sh_users e WHERE NOT EXISTS (SELECT 1 FROM dbo.stg_shd_users d WHERE d.UserId = e.CAUUserID)
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
    FROM evd_acct e
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
)
SELECT d.Category, d.ItemKind, d.ItemKey, d.ItemName, d.SafeName, d.Field, d.EvdValue, d.DirectValue, d.SafeNumber, d.PrincipalId,
       mk.Category AS MarkCategory,
       CAST(CASE WHEN d.Category IN (N'MissingFromDirect', N'AccountField') AND mk.Category IS NULL THEN 1 ELSE 0 END AS BIT) AS Blocking
FROM diff d
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

/* Compares the staged data now and stores it as the comparison for @PullKey (a shadow pull), replacing an earlier
   comparison of the same pull. Only the latest comparison keeps its differences; every run keeps its counts. */
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

    SELECT Category, ItemKind, ItemKey, ItemName, SafeName, Field, EvdValue, DirectValue, MarkCategory, Blocking, SafeNumber, PrincipalId
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

    INSERT INTO dbo.shd_comparison_difference (RunKey, Category, ItemKind, ItemKey, ItemName, SafeName, Field, EvdValue, DirectValue, MarkCategory, Blocking, SafeNumber, PrincipalId)
    SELECT @RunKey, Category, ItemKind, ItemKey, LEFT(ItemName, 512), LEFT(SafeName, 300), Field, LEFT(EvdValue, 1000), LEFT(DirectValue, 1000), MarkCategory, Blocking, SafeNumber, PrincipalId
    FROM #diff;
    COMMIT TRANSACTION;

    SELECT @RunKey AS RunKey;
END
GO

/* After an admin adds or removes a mark: re-applies the marks to the latest comparison's stored differences and
   recounts that run, without comparing again (staging may have moved on since). Same mark rules as the view. */
CREATE OR ALTER PROCEDURE dbo.usp_Apply_ShadowComparisonMarks
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RunKey BIGINT = (SELECT MAX(RunKey) FROM dbo.shd_comparison_difference);
    IF @RunKey IS NULL RETURN;

    BEGIN TRANSACTION;
    UPDATE d
    SET MarkCategory = mk.Category,
        Blocking = CASE WHEN d.Category IN (N'MissingFromDirect', N'AccountField') AND mk.Category IS NULL THEN 1 ELSE 0 END
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

/* usp_Import_Configured: as in 38_BlueTrack_SelfHostedDirect.sql, except that the Direct gate looks only at real
   pulls: a shadow pull (D-271) writes only the stg_shd_* tables, so it must never let the Import go ahead. */
CREATE OR ALTER PROCEDURE dbo.usp_Import_Configured
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Import');
    SET @EtlRunKey = SCOPE_IDENTITY();

    BEGIN TRY
        DECLARE @SelfHostedSource NVARCHAR(10), @SelfHostedStaged BIT = 0;
        DECLARE @ImportPrivilegeCloud BIT, @ImportSelfHosted BIT, @Folder NVARCHAR(400), @EvdDatabaseName NVARCHAR(128),
                @Platforms NVARCHAR(200), @Users NVARCHAR(200), @Groups NVARCHAR(200), @GroupMembers NVARCHAR(200),
                @Safes NVARCHAR(200), @Accounts NVARCHAR(200), @Entitlements NVARCHAR(200);

        SELECT @ImportPrivilegeCloud = ImportPrivilegeCloud, @ImportSelfHosted = ImportSelfHosted,
               @Folder = ExportFolderPath, @EvdDatabaseName = EvdDatabaseName,
               @Platforms = PlatformsFileName, @Users = UsersFileName, @Groups = GroupsFileName,
               @GroupMembers = GroupMembersFileName, @Safes = SafesFileName, @Accounts = AccountsFileName,
               @Entitlements = EntitlementsFileName, @SelfHostedSource = SelfHostedSource
        FROM web.cyberark_source_config
        WHERE CyberArkSourceConfigKey = 1;

        IF @@ROWCOUNT = 0
            THROW 50000, 'usp_Import_Configured: there are no CyberArk source settings. Set them on Admin > Data Sources, or re-run the installer''s Db.NightlyJob step.', 1;

        /* D-248: with Self-Hosted Direct the API pulls PVWA into the stg_sh_* tables ahead of this job (at DirectPullTime).
           The Import only checks that the latest pull succeeded and is recent. A rejected or failed pull fails the night,
           so the Load doesn't run on stale or incomplete data and NightlyImportFailed mails the reason.
           D-271: shadow pulls don't count. */
        IF @ImportSelfHosted = 1 AND @SelfHostedSource = N'Direct'
        BEGIN
            DECLARE @PullStatus NVARCHAR(20), @PullCompleted DATETIME2, @PullMessage NVARCHAR(2000), @PullError NVARCHAR(2048);
            SELECT TOP (1) @PullStatus = Status, @PullCompleted = CompletedAt, @PullMessage = Message
            FROM dbo.cyberark_direct_pull WHERE IsShadow = 0 ORDER BY PullKey DESC;

            IF @PullStatus IS NULL
                SET @PullError = N'Self-Hosted Direct: no PVWA pull has run yet. Run one from Admin > Data Sources, or check that the API is running.';
            ELSE IF @PullStatus = N'Started'
                SET @PullError = N'Self-Hosted Direct: the latest PVWA pull never finished.';
            ELSE IF @PullStatus IN (N'Failed', N'Rejected')
                SET @PullError = N'Self-Hosted Direct: the latest PVWA pull ' + LOWER(@PullStatus) + N'. ' + ISNULL(@PullMessage, N'');
            ELSE IF @PullCompleted < DATEADD(HOUR, -20, SYSUTCDATETIME())
                SET @PullError = N'Self-Hosted Direct: the latest successful PVWA pull is more than 20 hours old (' + CONVERT(NVARCHAR(30), @PullCompleted, 120) + N' UTC).';
            IF @PullError IS NOT NULL THROW 50000, @PullError, 1;

            SET @ImportSelfHosted = 0;
            SET @SelfHostedStaged = 1;
        END

        DECLARE @Today DATE = CAST(SYSDATETIME() AS DATE);
        DECLARE @PlatformsFile NVARCHAR(500), @UsersFile NVARCHAR(500), @GroupsFile NVARCHAR(500), @GroupMembersFile NVARCHAR(500),
                @SafesFile NVARCHAR(500), @AccountsFile NVARCHAR(500), @EntitlementsFile NVARCHAR(500);

        IF @ImportPrivilegeCloud = 1 AND NULLIF(LTRIM(RTRIM(@Folder)), N'') IS NOT NULL
        BEGIN
            IF RIGHT(@Folder, 1) <> N'\' SET @Folder = @Folder + N'\';
            -- usp_Import_All refuses a NULL path, so a bad {token} fails the run before anything is imported.
            SET @PlatformsFile    = @Folder + dbo.fn_ExpandDateTokens(@Platforms, @Today);
            SET @UsersFile        = @Folder + dbo.fn_ExpandDateTokens(@Users, @Today);
            SET @GroupsFile       = @Folder + dbo.fn_ExpandDateTokens(@Groups, @Today);
            SET @GroupMembersFile = @Folder + dbo.fn_ExpandDateTokens(@GroupMembers, @Today);
            SET @SafesFile        = @Folder + dbo.fn_ExpandDateTokens(@Safes, @Today);
            SET @AccountsFile     = @Folder + dbo.fn_ExpandDateTokens(@Accounts, @Today);
            SET @EntitlementsFile = @Folder + dbo.fn_ExpandDateTokens(@Entitlements, @Today);
        END

        EXEC usp_Import_All
            @EVDDatabaseName = @EvdDatabaseName,
            @PlatformsFile = @PlatformsFile,
            @UsersFile = @UsersFile,
            @GroupsFile = @GroupsFile,
            @GroupMembersFile = @GroupMembersFile,
            @SafesFile = @SafesFile,
            @AccountsFile = @AccountsFile,
            @EntitlementsFile = @EntitlementsFile,
            @EntitlementsExportDate = @Today,
            @ImportPrivilegeCloud = @ImportPrivilegeCloud,
            @ImportSelfHosted = @ImportSelfHosted,
            @SelfHostedStaged = @SelfHostedStaged;

        UPDATE dbo.etl_run_log SET Status = 'Succeeded', CompletedAt = SYSUTCDATETIME() WHERE EtlRunKey = @EtlRunKey;
    END TRY
    BEGIN CATCH
        -- A doomed transaction can't log; the import opens none of its own.
        IF XACT_STATE() = -1 ROLLBACK TRANSACTION;
        UPDATE dbo.etl_run_log
        SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(ERROR_MESSAGE(), 2000),
            ErrorProcedure = ERROR_PROCEDURE(), ErrorLine = ERROR_LINE()
        WHERE EtlRunKey = @EtlRunKey;
        THROW;
    END CATCH
END
GO

PRINT '42_BlueTrack_SelfHostedShadowPull.sql complete.';
GO
