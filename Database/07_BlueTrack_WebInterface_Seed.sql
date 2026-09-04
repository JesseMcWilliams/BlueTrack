/* ============================================================================
   07_BlueTrack_WebInterface_Seed.sql

   RUN THIS AFTER 06.

   Minimum-viable seed data for the web interface schema, so the
   application is actually usable end to end rather than just compiling:
   one enabled WindowsIntegrated identity provider, one bootstrap Admin
   role bundling every confirmed permission (D-61), and a mapping from a
   real AD admin group to that role.

   BEFORE RUNNING: replace @AdminGroupName in step 4 below with the actual
   AD group that should grant Admin access. A placeholder is deliberately
   left there rather than a guessed-at real group name -- step 4 skips
   itself (with a PRINT message) if the placeholder hasn't been replaced,
   rather than mapping a fake group.

   Per D-58, this is a hand-written numbered script that only adds data --
   it never drops or alters existing schema. Each step below is guarded
   (IF NOT EXISTS) so this file can be re-run safely while iterating in Dev.
   ============================================================================ */

USE $DatabaseName$;
GO


/* ============================================================================
   1. WindowsIntegrated identity provider (D-01, D-30) -- the only provider
      actually wired in App/Api/Auth/AuthenticationExtensions.cs so far.
   ============================================================================ */
IF NOT EXISTS (SELECT 1 FROM web.identity_provider_config WHERE ProviderType = 'WindowsIntegrated')
BEGIN
    INSERT INTO web.identity_provider_config (ProviderType, DisplayName, IsEnabled, DisplayOrder)
    VALUES ('WindowsIntegrated', 'Windows Integrated', 1, 1);
END
GO


/* ============================================================================
   2. Bootstrap Admin role.

   Design_Authorization_Model.md's example bundles (Viewer/Analyst/Approver/
   Admin) are explicitly "illustrative... confirm before building as
   literal default rows" -- this one exception is seeded anyway, because
   without at least one all-permissions role, nobody could log in and use
   the Roles & Permissions admin screen to define anything narrower. Treat
   this as a bootstrap, not a design decision about your eventual role
   structure -- split it up once real roles are defined.
   ============================================================================ */
IF NOT EXISTS (SELECT 1 FROM web.app_role WHERE RoleName = 'Admin')
BEGIN
    INSERT INTO web.app_role (RoleName, Description)
    VALUES ('Admin', 'Bootstrap role with every permission -- narrow this down via the Roles & Permissions admin screen once other roles exist.');
END
GO


/* ============================================================================
   3. Bundle every confirmed permission (D-61) into the bootstrap Admin role.
   ============================================================================ */
DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT @AdminRoleKey, p.PermissionKey
FROM web.app_permission p
WHERE NOT EXISTS (
    SELECT 1 FROM web.role_permission rp
    WHERE rp.RoleKey = @AdminRoleKey AND rp.PermissionKey = p.PermissionKey
);
GO


/* ============================================================================
   4. Map a real AD admin group to the bootstrap Admin role, scoped to the
      WindowsIntegrated provider (D-03, D-04, D-05, D-13, D-14).

      REPLACE THE PLACEHOLDER BELOW before this step will do anything.
   ============================================================================ */
DECLARE @AdminGroupName NVARCHAR(300) = 'REPLACE_WITH_YOUR_ADMIN_AD_GROUP';
DECLARE @WinIntProviderKey INT = (SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'WindowsIntegrated');
DECLARE @AdminRoleKeyForMapping INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

IF @AdminGroupName = 'REPLACE_WITH_YOUR_ADMIN_AD_GROUP'
BEGIN
    PRINT 'Skipped identity_group_role_map: replace @AdminGroupName in 07_BlueTrack_WebInterface_Seed.sql with your real AD admin group, then re-run this file.';
END
ELSE IF NOT EXISTS (
    SELECT 1 FROM web.identity_group_role_map
    WHERE ProviderKey = @WinIntProviderKey AND IdentityGroupName = @AdminGroupName AND AppRoleKey = @AdminRoleKeyForMapping
)
BEGIN
    INSERT INTO web.identity_group_role_map (ProviderKey, IdentityGroupName, AppRoleKey)
    VALUES (@WinIntProviderKey, @AdminGroupName, @AdminRoleKeyForMapping);
END
GO


PRINT 'BlueTrack web interface seed data applied.';
