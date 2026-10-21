/* ============================================================================
   48_BlueTrack_SelfHostedDirectExtras.sql

   RUN THIS AFTER 01-47. Guarded -- safe to re-run.

   More from Self-Hosted Direct (Claude_Docs/Design_CyberArk-Direct-Integration.md):

   - D-280: the account properties REST doesn't return, read by the pull
     from PVWA's classic account search: DeviceType, CreationMethod and the
     linked logon and reconcile accounts. One call per account, so the pull
     keeps what it read in dbo.cyberark_direct_account_detail and reads an
     account again only when its CategoryModificationTime changes. Staged in
     stg_shd_account_details; the Load fills new fact_account columns
     (usp_Load_FactAccountClassicDetail), and vw_powerbi_accounts gives them.
   - D-282: CyberArk Applications (AAM application IDs) and their
     authentication methods, from the classic Applications calls, into
     dim_cyberark_application(_authentication); vw_cyberark_application_
     account_access says which can retrieve each account.
   - D-281: the people behind a group that is a Safe member. Self-Hosted
     Vault groups' member lists now load into bridge_group_membership, and
     the pull reads each Safe-member group's users from AD (a Self-Hosted
     directory group by its DN, a Privilege Cloud group by name) into
     ad_group_member; vw_safe_access_people lists who can reach each Safe.

   Privilege Cloud's own loads are unchanged; its groups get AD members only
   on a site whose Self-Hosted Direct pull runs.
   ============================================================================ */

USE $DatabaseName$;
GO

/* ---- 1. D-280: the classic account details --------------------------------- */

/* What earlier pulls read, by REST account id (SafeNumber_FileId). Not staging: a pull never empties it; it adds or
   replaces the accounts it read (even when the pull is stopped) and forgets accounts PVWA no longer returns. */
IF OBJECT_ID('dbo.cyberark_direct_account_detail') IS NULL
BEGIN
    CREATE TABLE dbo.cyberark_direct_account_detail (
        AccountId                NVARCHAR(100)  NOT NULL CONSTRAINT PK_cyberark_direct_account_detail PRIMARY KEY,
        CategoryModificationTime DATETIME2      NULL,       -- REST's time when it was read; a different one means read it again
        DeviceType               NVARCHAR(100)  NULL,
        CreationMethod           NVARCHAR(100)  NULL,
        LogonSafe                NVARCHAR(300)  NULL,       -- ExtraPass1*: the linked logon account
        LogonFolder              NVARCHAR(300)  NULL,
        LogonName                NVARCHAR(512)  NULL,
        ReconcileSafe            NVARCHAR(300)  NULL,       -- ExtraPass3*: the linked reconcile account
        ReconcileFolder          NVARCHAR(300)  NULL,
        ReconcileName            NVARCHAR(512)  NULL,
        ReadAt                   DATETIME2      NOT NULL CONSTRAINT DF_cyberark_direct_account_detail_ReadAt DEFAULT SYSUTCDATETIME()
    );
END
GO

/* Every account's details the pull knows, read tonight (ReadThisPull = 1) or kept from an earlier pull. */
IF OBJECT_ID('dbo.stg_shd_account_details') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_account_details (
        LoadId                   BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId            UNIQUEIDENTIFIER NOT NULL,
        SourceFileName           NVARCHAR(260)    NOT NULL,
        LoadTimestamp            DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        AccountId                NVARCHAR(100)    NOT NULL,
        CategoryModificationTime DATETIME2        NULL,
        DeviceType               NVARCHAR(100)    NULL,
        CreationMethod           NVARCHAR(100)    NULL,
        LogonSafe                NVARCHAR(300)    NULL,
        LogonFolder              NVARCHAR(300)    NULL,
        LogonName                NVARCHAR(512)    NULL,
        ReconcileSafe            NVARCHAR(300)    NULL,
        ReconcileFolder          NVARCHAR(300)    NULL,
        ReconcileName            NVARCHAR(512)    NULL,
        ReadThisPull             BIT              NOT NULL
    );
END
GO

IF COL_LENGTH('dbo.fact_account', 'DeviceType') IS NULL
    ALTER TABLE dbo.fact_account ADD DeviceType NVARCHAR(100) NULL;               -- e.g. Operating System, Database
