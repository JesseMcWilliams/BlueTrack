/* ============================================================================
   24_BlueTrack_EntitlementMemberResolution.sql

   RUN THIS AFTER 01-23. Guarded -- safe to re-run.

   D-232: resolving Unresolved Entitlement Members, decided by the user on
   2026-10-14. D-145 assumed an unresolved member's ID was in an identifier
   space dim_user/dim_group never see. On the real data every one of them is
   a user or group the Users/Groups exports do have, under a different ID:
   the Entitlements export and the Users/Groups exports identify the same
   principal differently, and the name matches exactly.

     fact_safe_entitlement.MemberName       the member's name from the
                                            Entitlements export (always filled
                                            there). Shown on the report.
     fact_safe_entitlement.ResolutionMethod how the member was resolved:
                                            ById (the export's ID matched),
                                            ByName (the only PRIVCLOUD user or
                                            group with that exact name, case
                                            ignored), Manual (linked on the
                                            report), External (labelled
                                            "External / cloud role" on the
                                            report), NULL (unresolved).
                                            UnresolvedMemberId keeps the
                                            export's ID whenever the ID did
                                            not match (ByName, Manual,
                                            External, unresolved), so the
                                            report and audit can show it.
     CK_entitlement_member                  replaced by
                                            CK_fact_safe_entitlement_member
                                            (a resolved row may now also keep
                                            UnresolvedMemberId) and
                                            CK_fact_safe_entitlement_resolution.
     web.entitlement_member_resolution      manual resolutions, keyed by
                                            source system + member type +
                                            the export's member ID, so they
                                            survive the nightly reload: Linked
                                            (to a dim_user/dim_group; their
                                            keys are stable, the loads MERGE
                                            and never delete) or External
                                            (with an optional note).
     usp_Resolve_EntitlementMembers         resolves the members in the
                                            caller's #entitlement_member:
                                            manual first, then ID, then name.
     usp_Load_FactSafeEntitlement           redefined: Privilege Cloud rows
                                            resolve through it; a staging
                                            row's EntitlementKey comes from
                                            MERGE ... OUTPUT, not a rejoin on
                                            the natural key. Self-Hosted
                                            unchanged except ResolutionMethod.
     usp_Apply_EntitlementMemberResolution  re-resolves one member's current
                                            rows at once (after Resolve or
                                            Undo on the report).
     vw_unresolved_entitlement_members      redefined: every row whose ID did
                                            not match, with its name and how
                                            (and by whom) it was resolved.
     EntitlementMemberResolution            audit event type for Resolve and
                                            Undo.

   Existing rows: resolved ones are marked ById; unresolved ones get their
   name from staging. The name match itself runs with the next load.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('dbo.fact_safe_entitlement', 'MemberName') IS NULL
    ALTER TABLE dbo.fact_safe_entitlement ADD MemberName NVARCHAR(300) NULL;
GO

IF COL_LENGTH('dbo.fact_safe_entitlement', 'ResolutionMethod') IS NULL
    ALTER TABLE dbo.fact_safe_entitlement ADD ResolutionMethod NVARCHAR(20) NULL;
GO

UPDATE dbo.fact_safe_entitlement
SET ResolutionMethod = 'ById'
WHERE ResolutionMethod IS NULL AND (UserKey IS NOT NULL OR GroupKey IS NOT NULL);
GO

UPDATE fse
SET MemberName = e.MemberName
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_safe sf ON sf.SafeKey = fse.SafeKey
JOIN dbo.dim_source_system s ON s.SourceSystemKey = sf.SourceSystemKey AND s.SourceSystemCode = 'PRIVCLOUD'
CROSS APPLY (SELECT TOP (1) x.MemberName FROM dbo.stg_pc_entitlements x
             WHERE x.SafeUrlId = sf.SafeUrlId AND x.MemberType = fse.MemberType AND x.MemberId = fse.UnresolvedMemberId) e
WHERE fse.MemberName IS NULL AND fse.UnresolvedMemberId IS NOT NULL;
GO

IF OBJECT_ID('dbo.CK_entitlement_member', 'C') IS NOT NULL
    ALTER TABLE dbo.fact_safe_entitlement DROP CONSTRAINT CK_entitlement_member;
GO

IF OBJECT_ID('dbo.CK_fact_safe_entitlement_member', 'C') IS NULL
    ALTER TABLE dbo.fact_safe_entitlement ADD CONSTRAINT CK_fact_safe_entitlement_member CHECK (
        (MemberType = 'User'  AND UserKey IS NOT NULL AND GroupKey IS NULL) OR
        (MemberType = 'Group' AND GroupKey IS NOT NULL AND UserKey IS NULL) OR
        (UserKey IS NULL AND GroupKey IS NULL AND UnresolvedMemberId IS NOT NULL)
    );
GO

IF OBJECT_ID('dbo.CK_fact_safe_entitlement_resolution', 'C') IS NULL
    ALTER TABLE dbo.fact_safe_entitlement ADD CONSTRAINT CK_fact_safe_entitlement_resolution CHECK (
        (ResolutionMethod IS NULL AND UserKey IS NULL AND GroupKey IS NULL) OR
        (ResolutionMethod = 'ById' AND UnresolvedMemberId IS NULL AND (UserKey IS NOT NULL OR GroupKey IS NOT NULL)) OR
        (ResolutionMethod IN ('ByName', 'Manual') AND UnresolvedMemberId IS NOT NULL AND (UserKey IS NOT NULL OR GroupKey IS NOT NULL)) OR
        (ResolutionMethod = 'External' AND UnresolvedMemberId IS NOT NULL AND UserKey IS NULL AND GroupKey IS NULL)
    );
GO

IF OBJECT_ID('web.entitlement_member_resolution', 'U') IS NULL
    CREATE TABLE web.entitlement_member_resolution (
        EntitlementMemberResolutionKey INT IDENTITY(1,1) PRIMARY KEY,
        SourceSystemKey   INT            NOT NULL REFERENCES dbo.dim_source_system(SourceSystemKey),
        MemberType        NVARCHAR(20)   NOT NULL,
        SourceMemberId    NVARCHAR(300)  NOT NULL,
        MemberName        NVARCHAR(300)  NULL,
        Resolution        NVARCHAR(20)   NOT NULL,
        UserKey           INT            NULL REFERENCES dbo.dim_user(UserKey),
        GroupKey          INT            NULL REFERENCES dbo.dim_group(GroupKey),
        Note              NVARCHAR(500)  NULL,
        ResolvedBy        INT            NOT NULL REFERENCES web.app_user(UserKey),
        ResolvedDate      DATETIME2      NOT NULL CONSTRAINT DF_entitlement_member_resolution_ResolvedDate DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_entitlement_member_resolution UNIQUE (SourceSystemKey, MemberType, SourceMemberId),
        CONSTRAINT CK_entitlement_member_resolution CHECK (
            (Resolution = 'Linked' AND MemberType = 'User'  AND UserKey IS NOT NULL AND GroupKey IS NULL) OR
            (Resolution = 'Linked' AND MemberType = 'Group' AND GroupKey IS NOT NULL AND UserKey IS NULL) OR
            (Resolution = 'External' AND MemberType IN ('User', 'Group') AND UserKey IS NULL AND GroupKey IS NULL)
        )
    );
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT 'EntitlementMemberResolution', 'An unresolved entitlement member was resolved (linked to a user or group, or labelled external) or its resolution undone'
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'EntitlementMemberResolution');
GO

/* usp_Resolve_EntitlementMembers: fills UserKey/GroupKey/ResolutionMethod in
   the caller's #entitlement_member (MemberType, MemberId, MemberName,
   UserKey, GroupKey, ResolutionMethod), in this order:
     1. a manual resolution (web.entitlement_member_resolution) -- Manual or External
     2. the export's ID = dim_user.SourceUserId / dim_group.SourceGroupId -- ById
     3. the export's name = dim_user.Username / dim_group.GroupName, case
        ignored, only when exactly one user (or group) of that source has
        that name -- ByName. Two with the same name stay unresolved. */
