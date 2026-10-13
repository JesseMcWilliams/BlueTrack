/* ============================================================================
   09_BlueTrack_DefaultSignInProvider.sql

   RUN THIS AFTER 01-08. Guarded -- safe to re-run.

   D-205: an explicit default sign-in method, set on the Identity Providers
   page (requested 2026-10-12). Before this the default was implied: the
   enabled provider with the lowest DisplayOrder (D-41).

     web.app_config.DefaultSignInProvider   'WindowsIntegrated', 'OIDC', 'SAML', or NULL.
                                            NULL = no default: the sign-in page lists
                                            the enabled methods.

   On first run it's set to today's implied default (the lowest-DisplayOrder
   enabled provider other than DevFakeAuth), so an existing install signs
   people in exactly as before.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'DefaultSignInProvider') IS NULL
BEGIN
    ALTER TABLE web.app_config ADD DefaultSignInProvider NVARCHAR(50) NULL;

    EXEC (N'
        UPDATE web.app_config
        SET DefaultSignInProvider = (
            SELECT TOP (1) ProviderType
            FROM web.identity_provider_config
            WHERE IsEnabled = 1 AND ProviderType IN (N''WindowsIntegrated'', N''OIDC'', N''SAML'')
            ORDER BY DisplayOrder, ProviderKey);');
END
GO