GO
IF COL_LENGTH('dbo.fact_account', 'CreationMethod') IS NULL
    ALTER TABLE dbo.fact_account ADD CreationMethod NVARCHAR(100) NULL;           -- e.g. PVWA, AutoDetected
GO
IF COL_LENGTH('dbo.fact_account', 'LinkedLogonAccount') IS NULL
    ALTER TABLE dbo.fact_account ADD LinkedLogonAccount NVARCHAR(1200) NULL,      -- Safe\Name, or Safe\Folder\Name outside Root
                                     LinkedLogonAccountKey INT NULL;              -- that account in BlueTrack, when found
GO
IF COL_LENGTH('dbo.fact_account', 'LinkedReconcileAccount') IS NULL
    ALTER TABLE dbo.fact_account ADD LinkedReconcileAccount NVARCHAR(1200) NULL,
                                     LinkedReconcileAccountKey INT NULL;
GO

/* With Direct staging, each account's classic details from stg_shd_account_details. An account the pull has no
   details for (not read yet) keeps what it has. A linked account is found by Safe name and account name among the
   Self-Hosted accounts, one not deleted first. Runs after usp_Load_FactAccount, so tonight's accounts are there. */
CREATE OR ALTER PROCEDURE dbo.usp_Load_FactAccountClassicDetail
AS
BEGIN
    SET NOCOUNT ON;
    IF dbo.fn_SelfHostedStagedByDirect() = 0 RETURN;
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    UPDATE fa
    SET DeviceType = NULLIF(LTRIM(RTRIM(d.DeviceType)), ''),
        CreationMethod = NULLIF(LTRIM(RTRIM(d.CreationMethod)), ''),
        LinkedLogonAccount = CASE WHEN d.LogonName IS NOT NULL THEN
            CONCAT(d.LogonSafe, N'\', CASE WHEN ISNULL(d.LogonFolder, N'Root') = N'Root' THEN N'' ELSE d.LogonFolder + N'\' END, d.LogonName) END,
        LinkedLogonAccountKey = lg.AccountKey,
        LinkedReconcileAccount = CASE WHEN d.ReconcileName IS NOT NULL THEN
            CONCAT(d.ReconcileSafe, N'\', CASE WHEN ISNULL(d.ReconcileFolder, N'Root') = N'Root' THEN N'' ELSE d.ReconcileFolder + N'\' END, d.ReconcileName) END,
        LinkedReconcileAccountKey = rc.AccountKey
    FROM dbo.fact_account fa
    JOIN dbo.stg_shd_account_details d ON fa.SourceSystemKey = @SelfHostedKey AND fa.SourceAccountId = d.AccountId
    OUTER APPLY (SELECT TOP (1) la.AccountKey FROM dbo.fact_account la JOIN dbo.dim_safe s ON s.SafeKey = la.SafeKey
                 WHERE d.LogonName IS NOT NULL AND la.SourceSystemKey = @SelfHostedKey AND s.SafeName = d.LogonSafe AND la.AccountName = d.LogonName
                 ORDER BY la.IsDeleted, la.AccountKey) lg
    OUTER APPLY (SELECT TOP (1) ra.AccountKey FROM dbo.fact_account ra JOIN dbo.dim_safe s ON s.SafeKey = ra.SafeKey
                 WHERE d.ReconcileName IS NOT NULL AND ra.SourceSystemKey = @SelfHostedKey AND s.SafeName = d.ReconcileSafe AND ra.AccountName = d.ReconcileName
                 ORDER BY ra.IsDeleted, ra.AccountKey) rc;
END
GO

/* As in 46_BlueTrack_PowerBiCpmActionColumns.sql, plus (D-280) the four classic-detail columns at the end. */
CREATE OR ALTER VIEW vw_powerbi_accounts AS
SELECT
    fa.AccountKey,
    fa.SourceSystemKey,
    fa.SourceAccountId,
    fa.AccountName,
    fa.Address,
    fa.UserName,
    fa.PlatformKey,
    fa.SafeKey,
    fa.SecretType,
    fa.AutoManaged,
    fa.CPMStatus,
    fa.LastCPMModifiedDate,
    fa.LastReconciledDate,
    fa.LastVerifiedDate,
    fa.IsDeleted,
    fa.CreatedDate,
    fap.ProgressKey,
    fap.CurrentStageKey,
    fap.CurrentStatusKey,
    fap.AccountTypeKey,
    fap.SORKey,
    fap.RiskLevelKey,
    fap.OwnerName,
    fap.BusinessUnit,
    fap.TargetRemediationDate,
    fap.ActualCompletionDate,
    fap.LastUpdated AS ProgressLastUpdated,
    -- surfaces a plain flag for a common Power BI card/KPI without needing a DAX measure
    CASE WHEN fap.ProgressKey IS NULL THEN 1 ELSE 0 END AS IsUntrackedInBlueprint,
    -- surfaces overdue remediations directly for a simple visual filter
    CASE WHEN fap.TargetRemediationDate < CAST(GETDATE() AS DATE)
              AND fap.ActualCompletionDate IS NULL
         THEN 1 ELSE 0 END AS IsOverdue,
    -- D-277: Self-Hosted Direct only (NULL otherwise); times are UTC.
    fa.CreatedTime,
    fa.LastCPMAction,
    fa.LastCPMActionResult,
    fa.LastCPMActionTime,
    -- D-280: Self-Hosted Direct only.
    fa.DeviceType,
    fa.CreationMethod,
    fa.LinkedLogonAccount,
    fa.LinkedReconcileAccount
FROM fact_account fa
LEFT JOIN fact_account_progress fap ON fap.AccountKey = fa.AccountKey
WHERE NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey);
GO

/* ---- 2. D-282: CyberArk Applications (AAM application IDs) ------------------ */

IF OBJECT_ID('dbo.stg_shd_applications') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_applications (
        LoadId                                  BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId                           UNIQUEIDENTIFIER NOT NULL,
        SourceFileName                          NVARCHAR(260)    NOT NULL,
        LoadTimestamp                           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        AppId                                   NVARCHAR(256)    NOT NULL,
        Description                             NVARCHAR(1000)   NULL,
        Location                                NVARCHAR(400)    NULL,
        Disabled                                BIT              NULL,
        ExpirationDate                          NVARCHAR(100)    NULL,   -- as PVWA sends it
        AccessPermittedFrom                     INT              NULL,   -- hour of the day, 0-24
        AccessPermittedTo                       INT              NULL,
        AllowExtendedAuthenticationRestrictions BIT              NULL,
        BusinessOwnerFirstName                  NVARCHAR(200)    NULL,
        BusinessOwnerLastName                   NVARCHAR(200)    NULL,
        BusinessOwnerEmail                      NVARCHAR(320)    NULL,
        BusinessOwnerPhone                      NVARCHAR(100)    NULL,
        AuthenticationsRead                     BIT              NOT NULL  -- 0: its methods weren't read; the Load keeps the ones it has
    );
END
GO

/* AuthValue only for machineAddress, osUser, path and certificateSerialNumber; NULL for a hash and any other type. */
IF OBJECT_ID('dbo.stg_shd_application_authentications') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_application_authentications (
        LoadId                 BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId          UNIQUEIDENTIFIER NOT NULL,
        SourceFileName         NVARCHAR(260)    NOT NULL,
        LoadTimestamp          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        AppId                  NVARCHAR(256)    NOT NULL,
        AuthId                 NVARCHAR(100)    NULL,
        AuthType               NVARCHAR(100)    NOT NULL,
        AuthValue              NVARCHAR(1000)   NULL,
        Comment                NVARCHAR(1000)   NULL,
        IsFolder               BIT              NULL,
        AllowInternalScripts   BIT              NULL,
        Subject                NVARCHAR(1000)   NULL,   -- certificate attribute methods
        Issuer                 NVARCHAR(1000)   NULL,
        SubjectAlternativeName NVARCHAR(1000)   NULL
    );
END
GO

/* "CyberArk Application" throughout: web.dim_application is BlueTrack's own business applications (D-238). */
IF OBJECT_ID('dbo.dim_cyberark_application') IS NULL
BEGIN
    CREATE TABLE dbo.dim_cyberark_application (
        CyberArkApplicationKey                  INT IDENTITY(1,1) CONSTRAINT PK_dim_cyberark_application PRIMARY KEY,
        SourceSystemKey                         INT            NOT NULL REFERENCES dbo.dim_source_system(SourceSystemKey),
        AppId                                   NVARCHAR(256)  NOT NULL,
        Description                             NVARCHAR(1000) NULL,
        Location                                NVARCHAR(400)  NULL,
        Disabled                                BIT            NULL,
        ExpirationDate                          NVARCHAR(100)  NULL,
        AccessPermittedFrom                     INT            NULL,
        AccessPermittedTo                       INT            NULL,
        AllowExtendedAuthenticationRestrictions BIT            NULL,
        BusinessOwnerFirstName                  NVARCHAR(200)  NULL,
        BusinessOwnerLastName                   NVARCHAR(200)  NULL,
        BusinessOwnerEmail                      NVARCHAR(320)  NULL,
        BusinessOwnerPhone                      NVARCHAR(100)  NULL,
        IsDeletedInSource                       BIT            NOT NULL CONSTRAINT DF_dim_cyberark_application_IsDeletedInSource DEFAULT 0,
        FirstLoadedAt                           DATETIME2      NOT NULL CONSTRAINT DF_dim_cyberark_application_FirstLoadedAt DEFAULT SYSUTCDATETIME(),
        LastLoadedAt                            DATETIME2      NOT NULL CONSTRAINT DF_dim_cyberark_application_LastLoadedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_dim_cyberark_application UNIQUE (SourceSystemKey, AppId)
    );
END
GO

IF OBJECT_ID('dbo.dim_cyberark_application_authentication') IS NULL
BEGIN
    CREATE TABLE dbo.dim_cyberark_application_authentication (
        CyberArkApplicationAuthenticationKey INT IDENTITY(1,1) CONSTRAINT PK_dim_cyberark_application_authentication PRIMARY KEY,
        CyberArkApplicationKey               INT            NOT NULL REFERENCES dbo.dim_cyberark_application(CyberArkApplicationKey),
        AuthId                               NVARCHAR(100)  NULL,
        AuthType                             NVARCHAR(100)  NOT NULL,
        AuthValue                            NVARCHAR(1000) NULL,
        Comment                              NVARCHAR(1000) NULL,
        IsFolder                             BIT            NULL,
        AllowInternalScripts                 BIT            NULL,
        Subject                              NVARCHAR(1000) NULL,
        Issuer                               NVARCHAR(1000) NULL,
        SubjectAlternativeName               NVARCHAR(1000) NULL
    );
    CREATE INDEX IX_dim_cyberark_application_authentication_App ON dbo.dim_cyberark_application_authentication (CyberArkApplicationKey);
END
GO

/* With Direct staging that has applications: add or update each, flag the ones PVWA no longer has (kept, as accounts
   are), and replace the authentication methods of each application whose methods were read. Nothing staged (the list
   was refused, or not read) leaves everything as it was. */
CREATE OR ALTER PROCEDURE dbo.usp_Load_DimCyberArkApplication
AS
BEGIN
    SET NOCOUNT ON;
    IF dbo.fn_SelfHostedStagedByDirect() = 0 OR NOT EXISTS (SELECT 1 FROM dbo.stg_shd_applications) RETURN;
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    MERGE dbo.dim_cyberark_application AS tgt
    USING (SELECT * FROM dbo.stg_shd_applications) AS src
        ON tgt.SourceSystemKey = @SelfHostedKey AND tgt.AppId = src.AppId
    WHEN MATCHED THEN
        UPDATE SET Description = src.Description, Location = src.Location, Disabled = src.Disabled, ExpirationDate = src.ExpirationDate,
                   AccessPermittedFrom = src.AccessPermittedFrom, AccessPermittedTo = src.AccessPermittedTo,
                   AllowExtendedAuthenticationRestrictions = src.AllowExtendedAuthenticationRestrictions,
                   BusinessOwnerFirstName = src.BusinessOwnerFirstName, BusinessOwnerLastName = src.BusinessOwnerLastName,
                   BusinessOwnerEmail = src.BusinessOwnerEmail, BusinessOwnerPhone = src.BusinessOwnerPhone,
                   IsDeletedInSource = 0, LastLoadedAt = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, AppId, Description, Location, Disabled, ExpirationDate, AccessPermittedFrom, AccessPermittedTo,
                AllowExtendedAuthenticationRestrictions, BusinessOwnerFirstName, BusinessOwnerLastName, BusinessOwnerEmail, BusinessOwnerPhone)
        VALUES (@SelfHostedKey, src.AppId, src.Description, src.Location, src.Disabled, src.ExpirationDate, src.AccessPermittedFrom, src.AccessPermittedTo,
                src.AllowExtendedAuthenticationRestrictions, src.BusinessOwnerFirstName, src.BusinessOwnerLastName, src.BusinessOwnerEmail, src.BusinessOwnerPhone)
    WHEN NOT MATCHED BY SOURCE AND tgt.SourceSystemKey = @SelfHostedKey THEN
        UPDATE SET IsDeletedInSource = 1;

    DELETE x FROM dbo.dim_cyberark_application_authentication x
    JOIN dbo.dim_cyberark_application a ON a.CyberArkApplicationKey = x.CyberArkApplicationKey AND a.SourceSystemKey = @SelfHostedKey
    WHERE EXISTS (SELECT 1 FROM dbo.stg_shd_applications s WHERE s.AppId = a.AppId AND s.AuthenticationsRead = 1);

    INSERT INTO dbo.dim_cyberark_application_authentication (CyberArkApplicationKey, AuthId, AuthType, AuthValue, Comment, IsFolder,
        AllowInternalScripts, Subject, Issuer, SubjectAlternativeName)
    SELECT a.CyberArkApplicationKey, x.AuthId, x.AuthType, x.AuthValue, x.Comment, x.IsFolder, x.AllowInternalScripts, x.Subject, x.Issuer, x.SubjectAlternativeName
    FROM dbo.stg_shd_application_authentications x
    JOIN dbo.stg_shd_applications s ON s.AppId = x.AppId AND s.AuthenticationsRead = 1
    JOIN dbo.dim_cyberark_application a ON a.SourceSystemKey = @SelfHostedKey AND a.AppId = x.AppId;
