/* ============================================================================
   17_BlueTrack_CredentialsLdapBackupSchema.sql

   RUN THIS AFTER 01-16. Guarded (`IF OBJECT_ID(...) IS NULL` / column-exists
   checks) -- safe to re-run, per D-58's normal incremental-script convention.

   Resolved directly with the user (2026-09-08), all in
   Design_Credentials_Management.md / Design_Notifications.md's updated
   sections:
     - A generic web.credential store: pick a secrets backend (Windows DPAPI
       or any registered IVaultSecretProvider) per named credential, not one
       app-wide active backend (web.secrets_store's own "exactly one active"
       rule is unchanged -- this is a second, independent concept: naming and
       storing individual credentials this app itself authenticates *as*,
       e.g. the SMTP account or an LDAP bind account, versus which vault
       backend resolves *privileged-account* secrets CyberArk manages).
     - DPAPI credentials get a ScopePreference (admin's choice, Machine or
       User) separate from CurrentScope (what the stored ciphertext is
       actually protected with right now). A new credential always starts
       CurrentScope = 'Machine' regardless of ScopePreference -- if 'User'
       was chosen, the app opportunistically re-protects to CurrentUser scope
       and flips CurrentScope on the first successful decrypt (see
       CredentialRepository.ResolveForUseAsync), since every request in this
       app already runs under the one app-pool identity (D-30) -- there's no
       separate "admin-interactive" identity a save-time encrypt could get
       wrong the way a desktop app's own interactive user might.
     - SMTP's own Username/PasswordSecretReference columns (16's original
       shape) are replaced with a single SmtpCredentialKey pointing at a
       web.credential row -- the user's explicit choice to migrate SMTP onto
       the new store rather than leave it on its own separate inline fields.
     - web.app_role gets a NotificationEmail column, and
       web.dim_notification_type gets a nullable TargetRoleKey -- additive to
       the existing flat web.notification_recipient list (D-115), not a
       replacement: a notification type with no TargetRoleKey behaves exactly
       as before. When set, the final recipient list unions the flat list
       with the target role's own NotificationEmail and, if LDAP is enabled,
       every mail-attributed member of every AD group mapped to that role
       (web.identity_group_role_map), expanded recursively through nested
       groups -- resolved via a new LdapGroupMemberResolver, this app's first
       use of System.DirectoryServices.AccountManagement (a deliberate,
       explicit reversal of AdminUsersController.cs's prior "deliberately
       does NOT query AD/LDAP" stance, requested directly this time).
     - web.ldap_config: a singleton (matching web.notification_config's own
       shape) holding the domain controller/search base/SSL choice and a
       CredentialKey pointing at the LDAP bind account -- disabled by
       default, same "admin fills in real values later" pattern as OIDC/SAML.
     - web.app_config gets a BackupFolder setting for the new Deployment page
       "Backup App" button (BACKUP DATABASE ... TO DISK target directory --
       an admin-configurable path, not SQL Server's own default backup
       directory, so BlueTrack's own admin UI is the one place that decides
       where its backups land, consistent with every other admin-editable
       setting in this app).
     - Three new permissions: ManageCredentials (the new Credentials & LDAP
       admin page), TriggerBackup (the new Deployment page backup button --
       deliberately separate from the existing read-only ViewDeploymentInfo).
       Explicitly granted to the bootstrap Admin role, same as every
       permission added after 09_BlueTrack_WebSeed.sql's own one-time
       blanket grant already ran (D-98/D-115 precedent).
   ============================================================================ */

USE $DatabaseName$;
GO

-- web.credential: a named credential this app authenticates *as* (SMTP
-- account, LDAP bind account, and whatever else needs one later) -- distinct
-- from web.secrets_store, which picks the one active backend for resolving
-- *privileged-account* secrets CyberArk/a vault manages. Each credential
-- picks its own backend independently (CredentialRepository.ResolveForUseAsync
-- resolves DPAPI locally or calls the matching IVaultSecretProvider by
-- BackendType, not "whichever is globally active").
--
-- DPAPI fields (Username/ProtectedPassword/ScopePreference/CurrentScope) are
-- populated only when BackendType = 'WindowsDpapi'. Vault fields
-- (VaultSafe/VaultFolder/VaultObject) reuse IVaultSecretProvider's own
-- Safe/Folder/Object query shape (already how CyberArk CP/CCP/Conjur and,
-- with different field meanings per provider, Azure Key Vault/AWS Secrets
-- Manager all interpret a reference) and are populated only when BackendType
-- is one of those five. All are nullable so exactly one set applies per row.
IF OBJECT_ID('web.credential', 'U') IS NULL
BEGIN
    CREATE TABLE web.credential (
        CredentialKey        INT IDENTITY(1,1) PRIMARY KEY,
        CredentialName          NVARCHAR(100)    NOT NULL UNIQUE,
        BackendType                NVARCHAR(50)     NOT NULL,   -- 'WindowsDpapi' | 'CyberArkCP' | 'CyberArkCCP' | 'CyberArkConjur' | 'AzureKeyVault' | 'AwsSecretsManager'
        Username                      NVARCHAR(255)    NULL,       -- DPAPI only
        ProtectedPassword                NVARCHAR(2000)   NULL,       -- DPAPI only -- ciphertext
        ScopePreference                     NVARCHAR(10)     NULL,       -- DPAPI only -- 'Machine' | 'User', the admin's choice
        CurrentScope                           NVARCHAR(10)     NULL,       -- DPAPI only -- 'Machine' | 'User', what ProtectedPassword is actually protected with right now
        VaultSafe                                 NVARCHAR(100)    NULL,       -- vault backends only
        VaultFolder                                  NVARCHAR(100)    NULL,       -- vault backends only
        VaultObject                                     NVARCHAR(200)    NULL,       -- vault backends only
        ModifiedBy                                         INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedDate                                           DATETIME2        NULL
    );
