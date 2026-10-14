/* ============================================================================
   20_BlueTrack_SmtpOAuth2.sql

   RUN THIS AFTER 01-19. Guarded -- safe to re-run.

   D-223: OAuth2 (SASL XOAUTH2) sign-in for the SMTP sender
   (Design_Notifications.md), decided by the user on 2026-10-13. Adds the
   auth modes Microsoft365, GoogleWorkspace and GenericOAuth2 beside the
   existing None and Basic.

     web.notification_config        The settings each OAuth2 mode needs. None
                                    of them is a secret: the client secret,
                                    the certificate and private key, or the
                                    Google service account key stay in the
                                    web.credential row SmtpCredentialKey
                                    already points at.
       OAuthTenantId                Microsoft365: the Entra tenant (GUID or
                                    domain name).
       OAuthClientId                Microsoft365 and GenericOAuth2: the
                                    application (client) ID.
       OAuthClientCredentialType    Microsoft365: 'Secret' or 'Certificate'.
       OAuthTokenUrl                GenericOAuth2: the token endpoint (https).
       OAuthScope                   GenericOAuth2: space-separated scopes.
       OAuthAudience                GenericOAuth2: optional 'audience' value.
       OAuthClientAuthStyle         GenericOAuth2: 'Basic' (Authorization
                                    header, RFC 6749 2.3.1) or 'Body'.
       OAuthUsername                The mailbox to sign in as (the XOAUTH2
                                    user, and Google's impersonated user);
                                    blank means the From address.
       CK_notification_config_AuthMethod
                                    AuthMethod is one of the five modes (any
                                    other value, which the sender treated as
                                    no sign-in, becomes 'None' first).
     web.credential.ProtectedPassword
                                    NVARCHAR(2000) -> NVARCHAR(MAX): a Google
                                    service account key (about 2.3 KB of JSON)
                                    or a PEM certificate and key no longer fit
                                    once encrypted and Base64-encoded.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.notification_config', 'OAuthTenantId') IS NULL
    ALTER TABLE web.notification_config ADD OAuthTenantId NVARCHAR(100) NULL;
GO

IF COL_LENGTH('web.notification_config', 'OAuthClientId') IS NULL
    ALTER TABLE web.notification_config ADD OAuthClientId NVARCHAR(255) NULL;
GO

IF COL_LENGTH('web.notification_config', 'OAuthClientCredentialType') IS NULL
    ALTER TABLE web.notification_config ADD OAuthClientCredentialType NVARCHAR(20) NULL
        CONSTRAINT CK_notification_config_OAuthClientCredentialType CHECK (OAuthClientCredentialType IN ('Secret', 'Certificate'));
GO

IF COL_LENGTH('web.notification_config', 'OAuthTokenUrl') IS NULL
    ALTER TABLE web.notification_config ADD OAuthTokenUrl NVARCHAR(500) NULL;
GO

IF COL_LENGTH('web.notification_config', 'OAuthScope') IS NULL
    ALTER TABLE web.notification_config ADD OAuthScope NVARCHAR(1000) NULL;
GO

IF COL_LENGTH('web.notification_config', 'OAuthAudience') IS NULL
    ALTER TABLE web.notification_config ADD OAuthAudience NVARCHAR(500) NULL;
GO

IF COL_LENGTH('web.notification_config', 'OAuthClientAuthStyle') IS NULL
    ALTER TABLE web.notification_config ADD OAuthClientAuthStyle NVARCHAR(20) NULL
        CONSTRAINT CK_notification_config_OAuthClientAuthStyle CHECK (OAuthClientAuthStyle IN ('Basic', 'Body'));
GO

IF COL_LENGTH('web.notification_config', 'OAuthUsername') IS NULL
    ALTER TABLE web.notification_config ADD OAuthUsername NVARCHAR(320) NULL;
GO

-- The API accepted any AuthMethod before D-223, and the sender only ever
-- signed in for 'Basic', so any other value already behaved as 'None'.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_notification_config_AuthMethod')
    UPDATE web.notification_config SET AuthMethod = 'None'
    WHERE AuthMethod NOT IN ('None', 'Basic', 'Microsoft365', 'GoogleWorkspace', 'GenericOAuth2');
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_notification_config_AuthMethod')
    ALTER TABLE web.notification_config ADD CONSTRAINT CK_notification_config_AuthMethod
        CHECK (AuthMethod IN ('None', 'Basic', 'Microsoft365', 'GoogleWorkspace', 'GenericOAuth2'));
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('web.credential') AND name = 'ProtectedPassword' AND max_length <> -1)
    ALTER TABLE web.credential ALTER COLUMN ProtectedPassword NVARCHAR(MAX) NULL;
GO
