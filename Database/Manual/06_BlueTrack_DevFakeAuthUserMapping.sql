/* ============================================================================
   06_BlueTrack_DevFakeAuthUserMapping.sql

   D-195: this folder (Database/Manual) holds the scripts App/Migrator never
   runs -- it reads only the top level of Database/. Each is run by hand
   (sqlcmd or SSMS) or by Deploy/Install-BlueTrack.ps1.

   Development only. Maps one local Windows user to the bootstrap Admin role
   through the DevFakeAuth provider (Design_Authentication-Architecture.md):
   the same Negotiate handler Windows Integrated uses, except that group
   membership comes from identity_group_role_map rows scoped to DevFakeAuth,
   keyed by the signed-in Windows username, instead of real group SIDs. It
   never takes effect outside the Development hosting environment
   (App/Api/Auth/NegotiateProviderResolver.cs), and the provider row,
   seeded disabled by 05_BlueTrack_Baseline_WebSeed.sql, must also be
   enabled on the Identity Providers page.

   Replace @DevFakeAuthUsername below with your Windows username as it
   appears in principal.Identity.Name ("MACHINENAME\username" for a local
   account, "DOMAIN\username" on a domain), then run it:

       sqlcmd -S <server> -C -d BlueTrack -i 06_BlueTrack_DevFakeAuthUserMapping.sql

   Guarded -- safe to re-run. Left unedited, it changes nothing.
   ============================================================================ */



/* ============================================================================
   Map a local Windows username to the bootstrap Admin role, scoped to
   the DevFakeAuth provider.

   REPLACE THE PLACEHOLDER BELOW before this step will do anything.
   ============================================================================ */
DECLARE @DevFakeAuthUsername NVARCHAR(300) = 'REPLACE_WITH_YOUR_WINDOWS_USERNAME';
DECLARE @DevFakeAuthProviderKey INT = (SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth');
DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

IF @DevFakeAuthUsername = 'REPLACE_WITH_YOUR_WINDOWS_USERNAME'
BEGIN
    PRINT 'Skipped identity_group_role_map: replace @DevFakeAuthUsername in 06_BlueTrack_DevFakeAuthUserMapping.sql with your real Windows username, then run it again.';
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

PRINT '06_BlueTrack_DevFakeAuthUserMapping.sql complete.';