END
GO

/* Which CyberArk Applications can retrieve each account: a Safe member with RetrieveAccounts granted on the account's
   Safe that is the application's own Vault user (user type 35, AIMAccount, named by its AppID), or a Vault group that
   user is in (AccessPath names the group). Applications deleted in CyberArk are left out. */
CREATE OR ALTER VIEW dbo.vw_cyberark_application_account_access AS
SELECT fa.AccountKey, ca.CyberArkApplicationKey, ca.AppId, x.AccessPath, x.EntitlementKey
FROM dbo.fact_account fa
JOIN dbo.fact_safe_entitlement fse ON fse.SafeKey = fa.SafeKey
JOIN dbo.bridge_entitlement_permission bep ON bep.EntitlementKey = fse.EntitlementKey AND bep.IsGranted = 1
JOIN dbo.dim_permission p ON p.PermissionKey = bep.PermissionKey AND p.PermissionName = 'RetrieveAccounts'
CROSS APPLY (
    SELECT u.UserKey, CAST(N'Direct' AS NVARCHAR(300)) AS AccessPath, fse.EntitlementKey
    FROM dbo.dim_user u WHERE fse.MemberType = 'User' AND u.UserKey = fse.UserKey
    UNION ALL
    SELECT bgm.UserKey, CAST(N'Via Vault group: ' + g.GroupName AS NVARCHAR(300)), fse.EntitlementKey
    FROM dbo.dim_group g JOIN dbo.bridge_group_membership bgm ON bgm.GroupKey = g.GroupKey
    WHERE fse.MemberType = 'Group' AND g.GroupKey = fse.GroupKey
) x
JOIN dbo.dim_user au ON au.UserKey = x.UserKey AND au.UserType = '35'
JOIN dbo.dim_cyberark_application ca ON ca.SourceSystemKey = au.SourceSystemKey AND ca.AppId = au.Username AND ca.IsDeletedInSource = 0;
GO