CREATE OR ALTER PROCEDURE dbo.usp_Resolve_EntitlementMembers
    @SourceSystemKey INT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE m
    SET UserKey = r.UserKey, GroupKey = r.GroupKey,
        ResolutionMethod = CASE r.Resolution WHEN 'Linked' THEN 'Manual' ELSE 'External' END
    FROM #entitlement_member m
    JOIN web.entitlement_member_resolution r
        ON r.SourceSystemKey = @SourceSystemKey AND r.MemberType = m.MemberType AND r.SourceMemberId = m.MemberId;

    UPDATE m SET UserKey = u.UserKey, ResolutionMethod = 'ById'
    FROM #entitlement_member m
    JOIN dbo.dim_user u ON u.SourceSystemKey = @SourceSystemKey AND u.SourceUserId = m.MemberId
    WHERE m.ResolutionMethod IS NULL AND m.MemberType = 'User';

    UPDATE m SET GroupKey = g.GroupKey, ResolutionMethod = 'ById'
    FROM #entitlement_member m
    JOIN dbo.dim_group g ON g.SourceSystemKey = @SourceSystemKey AND g.SourceGroupId = m.MemberId
    WHERE m.ResolutionMethod IS NULL AND m.MemberType = 'Group';

    UPDATE m SET UserKey = n.UserKey, ResolutionMethod = 'ByName'
    FROM #entitlement_member m
    JOIN (SELECT u.Username COLLATE Latin1_General_CI_AS AS Name, MIN(u.UserKey) AS UserKey
          FROM dbo.dim_user u
          WHERE u.SourceSystemKey = @SourceSystemKey
          GROUP BY u.Username COLLATE Latin1_General_CI_AS
          HAVING COUNT(*) = 1) n
        ON n.Name = m.MemberName COLLATE Latin1_General_CI_AS
    WHERE m.ResolutionMethod IS NULL AND m.MemberType = 'User';

    UPDATE m SET GroupKey = n.GroupKey, ResolutionMethod = 'ByName'
    FROM #entitlement_member m
    JOIN (SELECT g.GroupName COLLATE Latin1_General_CI_AS AS Name, MIN(g.GroupKey) AS GroupKey
          FROM dbo.dim_group g
          WHERE g.SourceSystemKey = @SourceSystemKey
          GROUP BY g.GroupName COLLATE Latin1_General_CI_AS
          HAVING COUNT(*) = 1) n
        ON n.Name = m.MemberName COLLATE Latin1_General_CI_AS
    WHERE m.ResolutionMethod IS NULL AND m.MemberType = 'Group';
