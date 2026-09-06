/* ============================================================================
   03_BlueTrack_ETL_FactLoads.sql

   Split 2026-09-05 out of the former 02_BlueTrack_ETL_LoadProcedures.sql
   (see Database/README.md for the full script-numbering rationale). This
   file holds only the fact-table loaders -- the dimension-table loaders
   (plus usp_Load_GroupMembership) moved to
   02_BlueTrack_ETL_DimensionLoads.sql and the reporting views moved to
   04_BlueTrack_ETL_ReportingViews.sql. Object names, bodies, and logic are
   byte-for-byte unchanged from the original file; only the file layout
   changed.

   Depends on: 01_BlueTrack_CoreSchema.sql (all dim_, stg_, and fact_ tables
   referenced below) and 02_BlueTrack_ETL_DimensionLoads.sql (these
   procedures assume the dimension tables have already been populated by
   usp_RunFullLoad's call order -- see 06_BlueTrack_PowerBI_Support.sql).

   usp_Load_FactSafeEntitlement updated 2026-09-05, found on the first
   real load against this tenant's actual Privilege Cloud entitlements
   export: a third INSERT pass now captures entitlement rows granted to a
   CyberArk Identity/Entra-federated user or built-in cloud role -- a
   GUID or role name that never appears in stg_pc_users/stg_pc_groups at
   all, so dim_user/dim_group can never resolve it. These rows are no
   longer excluded from the load; they land with UserKey/GroupKey both
   NULL and the raw identifier in fact_safe_entitlement.UnresolvedMemberId
   instead (see that column's own comment in 01_BlueTrack_CoreSchema.sql).
   ============================================================================ */

USE $DatabaseName$;
GO

