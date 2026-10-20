/* ============================================================================
   41_BlueTrack_SelfHostedDirectStaging.sql

   RUN THIS AFTER 01-40. Guarded -- safe to re-run.

   D-270 (Claude_Docs/Planning_Self-Hosted-Direct-And-EVD-Retirement.md, phase 1):
   Self-Hosted Direct gets its own staging tables, stg_shd_*, holding what PVWA's
   REST API returns in REST's own shape (PascalCase REST field names, UTC times),
   including what the EVD-shaped stg_sh_* tables have no column for: the CPM
   status and last reconcile time, the latest CPM verify/change/reconcile from
   each CPM-processed account's activity, platform policy, every account
   property, predefined Safe members, and the user details.

   A pull writes these tables and the stg_sh_* tables in the same transaction.
   Nothing reads stg_shd_* yet: the Load still reads stg_sh_*, so its output is
   unchanged. An EVD import doesn't touch these tables.

   Every table: LoadId, ImportBatchId (dbo.cyberark_direct_pull.ImportBatchId),
   SourceFileName ('PVWA REST (host)'), LoadTimestamp.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('dbo.stg_shd_platforms') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_platforms (
        LoadId                                         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId                                  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName                                 NVARCHAR(260)    NOT NULL,
        LoadTimestamp                                  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PlatformId                                     NVARCHAR(100)    NOT NULL,
        Name                                           NVARCHAR(200)    NULL,
        SystemType                                     NVARCHAR(100)    NULL,
        PlatformType                                   NVARCHAR(50)     NULL,
        PlatformBaseId                                 NVARCHAR(100)    NULL,
        Active                                         BIT              NULL,
        Description                                    NVARCHAR(1000)   NULL,
        AllowedSafes                                   NVARCHAR(1000)   NULL,
        PerformPeriodicVerification                    BIT              NULL,
        RequirePasswordVerificationEveryXDays          INT              NULL,
        AllowManualVerification                        BIT              NULL,
        PerformPeriodicChange                          BIT              NULL,
        RequirePasswordChangeEveryXDays                INT              NULL,
        AllowManualChange                              BIT              NULL,
        AutomaticReconcileWhenUnsynched                BIT              NULL,
        AllowManualReconciliation                      BIT              NULL,
        RequirePrivilegedSessionMonitoringAndIsolation BIT              NULL,
        RecordAndSaveSessionActivity                   BIT              NULL,
        PsmServerId                                    NVARCHAR(200)    NULL,
        RequireDualControlPasswordAccessApproval       BIT              NULL,
        EnforceCheckinCheckoutExclusiveAccess          BIT              NULL,
        EnforceOnetimePasswordAccess                   BIT              NULL,
        -- From GET Platforms/Targets (target platforms only; NULL for others)
        RequireUsersToSpecifyReasonForAccess           BIT              NULL,
        VerifyOnAdd                                    BIT              NULL,
        ChangeOnAdd                                    BIT              NULL,
        ChangePasswordInResetMode                      BIT              NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_platform_properties') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_platform_properties (
        LoadId         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName NVARCHAR(260)    NOT NULL,
        LoadTimestamp  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PlatformId     NVARCHAR(100)    NOT NULL,
        Kind           NVARCHAR(20)     NOT NULL,   -- Required, Optional, LinkedAccount
        Name           NVARCHAR(200)    NOT NULL,
        DisplayName    NVARCHAR(200)    NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_safes') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_safes (
        LoadId                    BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId             UNIQUEIDENTIFIER NOT NULL,
        SourceFileName            NVARCHAR(260)    NOT NULL,
        LoadTimestamp             DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        SafeNumber                BIGINT           NOT NULL,
        SafeUrlId                 NVARCHAR(300)    NOT NULL,
        SafeName                  NVARCHAR(300)    NOT NULL,
        Description               NVARCHAR(1000)   NULL,
        Location                  NVARCHAR(400)    NULL,
        CreatorId                 NVARCHAR(100)    NULL,
        CreatorName               NVARCHAR(513)    NULL,
        OlacEnabled               BIT              NULL,
        ManagingCpm               NVARCHAR(200)    NULL,
        NumberOfVersionsRetention INT              NULL,
        NumberOfDaysRetention     INT              NULL,
        AutoPurgeEnabled          BIT              NULL,
        CreationTime              DATETIME2        NULL,
        LastModificationTime      DATETIME2        NULL,
        IsExpiredMember           BIT              NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_accounts') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_accounts (
        LoadId                           BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId                    UNIQUEIDENTIFIER NOT NULL,
        SourceFileName                   NVARCHAR(260)    NOT NULL,
        LoadTimestamp                    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        AccountId                        NVARCHAR(100)    NOT NULL,   -- SafeNumber_FileId, the Load's SourceAccountId
        SafeNumber                       BIGINT           NOT NULL,
        FileId                           BIGINT           NOT NULL,
        SafeName                         NVARCHAR(300)    NULL,
        Name                             NVARCHAR(512)    NULL,
        Address                          NVARCHAR(512)    NULL,
        UserName                         NVARCHAR(512)    NULL,
        PlatformId                       NVARCHAR(100)    NULL,
        SecretType                       NVARCHAR(50)     NULL,
        CreatedTime                      DATETIME2        NULL,
        CategoryModificationTime         DATETIME2        NULL,
        AutomaticManagementEnabled       BIT              NULL,
        ManualManagementReason           NVARCHAR(1000)   NULL,
        CpmStatus                        NVARCHAR(50)     NULL,       -- as PVWA sent it (the lab: lowercase success/failure)
        LastModifiedTime                 DATETIME2        NULL,
        LastReconciledTime               DATETIME2        NULL,
        LastVerifiedTime                 DATETIME2        NULL,
        RemoteMachines                   NVARCHAR(4000)   NULL,
        AccessRestrictedToRemoteMachines BIT              NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_account_properties') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_account_properties (
        LoadId         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName NVARCHAR(260)    NOT NULL,
        LoadTimestamp  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        AccountId      NVARCHAR(100)    NOT NULL,
        Name           NVARCHAR(256)    NOT NULL,
        Value          NVARCHAR(4000)   NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_account_cpm_activity') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_account_cpm_activity (
        LoadId         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName NVARCHAR(260)    NOT NULL,
        LoadTimestamp  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        AccountId      NVARCHAR(100)    NOT NULL,
        ActivityCode   INT              NOT NULL,   -- 22 verify, 38 verify failure, 24 change, 57 change failure, 31 reconcile, 60 reconcile failure, 414/415/419 SSH key
        Activity       NVARCHAR(200)    NULL,
        ActivityTime   DATETIME2        NULL,       -- the latest of this code for the account
        UserName       NVARCHAR(256)    NULL,
        Reason         NVARCHAR(1000)   NULL,
        MoreInfo       NVARCHAR(1000)   NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_safe_members') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_safe_members (
        LoadId                                 BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId                          UNIQUEIDENTIFIER NOT NULL,
        SourceFileName                         NVARCHAR(260)    NOT NULL,
        LoadTimestamp                          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        SafeUrlId                              NVARCHAR(300)    NOT NULL,
        SafeName                               NVARCHAR(300)    NOT NULL,
        SafeNumber                             BIGINT           NOT NULL,
        MemberId                               BIGINT           NOT NULL,
        MemberName                             NVARCHAR(300)    NOT NULL,
        MemberType                             NVARCHAR(20)     NULL,
        MembershipExpirationDate               DATETIME2        NULL,
        IsExpiredMembershipEnable              BIT              NULL,
        IsPredefinedUser                       BIT              NULL,
        IsReadOnly                             BIT              NULL,
        -- The 22 REST permissions, named as in stg_pc_entitlements
        UseAccounts                            BIT              NULL,
        RetrieveAccounts                       BIT              NULL,
        ListAccounts                           BIT              NULL,
        AddAccounts                            BIT              NULL,
        UpdateAccountContent                   BIT              NULL,
        UpdateAccountProperties                BIT              NULL,
        InitiateCPMAccountManagementOperations BIT              NULL,
        SpecifyNextAccountContent              BIT              NULL,
        RenameAccounts                         BIT              NULL,
        DeleteAccounts                         BIT              NULL,
        UnlockAccounts                         BIT              NULL,
        ManageSafe                             BIT              NULL,
        ManageSafeMembers                      BIT              NULL,
        BackupSafe                             BIT              NULL,
        ViewAuditLog                           BIT              NULL,
        ViewSafeMembers                        BIT              NULL,
        AccessWithoutConfirmation              BIT              NULL,
        CreateFolders                          BIT              NULL,
        DeleteFolders                          BIT              NULL,
        MoveAccountsAndFolders                 BIT              NULL,
        RequestsAuthorizationLevel1            BIT              NULL,
        RequestsAuthorizationLevel2            BIT              NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_users') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_users (
        LoadId                  BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId           UNIQUEIDENTIFIER NOT NULL,
        SourceFileName          NVARCHAR(260)    NOT NULL,
        LoadTimestamp           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        UserId                  BIGINT           NOT NULL,
        UserName                NVARCHAR(256)    NOT NULL,
        Source                  NVARCHAR(50)     NULL,
        UserType                NVARCHAR(100)    NULL,
        ComponentUser           BIT              NULL,
        Location                NVARCHAR(400)    NULL,
        Enabled                 BIT              NULL,
        Suspended               BIT              NULL,
        FirstName               NVARCHAR(200)    NULL,
        MiddleName              NVARCHAR(200)    NULL,
        LastName                NVARCHAR(200)    NULL,
        -- From the user's details call; DetailsRead = 0 for built-in and component users and when the call failed
        DetailsRead             BIT              NOT NULL,
        Organization            NVARCHAR(200)    NULL,
        Department              NVARCHAR(200)    NULL,
        Title                   NVARCHAR(200)    NULL,
        Profession              NVARCHAR(200)    NULL,
        BusinessEmail           NVARCHAR(320)    NULL,
        HomeEmail               NVARCHAR(320)    NULL,
        OtherEmail              NVARCHAR(320)    NULL,
        HomePage                NVARCHAR(400)    NULL,
        UserDn                  NVARCHAR(1000)   NULL,
        DistinguishedName       NVARCHAR(1000)   NULL,
        Description             NVARCHAR(400)    NULL,
        ExpiryDate              DATETIME2        NULL,
        LastSuccessfulLoginDate DATETIME2        NULL,
        PasswordNeverExpires    BIT              NULL,
        ChangePassOnNextLogon   BIT              NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_user_authorizations') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_user_authorizations (
        LoadId         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName NVARCHAR(260)    NOT NULL,
        LoadTimestamp  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        UserId         BIGINT           NOT NULL,
        Kind           NVARCHAR(40)     NOT NULL,   -- VaultAuthorization, UnauthorizedInterface, AuthenticationMethod, AllowedAuthenticationMethod
        Value          NVARCHAR(200)    NOT NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_groups') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_groups (
        LoadId         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName NVARCHAR(260)    NOT NULL,
        LoadTimestamp  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        GroupId        BIGINT           NOT NULL,
        GroupName      NVARCHAR(256)    NOT NULL,
        GroupType      NVARCHAR(50)     NULL,
        Location       NVARCHAR(400)    NULL,
        Description    NVARCHAR(1000)   NULL,
        Directory      NVARCHAR(256)    NULL,
        Dn             NVARCHAR(1000)   NULL
    );
END
GO

IF OBJECT_ID('dbo.stg_shd_group_members') IS NULL
BEGIN
    CREATE TABLE dbo.stg_shd_group_members (
        LoadId         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName NVARCHAR(260)    NOT NULL,
        LoadTimestamp  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        GroupId        BIGINT           NOT NULL,
        MemberId       BIGINT           NOT NULL,
        MemberName     NVARCHAR(256)    NULL
    );
END
GO