END
GO

/* usp_Load_FactSafeEntitlement: as in 02_BlueTrack_Baseline_EtlLoads.sql,
   but Privilege Cloud members resolve through usp_Resolve_EntitlementMembers
   (manual, ID, name) and every row is kept, resolved or not, in one pass.
   Each staging row's EntitlementKey is captured with MERGE ... OUTPUT
   (which, unlike INSERT ... OUTPUT, can return source columns), so a member
   resolved by name to a user who also holds the same safe by ID can't fan
   the permission bridge out. */
CREATE OR ALTER PROCEDURE dbo.usp_Load_FactSafeEntitlement
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);

    -- clear and reload each run -- entitlement exports are point-in-time snapshots
    DELETE bep FROM bridge_entitlement_permission bep
        JOIN fact_safe_entitlement fse ON fse.EntitlementKey = bep.EntitlementKey
        JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey
        WHERE sf.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey);
    DELETE fse FROM fact_safe_entitlement fse
        JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey
        WHERE sf.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey);

    -- ---- Privilege Cloud: resolve each distinct member once ----
    CREATE TABLE #entitlement_member (
        MemberType       NVARCHAR(20)  COLLATE DATABASE_DEFAULT NOT NULL,
        MemberId         NVARCHAR(300) COLLATE DATABASE_DEFAULT NOT NULL,
        MemberName       NVARCHAR(300) COLLATE DATABASE_DEFAULT NULL,
        UserKey          INT           NULL,
        GroupKey         INT           NULL,
        ResolutionMethod NVARCHAR(20)  COLLATE DATABASE_DEFAULT NULL,
        PRIMARY KEY (MemberType, MemberId)
    );

    INSERT INTO #entitlement_member (MemberType, MemberId, MemberName)
    SELECT e.MemberType, e.MemberId, MAX(e.MemberName)
    FROM stg_pc_entitlements e
    JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
    WHERE e.MemberType IN ('User', 'Group')
    GROUP BY e.MemberType, e.MemberId;

    EXEC dbo.usp_Resolve_EntitlementMembers @SourceSystemKey = @PrivCloudKey;

    CREATE TABLE #entitlement_row (EntitlementKey BIGINT NOT NULL PRIMARY KEY, LoadId BIGINT NOT NULL);

    MERGE fact_safe_entitlement AS tgt
    USING (
        SELECT e.LoadId, sf.SafeKey, e.MemberType, m.UserKey, m.GroupKey,
               CASE WHEN m.ResolutionMethod = 'ById' THEN NULL ELSE e.MemberId END AS UnresolvedMemberId,
               e.MemberName, m.ResolutionMethod,
               e.MembershipExpirationDate, e.IsExpiredMembershipEnable, e.IsPredefinedUser
        FROM stg_pc_entitlements e
        JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
        JOIN #entitlement_member m ON m.MemberType = e.MemberType AND m.MemberId = e.MemberId
    ) AS src
    ON 1 = 0
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SafeKey, MemberType, UserKey, GroupKey, UnresolvedMemberId, MemberName, ResolutionMethod,
                MembershipExpirationDate, IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
        VALUES (src.SafeKey, src.MemberType, src.UserKey, src.GroupKey, src.UnresolvedMemberId, src.MemberName, src.ResolutionMethod,
                src.MembershipExpirationDate, src.IsExpiredMembershipEnable, src.IsPredefinedUser, @Today)
    OUTPUT inserted.EntitlementKey, src.LoadId INTO #entitlement_row (EntitlementKey, LoadId);

    INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
    SELECT r.EntitlementKey, pa.PermissionKey, v.IsGranted
    FROM #entitlement_row r
    JOIN stg_pc_entitlements e ON e.LoadId = r.LoadId
    CROSS APPLY (VALUES
        ('UseAccounts', e.UseAccounts), ('RetrieveAccounts', e.RetrieveAccounts), ('ListAccounts', e.ListAccounts),
        ('AddAccounts', e.AddAccounts), ('UpdateAccountContent', e.UpdateAccountContent),
        ('UpdateAccountProperties', e.UpdateAccountProperties),
        ('InitiateCPMAccountManagementOperations', e.InitiateCPMAccountManagementOperations),
        ('SpecifyNextAccountContent', e.SpecifyNextAccountContent), ('RenameAccounts', e.RenameAccounts),
        ('DeleteAccounts', e.DeleteAccounts), ('UnlockAccounts', e.UnlockAccounts), ('ManageSafe', e.ManageSafe),
        ('ManageSafeMembers', e.ManageSafeMembers), ('BackupSafe', e.BackupSafe), ('ViewAuditLog', e.ViewAuditLog),
        ('ViewSafeMembers', e.ViewSafeMembers), ('AccessWithoutConfirmation', e.AccessWithoutConfirmation),
        ('CreateFolders', e.CreateFolders), ('DeleteFolders', e.DeleteFolders),
        ('MoveAccountsAndFolders', e.MoveAccountsAndFolders),
        ('RequestsAuthorizationLevel1', e.RequestsAuthorizationLevel1),
        ('RequestsAuthorizationLevel2', e.RequestsAuthorizationLevel2)
    ) AS v(RawPermissionName, IsGranted)
    JOIN permission_alias pa ON pa.SourceSystemKey = @PrivCloudKey AND pa.RawPermissionName = v.RawPermissionName
    WHERE v.IsGranted IS NOT NULL;

    -- ---- Self-Hosted entitlements (CAOwners), as before ----
    -- CAOOwnerType decodes via dim_selfhosted_code type 10: 0=User, 1=Group, 2=Gateway account.
    -- Gateway-account owners aren't represented in dim_user or dim_group and are excluded.
    INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, GroupKey, ResolutionMethod, MembershipExpirationDate,
                                        IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
    SELECT sf.SafeKey,
           CASE o.CAOOwnerType WHEN 0 THEN 'User' WHEN 1 THEN 'Group' ELSE NULL END,
           u.UserKey, g.GroupKey, 'ById', o.CAOExpirationDate, NULL, NULL, @Today
    FROM stg_sh_owners o
    JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(o.CAOSafeID AS NVARCHAR(300))
    LEFT JOIN dim_user  u ON o.CAOOwnerType = 0 AND u.SourceSystemKey = @SelfHostedKey AND u.SourceUserId  = CAST(o.CAOOwnerID AS NVARCHAR(100))
    LEFT JOIN dim_group g ON o.CAOOwnerType = 1 AND g.SourceSystemKey = @SelfHostedKey AND g.SourceGroupId = CAST(o.CAOOwnerID AS NVARCHAR(100))
    WHERE o.CAOOwnerType IN (0, 1);

    INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
    SELECT fse.EntitlementKey, pa.PermissionKey, dbo.ufn_YNToBit(v.RawValue)
    FROM stg_sh_owners o
    JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(o.CAOSafeID AS NVARCHAR(300))
    LEFT JOIN dim_user  u ON o.CAOOwnerType = 0 AND u.SourceSystemKey = @SelfHostedKey AND u.SourceUserId  = CAST(o.CAOOwnerID AS NVARCHAR(100))
    LEFT JOIN dim_group g ON o.CAOOwnerType = 1 AND g.SourceSystemKey = @SelfHostedKey AND g.SourceGroupId = CAST(o.CAOOwnerID AS NVARCHAR(100))
    JOIN fact_safe_entitlement fse
        ON fse.SafeKey = sf.SafeKey
       AND fse.MemberType = CASE o.CAOOwnerType WHEN 0 THEN 'User' WHEN 1 THEN 'Group' ELSE NULL END
       AND ISNULL(fse.UserKey, -1)  = ISNULL(u.UserKey, -1)
       AND ISNULL(fse.GroupKey, -1) = ISNULL(g.GroupKey, -1)
       AND fse.SnapshotDate = @Today
    CROSS APPLY (VALUES
        ('CAOList', o.CAOList), ('CAORetrieve', o.CAORetrieve), ('CAOCreateObject', o.CAOCreateObject),
        ('CAOUpdateObject', o.CAOUpdateObject), ('CAOUpdateObjectProperties', o.CAOUpdateObjectProperties),
        ('CAORenameObject', o.CAORenameObject), ('CAODelete', o.CAODelete),
        ('CAOInitiateCPMChange', o.CAOInitiateCPMChange),
        ('CAOInitiateCPMChangeWithManualPassword', o.CAOInitiateCPMChangeWithManualPassword),
        ('CAOCreateFolder', o.CAOCreateFolder), ('CAODeleteFolder', o.CAODeleteFolder),
        ('CAOUnlockObject', o.CAOUnlockObject), ('CAOMoveFrom', o.CAOMoveFrom), ('CAOMoveInto', o.CAOMoveInto),
        ('CAOManageSafe', o.CAOManageSafe), ('CAOManageSafeOwners', o.CAOManageSafeOwners),
        ('CAOBackup', o.CAOBackup), ('CAONoConfirmRequired', o.CAONoConfirmRequired),
        ('CAOEventsList', o.CAOEventsList)
    ) AS v(RawPermissionName, RawValue)
    JOIN permission_alias pa ON pa.SourceSystemKey = @SelfHostedKey AND pa.RawPermissionName = v.RawPermissionName
    WHERE o.CAOOwnerType IN (0, 1) AND dbo.ufn_YNToBit(v.RawValue) IS NOT NULL;