END
GO

-- web.ldap_config: singleton, disabled by default -- same "admin fills in
-- real values later" pattern as OIDC/SAML's placeholder rows (D-84).
-- CredentialKey (nullable) points at the web.credential row holding the bind
-- account; LDAP lookups are skipped entirely (not attempted, not an error)
-- until both IsEnabled and CredentialKey are set.
IF OBJECT_ID('web.ldap_config', 'U') IS NULL
BEGIN
    CREATE TABLE web.ldap_config (
        LdapConfigKey     INT IDENTITY(1,1) PRIMARY KEY,
        IsEnabled            BIT              NOT NULL DEFAULT 0,
        DomainController        NVARCHAR(255)    NULL,
        SearchBase                  NVARCHAR(500)    NULL,
        UseSsl                          BIT              NOT NULL DEFAULT 0,
        CredentialKey                      INT              NULL REFERENCES web.credential(CredentialKey),
        ModifiedBy                             INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedDate                               DATETIME2        NULL
    );

    INSERT INTO web.ldap_config (IsEnabled, UseSsl) VALUES (0, 0);
END
GO

-- Migrate notification_config's SMTP credential off its own inline
-- Username/PasswordSecretReference columns onto the new shared web.credential
-- store -- the user's explicit choice (2026-09-08) over leaving SMTP on its
-- own separate storage. 16's Username/PasswordSecretReference columns held
-- only test values (already reverted to NULL before D-115 shipped), so
-- there's no real data this drops.
IF COL_LENGTH('web.notification_config', 'SmtpCredentialKey') IS NULL
BEGIN
    ALTER TABLE web.notification_config ADD SmtpCredentialKey INT NULL REFERENCES web.credential(CredentialKey);
END
GO

IF COL_LENGTH('web.notification_config', 'Username') IS NOT NULL
BEGIN
    ALTER TABLE web.notification_config DROP COLUMN Username;
END
GO

IF COL_LENGTH('web.notification_config', 'PasswordSecretReference') IS NOT NULL
BEGIN
    ALTER TABLE web.notification_config DROP COLUMN PasswordSecretReference;
END
GO

-- Per-role notification email -- additive to D-115's flat recipient list,
-- not a replacement (Design_Notifications.md's updated Proposed Workflow).
IF COL_LENGTH('web.app_role', 'NotificationEmail') IS NULL
BEGIN
    ALTER TABLE web.app_role ADD NotificationEmail NVARCHAR(320) NULL;
END
GO

-- Optional per-notification-type target role. NULL (the default for the
-- existing DevFakeAuthEnabledTooLong row) preserves D-115's exact behavior --
-- flat recipient list only -- until an admin explicitly assigns a role on
-- the Notifications page.
IF COL_LENGTH('web.dim_notification_type', 'TargetRoleKey') IS NULL
BEGIN
    ALTER TABLE web.dim_notification_type ADD TargetRoleKey INT NULL REFERENCES web.app_role(AppRoleKey);
END
GO

-- Backup destination folder for the new Deployment page "Backup App" button.
IF COL_LENGTH('web.app_config', 'BackupFolder') IS NULL
BEGIN
    ALTER TABLE web.app_config ADD BackupFolder NVARCHAR(500) NULL;
END
GO

-- New permissions, added after 09_BlueTrack_WebSeed.sql's own one-time
-- blanket grant already ran -- explicit catalog insert + explicit grant to
-- the bootstrap Admin role, same as every permission added since (D-98/D-115).
IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageCredentials')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageCredentials', 'Manage stored credentials (SMTP, LDAP bind account, etc.) and LDAP configuration');
END
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'TriggerBackup')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('TriggerBackup', 'Trigger an on-demand database and configuration backup from the Deployment admin page');
END
GO

DECLARE @ManageCredentialsPermissionKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageCredentials');
DECLARE @TriggerBackupPermissionKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'TriggerBackup');
DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ManageCredentialsPermissionKey
)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @ManageCredentialsPermissionKey);
END
GO

DECLARE @AdminRoleKey2 INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @TriggerBackupPermissionKey2 INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'TriggerBackup');

IF @AdminRoleKey2 IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey2 AND PermissionKey = @TriggerBackupPermissionKey2
)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey2, @TriggerBackupPermissionKey2);
END
GO

PRINT '17_BlueTrack_CredentialsLdapBackupSchema.sql complete.';
