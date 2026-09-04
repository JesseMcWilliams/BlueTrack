/* ============================================================================
   07_BlueTrack_WebInterface_Seed.sql

   RUN THIS AFTER 06.

   Minimum-viable seed data for the web interface schema, so the
   application is actually usable end to end rather than just compiling:
   one enabled WindowsIntegrated identity provider, one bootstrap Admin
   role bundling every confirmed permission (D-61), and a mapping from a
   real AD admin group to that role.

   Step 4's default admin group is BUILTIN\Administrators (SID
   S-1-5-32-544, a fixed well-known SID -- confirmed by direct lookup on
   2026-09-04, not assumed), chosen explicitly as a bootstrap default
   rather than a guessed-at real AD group, since every domain-joined
   Windows Server already has local admins in that group and it needs no
   AD group to actually exist first. Change this to a real AD group any
   time after install via the Group/Role Mapping admin screen (or by
   editing web.identity_group_role_map directly) -- it's a normal runtime
   setting, not something baked in permanently by this seed script.

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
   4. Map the bootstrap admin group to the bootstrap Admin role, scoped to
      the WindowsIntegrated provider (D-03, D-04, D-05, D-13, D-14).

      Defaults to BUILTIN\Administrators (S-1-5-32-544) -- see this file's
      header comment. GroupIdentifierExtractor.GetGroupIdentifiers reads
      SIDs straight off the Windows access token for WindowsIntegrated, so
      IdentityGroupName here must be the SID, not the display name
      "BUILTIN\Administrators" (that string never appears on the token).
      Change @AdminGroupName below to a real AD group's SID whenever you're
      ready to move off the bootstrap default -- or just add/change the
      mapping later via the admin UI instead of re-running this file.
   ============================================================================ */
DECLARE @AdminGroupName NVARCHAR(300) = 'S-1-5-32-544';
DECLARE @WinIntProviderKey INT = (SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'WindowsIntegrated');
DECLARE @AdminRoleKeyForMapping INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

IF NOT EXISTS (
    SELECT 1 FROM web.identity_group_role_map
    WHERE ProviderKey = @WinIntProviderKey AND IdentityGroupName = @AdminGroupName AND AppRoleKey = @AdminRoleKeyForMapping
)
BEGIN
    INSERT INTO web.identity_group_role_map (ProviderKey, IdentityGroupName, AppRoleKey)
    VALUES (@WinIntProviderKey, @AdminGroupName, @AdminRoleKeyForMapping);
END
GO


PRINT 'BlueTrack web interface seed data applied.';