/* ============================================================================
   6. fact_account  (both sources)

   Privilege Cloud: straight copy from stg_pc_accounts.
   Self-Hosted: CAFiles filtered to CAFType = 2 ('Password', per
   dim_selfhosted_code type 12) joined to the pivoted CAObjectProperties.

   *** PIVOT PROPERTY NAMES BELOW ARE PLACEHOLDERS ('UserName','Address',
   'PolicyID') *** -- confirm against your actual data first:
       SELECT DISTINCT CAOPObjectPropertyName FROM stg_sh_objectproperties;
   and edit the MAX(CASE WHEN ...) lines to match what you find.

   SourceAccountId for Self-Hosted is built as CAFSafeID + '_' + CAFFileID,
   confirmed to match the same pattern as Privilege Cloud's AccountID
   (e.g. '15_5'). This means an account that migrated from Self-Hosted to
   Privilege Cloud without changing its underlying Safe/File ID will produce
   the SAME SourceAccountId string in both stg_pc_accounts and the
   self-hosted pivot below -- they still land as two separate fact_account
   rows (one per SourceSystemKey) by design, since fact_account's natural
   key is (SourceSystemKey, SourceAccountId). If you want a single unified
   "this real-world account" record spanning its Self-Hosted history and its
   Privilege Cloud present, add a reconciliation table that links the two
   AccountKeys on a SourceAccountId match -- don't collapse them into one
   fact_account row, or you lose the ability to see which source is current.
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_FactAccount
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    -- ---- Privilege Cloud ----
    MERGE fact_account AS tgt
    USING (
        SELECT
            @PrivCloudKey AS SourceSystemKey,
            a.AccountID AS SourceAccountId, a.AccountName, a.Address, a.UserName,
            plat.PlatformKey, sf.SafeKey,
            a.SecretType, a.AutoManaged, a.CPMStatus, a.ManualReason,
            a.LastCPMModified AS LastCPMModifiedDate, a.LastReconciled AS LastReconciledDate,
            a.LastVerified AS LastVerifiedDate, a.RemoteAccessRestricted,
            ISNULL(a.Deleted, 0) AS IsDeleted, a.Created AS CreatedDate, a.Platform_LogonDomain AS PlatformLogonDomain
        FROM stg_pc_accounts a
        LEFT JOIN dim_platform plat ON plat.SourceSystemKey = @PrivCloudKey AND plat.PlatformID = a.PlatformID
        LEFT JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeName = a.SafeName
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceAccountId = src.SourceAccountId
    WHEN MATCHED THEN
        UPDATE SET AccountName = src.AccountName, Address = src.Address, UserName = src.UserName,
                   PlatformKey = src.PlatformKey, SafeKey = src.SafeKey, SecretType = src.SecretType,
                   AutoManaged = src.AutoManaged, CPMStatus = src.CPMStatus, ManualReason = src.ManualReason,
                   LastCPMModifiedDate = src.LastCPMModifiedDate, LastReconciledDate = src.LastReconciledDate,
                   LastVerifiedDate = src.LastVerifiedDate, RemoteAccessRestricted = src.RemoteAccessRestricted,
                   IsDeleted = src.IsDeleted, PlatformLogonDomain = src.PlatformLogonDomain
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, SafeKey,
                SecretType, AutoManaged, CPMStatus, ManualReason, LastCPMModifiedDate, LastReconciledDate,
                LastVerifiedDate, RemoteAccessRestricted, IsDeleted, CreatedDate, PlatformLogonDomain)
        VALUES (src.SourceSystemKey, src.SourceAccountId, src.AccountName, src.Address, src.UserName,
                src.PlatformKey, src.SafeKey, src.SecretType, src.AutoManaged, src.CPMStatus, src.ManualReason,
                src.LastCPMModifiedDate, src.LastReconciledDate, src.LastVerifiedDate, src.RemoteAccessRestricted,
                src.IsDeleted, src.CreatedDate, src.PlatformLogonDomain)
    -- an account present in fact_account for this source but absent from the
    -- latest staging snapshot means it no longer exists in the vault -- flag it
    WHEN NOT MATCHED BY SOURCE AND tgt.SourceSystemKey = @PrivCloudKey THEN
        UPDATE SET IsDeleted = 1;

    -- ---- Self-Hosted ----
    ;WITH PivotedAccounts AS (
        SELECT
            f.CAFSafeID, f.CAFFileID, f.CAFSafeName,
            CAST(f.CAFSafeID AS NVARCHAR(50)) + '_' + CAST(f.CAFFileID AS NVARCHAR(50)) AS SourceAccountId,
            f.CAFFileName AS AccountName,
            f.CAFCreationDate AS CreatedDate,
            f.CAFModificationDate AS LastCPMModifiedDate,
            f.CAFLastUsedDate AS LastVerifiedDate,
            -- PLACEHOLDER property names -- confirm against stg_sh_objectproperties before trusting
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'UserName' THEN op.CAOPObjectPropertyValue END) AS UserName,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'Address'  THEN op.CAOPObjectPropertyValue END) AS Address,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'PolicyID' THEN op.CAOPObjectPropertyValue END) AS PolicyID
        FROM stg_sh_files f
        LEFT JOIN stg_sh_objectproperties op
               ON op.CAOPFileId = f.CAFFileID AND op.CAOPSafeId = f.CAFSafeID
        WHERE f.CAFType = 2   -- 'Password' per dim_selfhosted_code (CodeType 12, CodeValue 2)
        GROUP BY f.CAFSafeID, f.CAFFileID, f.CAFSafeName, f.CAFFileName,
                 f.CAFCreationDate, f.CAFModificationDate, f.CAFLastUsedDate
    )
    MERGE fact_account AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey, p.SourceAccountId, p.AccountName, p.Address, p.UserName,
               plat.PlatformKey, sf.SafeKey, p.CreatedDate, p.LastCPMModifiedDate, p.LastVerifiedDate
        FROM PivotedAccounts p
        LEFT JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(p.CAFSafeID AS NVARCHAR(300))
        LEFT JOIN dim_platform plat ON plat.SourceSystemKey = @SelfHostedKey AND plat.PlatformID = p.PolicyID  -- will stay NULL until a Self-Hosted platform reference exists (see schema notes)
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceAccountId = src.SourceAccountId
    WHEN MATCHED THEN
        UPDATE SET AccountName = src.AccountName, Address = src.Address, UserName = src.UserName,
                   PlatformKey = src.PlatformKey, SafeKey = src.SafeKey,
                   LastCPMModifiedDate = src.LastCPMModifiedDate, LastVerifiedDate = src.LastVerifiedDate,
                   IsDeleted = 0
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, SafeKey,
                CreatedDate, LastCPMModifiedDate, LastVerifiedDate, IsDeleted)
        VALUES (src.SourceSystemKey, src.SourceAccountId, src.AccountName, src.Address, src.UserName,
                src.PlatformKey, src.SafeKey, src.CreatedDate, src.LastCPMModifiedDate, src.LastVerifiedDate, 0)
    WHEN NOT MATCHED BY SOURCE AND tgt.SourceSystemKey = @SelfHostedKey THEN
        UPDATE SET IsDeleted = 1;
END
GO
/* ============================================================================
   6b. fact_account_progress  (both sources) -- creates the Stage 1
   ("Discovered") tracking row for any account that doesn't have one yet.

   Never touches an existing progress row -- an account already being
   tracked keeps whatever stage/status/owner/etc. a person has since set,
   even if this proc runs again. This only handles brand-new accounts.

   AccountTypeKey and SORKey are pre-filled from platform_account_type_map
   as a suggested default based on the account's Platform, saving manual
   entry for the common case -- but both remain ordinary editable columns
   on fact_account_progress, so an analyst can override either one for an
   individual account that doesn't fit its Platform's usual pattern.
   Accounts on a Platform that hasn't been mapped yet in
   platform_account_type_map simply get NULL for both and are picked up
   once that mapping is completed (see vw_review_platform_sor_accounttype
   below to find unmapped platforms).
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_FactAccountProgress
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered');
    DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');

    INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey, AccountTypeKey, SORKey)
    SELECT fa.AccountKey, @DiscoveredStageKey, @NotStartedStatusKey, pm.AccountTypeKey, pm.SORKey
    FROM fact_account fa
    LEFT JOIN platform_account_type_map pm ON pm.PlatformKey = fa.PlatformKey
    WHERE fa.IsDeleted = 0
      AND NOT EXISTS (SELECT 1 FROM fact_account_progress fap WHERE fap.AccountKey = fa.AccountKey);
END
GO
/* ============================================================================
   6b. Auto-advance to "Onboarded to Vault" (Stage 3)

   Business rule, direct from the user (2026-09-04): any account found in
   either source's data is considered onboarded to the vault unless it
   sits in a "pending accounts" safe -- matched by safe name containing
   "_Pending" (e.g. PasswordManager_Pending), not a single fixed name,
   since more than one such safe can exist. An account with no safe at
   all (SafeKey NULL) is NOT excluded by this rule (it isn't "in" a
   Pending safe), so it advances too.

   SAFEGUARD -- only ever touches a row still at its untouched default
   (CurrentStageKey = Discovered AND CurrentStatusKey = Not Started).
   Deliberately re-evaluated on every Load run (not just for brand-new
   accounts, unlike usp_Load_FactAccountProgress above) so the 2,432
   accounts already stuck at Discovered before this procedure existed get
   picked up retroactively -- but the same untouched-default check means
   it will never move an account a person has already started curating
   (changed its status, stage, owner, notes, etc.), which would otherwise
   silently fight Design_Data_Editing_Behavior.md's regression-requires-
   a-reason rule the next time this runs.

   NOT written to web.audit_event -- that table's PerformedByUserKey is
   NOT NULL, FK'd to web.app_user (a real logged-in person), and this
   runs from the ETL/Load pipeline with no such context. The daily
   usp_Load_FactAccountProgressHistory snapshot (run after this in
   usp_RunFullLoad) already gives a de facto record of exactly which day
   an account's StageKey changed, which is enough for a first cut; a
   dedicated system/service app_user row for ETL-originated audit events
   is a bigger decision left for later if it's actually needed.

   Stage 4 ("Managed / Rotation Enabled") is deliberately NOT handled
   here yet: it needs the safe to have a CPM assigned (dim_safe.ManagingCPM
   -- present in the schema and the source export, but empty for every
   safe in this test tenant's actual data) AND platform-level automatic-
   management/rotation settings, which the current Platforms export
   doesn't carry at all (only PlatformID/Name/Description/Active/
   PlatformType). Confirmed directly with the user (2026-09-04) to hold
   off on Stage 4 until a fuller Platform export is available, rather
   than build it against half the real rule.
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_AccountProgressAutoAdvance
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered');
    DECLARE @OnboardedStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Onboarded to Vault');
    DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');

    UPDATE fap
    SET fap.CurrentStageKey = @OnboardedStageKey,
        fap.LastUpdated = SYSUTCDATETIME()
    FROM fact_account_progress fap
    JOIN fact_account fa ON fa.AccountKey = fap.AccountKey
    LEFT JOIN dim_safe ds ON ds.SafeKey = fa.SafeKey
    WHERE fap.CurrentStageKey = @DiscoveredStageKey
      AND fap.CurrentStatusKey = @NotStartedStatusKey
      AND fa.IsDeleted = 0
      AND (ds.SafeKey IS NULL OR ds.SafeName NOT LIKE '%[_]Pending%');
END
GO
/* ============================================================================
   7. fact_safe_entitlement + bridge_entitlement_permission  (both sources)

   Uses CROSS APPLY (VALUES ...) to unpivot each source's wide boolean
   columns into the long/normalized bridge table, mapping raw column names
   to canonical permissions via permission_alias.

   NOTE ON APPROACH: earlier revisions of this proc tried to capture each
   newly-inserted EntitlementKey via OUTPUT ... INTO a table variable
   alongside staging columns like SafeUrlId/MemberId. That doesn't work --
   OUTPUT on an INSERT can only reference inserted./deleted. columns (the
   target table's own columns), never columns from the source SELECT, so
   SafeUrlId/MemberId/CAOSafeID/CAOOwnerID (which aren't columns on
   fact_safe_entitlement at all) can never appear there. Instead, each
   INSERT below is followed by a second statement that re-joins the same
   staging rows back to fact_safe_entitlement on the natural key
   (SafeKey, MemberType, UserKey, GroupKey, SnapshotDate) to find the
   EntitlementKey each row landed at. This assumes that natural key is
   unique per snapshot, which holds as long as the staging export itself
   has no duplicate (Safe, Member) rows -- true given the DESIGN
   ASSUMPTION at the top of this file that staging holds one clean current
   snapshot; a staging table with duplicate rows would fan out here.
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_FactSafeEntitlement
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);

    -- clear and reload each run -- entitlement exports are point-in-time
    -- snapshots, so this table is treated as "current state" rather than
    -- something to incrementally MERGE row-by-row like the dimensions above
    DELETE bep FROM bridge_entitlement_permission bep
        JOIN fact_safe_entitlement fse ON fse.EntitlementKey = bep.EntitlementKey
        JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey
        WHERE sf.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey);
    DELETE fse FROM fact_safe_entitlement fse
        JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey
        WHERE sf.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey);

    -- ---- Privilege Cloud entitlements: resolved (matches dim_user/dim_group) ----
    INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, GroupKey, MembershipExpirationDate,
                                        IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
    SELECT sf.SafeKey, e.MemberType, u.UserKey, g.GroupKey,
           e.MembershipExpirationDate, e.IsExpiredMembershipEnable, e.IsPredefinedUser, @Today
    FROM stg_pc_entitlements e
    JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
    LEFT JOIN dim_user  u ON e.MemberType = 'User'  AND u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId  = e.MemberId
    LEFT JOIN dim_group g ON e.MemberType = 'Group' AND g.SourceSystemKey = @PrivCloudKey AND g.SourceGroupId = e.MemberId
    WHERE (e.MemberType = 'User'  AND u.UserKey  IS NOT NULL)
       OR (e.MemberType = 'Group' AND g.GroupKey IS NOT NULL);

    INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
    SELECT fse.EntitlementKey, pa.PermissionKey, v.IsGranted
    FROM stg_pc_entitlements e
    JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
    LEFT JOIN dim_user  u ON e.MemberType = 'User'  AND u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId  = e.MemberId
    LEFT JOIN dim_group g ON e.MemberType = 'Group' AND g.SourceSystemKey = @PrivCloudKey AND g.SourceGroupId = e.MemberId
    JOIN fact_safe_entitlement fse
        ON fse.SafeKey = sf.SafeKey
       AND fse.MemberType = e.MemberType
       AND ISNULL(fse.UserKey, -1)  = ISNULL(u.UserKey, -1)
       AND ISNULL(fse.GroupKey, -1) = ISNULL(g.GroupKey, -1)
       AND fse.SnapshotDate = @Today
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
    WHERE v.IsGranted IS NOT NULL
      AND ((e.MemberType = 'User'  AND u.UserKey  IS NOT NULL)
        OR (e.MemberType = 'Group' AND g.GroupKey IS NOT NULL));

    -- ---- Privilege Cloud entitlements: unresolved (CyberArk Identity/
    -- Entra-federated user or built-in cloud role -- see UnresolvedMemberId's
    -- own comment in 01_BlueTrack_CoreSchema.sql). Preserved rather than
    -- dropped. The bridge rejoin below keys on UnresolvedMemberId, not
    -- UserKey/GroupKey (both always NULL here) -- more than one unresolved
    -- member of the same MemberType can exist on the same safe, so
    -- UserKey/GroupKey alone would not be a unique rejoin key the way it
    -- is for the resolved case above.
    INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UnresolvedMemberId, MembershipExpirationDate,
                                        IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
    SELECT sf.SafeKey, e.MemberType, e.MemberId,
           e.MembershipExpirationDate, e.IsExpiredMembershipEnable, e.IsPredefinedUser, @Today
    FROM stg_pc_entitlements e
    JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
    LEFT JOIN dim_user  u ON e.MemberType = 'User'  AND u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId  = e.MemberId
    LEFT JOIN dim_group g ON e.MemberType = 'Group' AND g.SourceSystemKey = @PrivCloudKey AND g.SourceGroupId = e.MemberId
    WHERE (e.MemberType = 'User'  AND u.UserKey  IS NULL)
       OR (e.MemberType = 'Group' AND g.GroupKey IS NULL);

    INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
    SELECT fse.EntitlementKey, pa.PermissionKey, v.IsGranted
    FROM stg_pc_entitlements e
    JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
    LEFT JOIN dim_user  u ON e.MemberType = 'User'  AND u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId  = e.MemberId
    LEFT JOIN dim_group g ON e.MemberType = 'Group' AND g.SourceSystemKey = @PrivCloudKey AND g.SourceGroupId = e.MemberId
    JOIN fact_safe_entitlement fse
        ON fse.SafeKey = sf.SafeKey
       AND fse.MemberType = e.MemberType
       AND fse.UnresolvedMemberId = e.MemberId
       AND fse.SnapshotDate = @Today
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
    WHERE v.IsGranted IS NOT NULL
      AND ((e.MemberType = 'User'  AND u.UserKey  IS NULL)
        OR (e.MemberType = 'Group' AND g.GroupKey IS NULL));

    -- ---- Self-Hosted entitlements (CAOwners) ----
    -- CAOOwnerType decodes via dim_selfhosted_code type 10: 0=User, 1=Group, 2=Gateway account.
    -- Gateway-account owners aren't represented in dim_user or dim_group -- they're
    -- excluded below via the WHERE clause (rather than relying on the CHECK
    -- constraint to reject them). Revisit this if Gateway account owners
    -- matter for your reporting.
    INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, GroupKey, MembershipExpirationDate,
                                        IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
    SELECT sf.SafeKey,
           CASE o.CAOOwnerType WHEN 0 THEN 'User' WHEN 1 THEN 'Group' ELSE NULL END,
           u.UserKey, g.GroupKey, o.CAOExpirationDate, NULL, NULL, @Today
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

PRINT '03_BlueTrack_ETL_FactLoads.sql complete.';
GO
