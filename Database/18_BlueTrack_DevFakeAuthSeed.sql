/* ============================================================================
   18_BlueTrack_DevFakeAuthSeed.sql

   RUN THIS AFTER 01-17.

   DevFakeAuth (Design_Authentication_Architecture.md): not a separate
   authentication mechanism -- it's the same Negotiate handler Windows
   Integrated already uses, with one substitution: group membership is
   resolved from identity_group_role_map rows scoped to THIS provider,
   keyed by the authenticated Windows username itself, rather than from
   real AD/Entra/Okta group SIDs. Lets a developer exercise every
   authorization path (Viewer/Analyst/Approver/Admin) against a local,
   non-domain Windows account. Guarded in code
   (App/Api/Auth/NegotiateProviderResolver.cs) to never take effect outside
   the Development hosting environment, regardless of IsEnabled below.

   Step 1 seeds the provider row itself, disabled by default -- enabling it
   is a deliberate admin-screen action (Identity Providers page), not
   something this script should flip on for you.

   Step 2 (the actual username -> role mapping) is deliberately left as a
   placeholder, same pattern as 07_BlueTrack_WebInterface_Seed.sql's
   @AdminGroupName: machine/developer-specific, so a guessed-at real
   username is worse than not mapping one at all. Replace
   @DevFakeAuthUsername below with your own Windows username (as it
   appears in principal.Identity.Name -- typically "MACHINENAME\username"
   for a local account, or "DOMAIN\username" if still domain-joined) and
   re-run this file.

   Guarded throughout -- safe to re-run.
   ============================================================================ */

USE BlueTrack;
GO


/* ============================================================================
   1. DevFakeAuth identity provider, disabled by default.
   ============================================================================ */
IF NOT EXISTS (SELECT 1 FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth')
BEGIN
    INSERT INTO web.identity_provider_config (ProviderType, DisplayName, IsEnabled, DisplayOrder)
    VALUES ('DevFakeAuth', 'Dev Fake Auth (Development only)', 0, 99);
END
GO


/* ============================================================================
   2. Map a local Windows username to the bootstrap Admin role, scoped to
      the DevFakeAuth provider.

      REPLACE THE PLACEHOLDER BELOW before this step will do anything.
   ============================================================================ */
DECLARE @DevFakeAuthUsername NVARCHAR(300) = 'REPLACE_WITH_YOUR_WINDOWS_USERNAME';
DECLARE @DevFakeAuthProviderKey INT = (SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth');
DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

IF @DevFakeAuthUsername = 'REPLACE_WITH_YOUR_WINDOWS_USERNAME'
BEGIN
    PRINT 'Skipped identity_group_role_map: replace @DevFakeAuthUsername in 18_BlueTrack_DevFakeAuthSeed.sql with your real Windows username, then re-run this file.';
END
ELSE IF NOT EXISTS (
    SELECT 1 FROM web.identity_group_role_map
    WHERE ProviderKey = @DevFakeAuthProviderKey AND IdentityGroupName = @DevFakeAuthUsername AND AppRoleKey = @AdminRoleKey
)
BEGIN
    INSERT INTO web.identity_group_role_map (ProviderKey, IdentityGroupName, AppRoleKey)
    VALUES (@DevFakeAuthProviderKey, @DevFakeAuthUsername, @AdminRoleKey);
END
GO

PRINT 'DevFakeAuth seed applied.';