END
GO

/* usp_Apply_EntitlementMemberResolution: after a manual resolution is saved
   or removed, re-resolves that member's current rows (those whose ID did not
   match at the last load) the same way the load would, so the report changes
   at once instead of after the next nightly load. Returns the rows changed. */
CREATE OR ALTER PROCEDURE dbo.usp_Apply_EntitlementMemberResolution
    @SourceSystemKey INT,
    @MemberType NVARCHAR(20),
    @SourceMemberId NVARCHAR(300)
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #entitlement_member (
        MemberType       NVARCHAR(20)  COLLATE DATABASE_DEFAULT NOT NULL,
        MemberId         NVARCHAR(300) COLLATE DATABASE_DEFAULT NOT NULL,
        MemberName       NVARCHAR(300) COLLATE DATABASE_DEFAULT NULL,
        UserKey          INT           NULL,
        GroupKey         INT           NULL,
        ResolutionMethod NVARCHAR(20)  COLLATE DATABASE_DEFAULT NULL,
        PRIMARY KEY (MemberType, MemberId)
    );

    INSERT INTO #entitlement_member (MemberType, MemberId, MemberName)
    SELECT fse.MemberType, fse.UnresolvedMemberId, MAX(fse.MemberName)
    FROM dbo.fact_safe_entitlement fse
    JOIN dbo.dim_safe sf ON sf.SafeKey = fse.SafeKey AND sf.SourceSystemKey = @SourceSystemKey
    WHERE fse.MemberType = @MemberType AND fse.UnresolvedMemberId = @SourceMemberId
    GROUP BY fse.MemberType, fse.UnresolvedMemberId;

    EXEC dbo.usp_Resolve_EntitlementMembers @SourceSystemKey = @SourceSystemKey;

    UPDATE fse
    SET UserKey = m.UserKey, GroupKey = m.GroupKey, ResolutionMethod = m.ResolutionMethod,
        UnresolvedMemberId = CASE WHEN m.ResolutionMethod = 'ById' THEN NULL ELSE fse.UnresolvedMemberId END
    FROM dbo.fact_safe_entitlement fse
    JOIN dbo.dim_safe sf ON sf.SafeKey = fse.SafeKey AND sf.SourceSystemKey = @SourceSystemKey
    JOIN #entitlement_member m ON m.MemberType = fse.MemberType AND m.MemberId = fse.UnresolvedMemberId;

    SELECT @@ROWCOUNT AS RowsChanged;
