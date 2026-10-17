/* ============================================================================
   31_BlueTrack_ApiAccess.sql

   RUN THIS AFTER 01-30. Guarded -- safe to re-run.

   D-243 (phase 1): API access for scripts and other systems. A caller
   exchanges an API key for a short-lived BlueTrack token (a JWT) and sends
   that token on API requests.

   - web.api_key: one row per key. Only a SHA-256 hash of the key is stored;
     KeyPrefix (the first characters) finds the row. One role per key.
   - web.api_signing_key: the secret that signs the tokens, protected with
     Windows DPAPI (Machine scope). Created by the API on first use.
   - web.app_config.ApiKeyDefaultLifetimeDays: the expiry given to a new key
     (default 90, 1 to 365).
   - An 'ApiAccess' identity provider row, so a key's identity is an app_user
     like any other (audit, rights cache). It is hidden from the sign-in
     page and the Identity Providers page, and is not a way to sign in.
   - An app_user, the 'BlueTrack API access (system)' user, which failed
     token requests for an unknown key are attributed to.
   - The ManageApiKeys permission, granted to Admin.
   - Audit event types: ApiKeyCreated, ApiKeyChanged, ApiTokenIssued,
     ApiTokenRejected.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.api_key', 'U') IS NULL
BEGIN
    CREATE TABLE web.api_key (
        ApiKeyKey        INT IDENTITY(1,1) CONSTRAINT PK_api_key PRIMARY KEY,
        DisplayName      NVARCHAR(100)  NOT NULL CONSTRAINT UQ_api_key_DisplayName UNIQUE,
        KeyPrefix        NVARCHAR(16)   NOT NULL CONSTRAINT UQ_api_key_KeyPrefix UNIQUE,
        KeyHash          VARBINARY(32)  NOT NULL,
        AppRoleKey       INT            NOT NULL CONSTRAINT FK_api_key_AppRole REFERENCES web.app_role(AppRoleKey),
        ExpiresAt        DATETIME2      NOT NULL,
        IsEnabled        BIT            NOT NULL CONSTRAINT DF_api_key_IsEnabled DEFAULT (1),
        CreatedBy        INT            NOT NULL CONSTRAINT FK_api_key_CreatedBy REFERENCES web.app_user(UserKey),
        CreatedAt        DATETIME2      NOT NULL CONSTRAINT DF_api_key_CreatedAt DEFAULT SYSUTCDATETIME(),
        LastUsedAt       DATETIME2      NULL,
        LastUsedFrom     NVARCHAR(64)   NULL
    );
END
GO

IF OBJECT_ID('web.api_signing_key', 'U') IS NULL
BEGIN
    CREATE TABLE web.api_signing_key (
        SigningKeyKey    INT IDENTITY(1,1) CONSTRAINT PK_api_signing_key PRIMARY KEY,
        KeyId            NVARCHAR(40)   NOT NULL CONSTRAINT UQ_api_signing_key_KeyId UNIQUE,
        ProtectedKey     NVARCHAR(MAX)  NOT NULL,
        IsActive         BIT            NOT NULL CONSTRAINT DF_api_signing_key_IsActive DEFAULT (1),
        CreatedAt        DATETIME2      NOT NULL CONSTRAINT DF_api_signing_key_CreatedAt DEFAULT SYSUTCDATETIME()
    );
END
GO

IF COL_LENGTH('web.app_config', 'ApiKeyDefaultLifetimeDays') IS NULL
BEGIN
    ALTER TABLE web.app_config ADD ApiKeyDefaultLifetimeDays INT NOT NULL
        CONSTRAINT DF_app_config_ApiKeyDefaultLifetimeDays DEFAULT (90)
        CONSTRAINT CK_app_config_ApiKeyDefaultLifetimeDays CHECK (ApiKeyDefaultLifetimeDays BETWEEN 1 AND 365);
END
GO

IF NOT EXISTS (SELECT 1 FROM web.identity_provider_config WHERE ProviderType = 'ApiAccess')
BEGIN
    INSERT INTO web.identity_provider_config (ProviderType, DisplayName, IsEnabled, DisplayOrder)
    VALUES ('ApiAccess', 'API access (keys)', 1, 900);
END
GO

DECLARE @ApiProviderKey INT = (SELECT TOP (1) ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'ApiAccess');
IF NOT EXISTS (SELECT 1 FROM web.app_user WHERE ProviderKey = @ApiProviderKey AND ExternalIdentifier = 'apikey-system')
BEGIN
    INSERT INTO web.app_user (ProviderKey, ExternalIdentifier, DisplayName)
    VALUES (@ApiProviderKey, 'apikey-system', 'BlueTrack API access (system)');
END
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageApiKeys')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageApiKeys', 'Create, disable and delete API keys for scripts and other systems');
END
GO

INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT r.AppRoleKey, p.PermissionKey
FROM web.app_role r
CROSS JOIN web.app_permission p
WHERE r.RoleName = 'Admin' AND p.PermissionName = 'ManageApiKeys'
  AND NOT EXISTS (SELECT 1 FROM web.role_permission rp WHERE rp.RoleKey = r.AppRoleKey AND rp.PermissionKey = p.PermissionKey);
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('ApiKeyCreated',    'An API key was created (name, role, expiry; never the key)'),
    ('ApiKeyChanged',    'An API key was enabled, disabled or deleted'),
    ('ApiTokenIssued',   'An API key was exchanged for a short-lived token'),
    ('ApiTokenRejected', 'A token request was refused (unknown, expired or disabled key; the key prefix only)')
) AS v(EventTypeName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName);
GO