/* ---- 3. D-281: the people behind a group that is a Safe member --------------- */

/* Self-Hosted Vault groups' own member lists (stg_shd_group_members, by id) go into bridge_group_membership as
   Privilege Cloud's do; the baseline left Self-Hosted out. As in 02_BlueTrack_Baseline_EtlLoads.sql, plus that block. */
CREATE OR ALTER PROCEDURE usp_Load_GroupMembership
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);

    DELETE bgm FROM bridge_group_membership bgm
        JOIN dim_group g ON g.GroupKey = bgm.GroupKey
        WHERE g.SourceSystemKey = @PrivCloudKey;

    INSERT INTO bridge_group_membership (GroupKey, UserKey, MemberLevel, SnapshotDate)
    SELECT DISTINCT g.GroupKey, u.UserKey, gm.MemberLevel, @Today
    FROM stg_pc_groupmembers gm
    JOIN dim_group g ON g.SourceSystemKey = @PrivCloudKey AND g.GroupName = gm.RootGroupName
    JOIN dim_user u  ON u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId = gm.MemberID
    WHERE gm.MemberType = 'User';   -- only direct User members are handled; a future export containing Group-type nested members would need group-to-group expansion, not covered here

    -- D-281: Self-Hosted, with Direct staging only (users and groups share one id space; a member that is a group isn't a user).
    IF dbo.fn_SelfHostedStagedByDirect() = 1
    BEGIN
        DELETE bgm FROM bridge_group_membership bgm
            JOIN dim_group g ON g.GroupKey = bgm.GroupKey
            WHERE g.SourceSystemKey = @SelfHostedKey;

        INSERT INTO bridge_group_membership (GroupKey, UserKey, MemberLevel, SnapshotDate)
        SELECT DISTINCT g.GroupKey, u.UserKey, NULL, @Today
        FROM stg_shd_group_members gm
        JOIN dim_group g ON g.SourceSystemKey = @SelfHostedKey AND g.SourceGroupId = CAST(gm.GroupId AS NVARCHAR(100))
        JOIN dim_user u  ON u.SourceSystemKey = @SelfHostedKey AND u.SourceUserId = CAST(gm.MemberId AS NVARCHAR(100));
    END
END
GO

/* What the nightly Direct pull read from AD for each group that is a Safe member: a Self-Hosted directory group by its
   DN, a Privilege Cloud group by its name (best effort: the export has no DN). GroupSource is SELFHOSTED (GroupRef =
   the Vault group id) or PRIVCLOUD (GroupRef = the group name). */
IF OBJECT_ID('dbo.stg_shd_ad_group_reads') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_ad_group_reads (
        LoadId         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName NVARCHAR(260)    NOT NULL,
        LoadTimestamp  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        GroupSource    NVARCHAR(20)     NOT NULL,
        GroupRef       NVARCHAR(256)    NOT NULL,
        GroupName      NVARCHAR(256)    NULL,
        Dn             NVARCHAR(1000)   NULL,
        DomainName     NVARCHAR(256)    NULL,       -- where it was found
        Status         NVARCHAR(20)     NOT NULL,   -- Found, NotFound or Failed
        MemberCount    INT              NULL,
        Message        NVARCHAR(1000)   NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_ad_group_members') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_ad_group_members (
        LoadId            BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId     UNIQUEIDENTIFIER NOT NULL,
        SourceFileName    NVARCHAR(260)    NOT NULL,
        LoadTimestamp     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        GroupSource       NVARCHAR(20)     NOT NULL,
        GroupRef          NVARCHAR(256)    NOT NULL,
        DomainName        NVARCHAR(256)    NOT NULL,
        SamAccountName    NVARCHAR(256)    NOT NULL,
        DistinguishedName NVARCHAR(1000)   NULL,
        ObjectSid         NVARCHAR(200)    NULL,
        DisplayName       NVARCHAR(400)    NULL,
        IsEnabled         BIT              NOT NULL
    );
END
GO

/* The AD users of each group, nested groups flattened (D-116's rule). Replaced per group at each load; a group whose
   read Failed keeps the members it had. */
IF OBJECT_ID('dbo.ad_group_member') IS NULL
BEGIN
    CREATE TABLE dbo.ad_group_member (
        AdGroupMemberKey  BIGINT IDENTITY(1,1) CONSTRAINT PK_ad_group_member PRIMARY KEY,
        GroupKey          INT            NOT NULL REFERENCES dbo.dim_group(GroupKey),
        DomainName        NVARCHAR(256)  NOT NULL,
        SamAccountName    NVARCHAR(256)  NOT NULL,
        DistinguishedName NVARCHAR(1000) NULL,
        ObjectSid         NVARCHAR(200)  NULL,
        DisplayName       NVARCHAR(400)  NULL,
        IsEnabled         BIT            NOT NULL,
        LoadedAt          DATETIME2      NOT NULL CONSTRAINT DF_ad_group_member_LoadedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_ad_group_member UNIQUE (GroupKey, DomainName, SamAccountName)
    );
END
GO

/* How each group's last AD read went; LastFoundAt is when its members were last read. */
IF OBJECT_ID('dbo.ad_group_read') IS NULL
BEGIN
    CREATE TABLE dbo.ad_group_read (
        GroupKey      INT            NOT NULL CONSTRAINT PK_ad_group_read PRIMARY KEY REFERENCES dbo.dim_group(GroupKey),
        MatchedBy     NVARCHAR(10)   NOT NULL,   -- Dn or Name
        Status        NVARCHAR(20)   NOT NULL,
        DomainName    NVARCHAR(256)  NULL,
        MemberCount   INT            NULL,
        Message       NVARCHAR(1000) NULL,
        LastAttemptAt DATETIME2      NOT NULL,
        LastFoundAt   DATETIME2      NULL
    );
END
GO

/* Applies the staged AD reads: replace the members of each group read (Found or NotFound), keep those of a group whose
   read Failed, and drop groups no longer read (not a Safe member any more). No reads staged (no LDAP domain enabled)
   leaves everything as it was. Called by the Load after a Direct pull (usp_Load_AdGroupMembers), and by the API's own
   nightly AD read on a site with Self-Hosted off (D-281: Privilege Cloud groups only). */
CREATE OR ALTER PROCEDURE dbo.usp_Apply_AdGroupMembers
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.stg_shd_ad_group_reads) RETURN;
    DECLARE @Now DATETIME2 = SYSUTCDATETIME();

    SELECT r.Status, r.DomainName, r.MemberCount, r.Message, r.GroupSource, r.GroupRef, g.GroupKey,
           CASE r.GroupSource WHEN 'SELFHOSTED' THEN N'Dn' ELSE N'Name' END AS MatchedBy
    INTO #reads
    FROM dbo.stg_shd_ad_group_reads r
    JOIN dbo.dim_source_system s ON s.SourceSystemCode = r.GroupSource
    JOIN dbo.dim_group g ON g.SourceSystemKey = s.SourceSystemKey
                        AND ((r.GroupSource = 'SELFHOSTED' AND g.SourceGroupId = r.GroupRef) OR (r.GroupSource = 'PRIVCLOUD' AND g.GroupName = r.GroupRef));

    DELETE m FROM dbo.ad_group_member m
    WHERE NOT EXISTS (SELECT 1 FROM #reads r WHERE r.GroupKey = m.GroupKey AND r.Status = 'Failed');

    INSERT INTO dbo.ad_group_member (GroupKey, DomainName, SamAccountName, DistinguishedName, ObjectSid, DisplayName, IsEnabled, LoadedAt)
    SELECT DISTINCT r.GroupKey, m.DomainName, m.SamAccountName, m.DistinguishedName, m.ObjectSid, m.DisplayName, m.IsEnabled, @Now
    FROM dbo.stg_shd_ad_group_members m
    JOIN #reads r ON r.GroupSource = m.GroupSource AND r.GroupRef = m.GroupRef AND r.Status = 'Found'
    WHERE NOT EXISTS (SELECT 1 FROM dbo.ad_group_member x WHERE x.GroupKey = r.GroupKey AND x.DomainName = m.DomainName AND x.SamAccountName = m.SamAccountName);

    DELETE g FROM dbo.ad_group_read g WHERE NOT EXISTS (SELECT 1 FROM #reads r WHERE r.GroupKey = g.GroupKey);
    MERGE dbo.ad_group_read AS tgt
    USING (SELECT DISTINCT GroupKey, MatchedBy, Status, DomainName, MemberCount, Message FROM #reads) AS src ON tgt.GroupKey = src.GroupKey
    WHEN MATCHED THEN
        UPDATE SET MatchedBy = src.MatchedBy, Status = src.Status, Message = src.Message, LastAttemptAt = @Now,
                   DomainName = CASE WHEN src.Status = 'Failed' THEN tgt.DomainName ELSE src.DomainName END,
                   MemberCount = CASE WHEN src.Status = 'Failed' THEN tgt.MemberCount ELSE src.MemberCount END,
                   LastFoundAt = CASE WHEN src.Status = 'Found' THEN @Now WHEN src.Status = 'NotFound' THEN NULL ELSE tgt.LastFoundAt END
    WHEN NOT MATCHED THEN
        INSERT (GroupKey, MatchedBy, Status, DomainName, MemberCount, Message, LastAttemptAt, LastFoundAt)
        VALUES (src.GroupKey, src.MatchedBy, src.Status, src.DomainName, src.MemberCount, src.Message, @Now,
                CASE WHEN src.Status = 'Found' THEN @Now END);
END
GO

/* The Load's step: only with tonight's Direct staging (the pull staged the AD reads with it). */
CREATE OR ALTER PROCEDURE dbo.usp_Load_AdGroupMembers
AS
BEGIN
    SET NOCOUNT ON;
    IF dbo.fn_SelfHostedStagedByDirect() = 1 EXEC dbo.usp_Apply_AdGroupMembers;
END
GO

/* Who can reach each Safe: its user members (Direct), the users of its Vault group members (Via Vault group), and the
   AD users of its group members that were found in AD (Via AD group; for such a group the Vault's own member list is
   left out, as AD's is the whole of it). VaultUserKey is set for a Vault user; an AD user has DomainName and
   SamAccountName. EntitlementKey gives the permissions (bridge_entitlement_permission). */
CREATE OR ALTER VIEW dbo.vw_safe_access_people AS
SELECT fse.SafeKey, fse.EntitlementKey, u.UserKey AS VaultUserKey, u.Username AS PersonName,
       CAST(NULL AS NVARCHAR(256)) AS DomainName, CAST(NULL AS NVARCHAR(256)) AS SamAccountName,
       NULLIF(LTRIM(CONCAT(u.FirstName, N' ', u.LastName)), N'') AS DisplayName, CAST(1 AS BIT) AS IsEnabled,
       CAST(N'Direct' AS NVARCHAR(300)) AS AccessPath, u.UserType
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_user u ON u.UserKey = fse.UserKey
WHERE fse.MemberType = 'User'
UNION ALL
SELECT fse.SafeKey, fse.EntitlementKey, u.UserKey, u.Username, NULL, NULL,
       NULLIF(LTRIM(CONCAT(u.FirstName, N' ', u.LastName)), N''), CAST(1 AS BIT), CAST(N'Via Vault group: ' + g.GroupName AS NVARCHAR(300)), u.UserType
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_group g ON g.GroupKey = fse.GroupKey
JOIN dbo.bridge_group_membership bgm ON bgm.GroupKey = g.GroupKey
JOIN dbo.dim_user u ON u.UserKey = bgm.UserKey
WHERE fse.MemberType = 'Group'
  AND NOT EXISTS (SELECT 1 FROM dbo.ad_group_read r WHERE r.GroupKey = g.GroupKey AND r.LastFoundAt IS NOT NULL)
UNION ALL
SELECT fse.SafeKey, fse.EntitlementKey, NULL, m.DomainName + N'\' + m.SamAccountName, m.DomainName, m.SamAccountName,
       m.DisplayName, m.IsEnabled, CAST(N'Via AD group: ' + g.GroupName AS NVARCHAR(300)), NULL
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_group g ON g.GroupKey = fse.GroupKey
JOIN dbo.ad_group_member m ON m.GroupKey = g.GroupKey
WHERE fse.MemberType = 'Group';
GO

/* ---- 9. The full load ------------------------------------------------------- */

/* As in 21_BlueTrack_SnapshotAndSiteUrl.sql, plus usp_Load_DimCyberArkApplication (D-282) and usp_Load_AdGroupMembers
   (D-281) after the groups, and usp_Load_FactAccountClassicDetail (D-280) after usp_Load_FactAccount. */
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

PRINT '48_BlueTrack_SelfHostedDirectExtras.sql complete.';
GO