END
GO

/* Every entitlement whose member ID did not match (UnresolvedMemberId set):
   unresolved ones (ResolutionMethod NULL) and the ones resolved by name, by
   hand or labelled external, with whom they resolved to and who did it. */
CREATE OR ALTER VIEW dbo.vw_unresolved_entitlement_members AS
SELECT
    fse.EntitlementKey,
    fse.SafeKey,
    ds.SafeName,
    ds.SourceSystemKey,
    fse.MemberType,
    fse.UnresolvedMemberId,
    fse.MemberName,
    fse.ResolutionMethod,
    fse.UserKey,
    fse.GroupKey,
    COALESCE(u.Username, g.GroupName) AS ResolvedToName,
    CASE WHEN fse.ResolutionMethod IN ('Manual', 'External') THEN r.Note END AS ResolutionNote,
    CASE WHEN fse.ResolutionMethod IN ('Manual', 'External') THEN COALESCE(au.DisplayName, au.ExternalIdentifier) END AS ResolvedByName,
    CASE WHEN fse.ResolutionMethod IN ('Manual', 'External') THEN r.ResolvedDate END AS ResolvedDate,
    fse.MembershipExpirationDate,
    fse.SnapshotDate
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_safe ds ON ds.SafeKey = fse.SafeKey
LEFT JOIN dbo.dim_user u ON u.UserKey = fse.UserKey
LEFT JOIN dbo.dim_group g ON g.GroupKey = fse.GroupKey
LEFT JOIN web.entitlement_member_resolution r
    ON r.SourceSystemKey = ds.SourceSystemKey AND r.MemberType = fse.MemberType AND r.SourceMemberId = fse.UnresolvedMemberId
LEFT JOIN web.app_user au ON au.UserKey = r.ResolvedBy
WHERE fse.UnresolvedMemberId IS NOT NULL;
GO
