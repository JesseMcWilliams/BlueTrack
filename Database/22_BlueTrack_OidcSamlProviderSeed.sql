/* ============================================================================
   22_BlueTrack_OidcSamlProviderSeed.sql

   RUN THIS AFTER 01-21.

   D-84: seeds disabled OIDC and SAML placeholder rows into
   web.identity_provider_config, the same way 18_BlueTrack_DevFakeAuthSeed.sql
   seeded a disabled DevFakeAuth row -- the framework (AuthenticationExtensions,
   Saml2Controller/Saml2ConfigurationFactory) exists ahead of real IdP
   metadata, but nothing is enabled or usable until an admin fills in real
   values via the Identity Providers admin page and (for OIDC) restarts the
   app -- see AuthenticationExtensions.cs's own comment on why OIDC scheme
   registration is startup-time, not dynamic.

   ConfigurationValues here documents the expected shape
   (OidcProviderSettings / SamlProviderSettings) rather than real values --
   an admin overwrites this via the admin page once real values exist.

   Guarded -- safe to re-run.
   ============================================================================ */

USE BlueTrack;
GO

IF NOT EXISTS (SELECT 1 FROM web.identity_provider_config WHERE ProviderType = 'OIDC')
BEGIN
    INSERT INTO web.identity_provider_config (ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues, SecretReference)
    VALUES (
        'OIDC',
        'Microsoft Entra ID',
        0,
        2,
        N'{"Authority":"","ClientId":"","CallbackPath":"/signin-oidc","GroupsClaimType":"groups"}',
        NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM web.identity_provider_config WHERE ProviderType = 'SAML')
BEGIN
    INSERT INTO web.identity_provider_config (ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues, SecretReference)
    VALUES (
        'SAML',
        'Okta',
        0,
        3,
        N'{"SpEntityId":"","SpCertificateThumbprint":"","IdpEntityId":"","IdpSingleSignOnDestination":"","IdpSingleLogoutDestination":"","IdpCertificateThumbprint":"","GroupClaimType":"http://schemas.xmlsoap.org/claims/Group"}',
        NULL
    );
END

PRINT 'identity_provider_config: OIDC/SAML placeholder rows present (disabled).';
