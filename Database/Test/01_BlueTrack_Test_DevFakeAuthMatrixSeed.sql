/* ============================================================================
   01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql

   Test-only seed data (Design_Testing_Strategy.md) -- NOT part of the real
   environment's numbered-script sequence (the numbered .sql files directly
   under Database/). Never run this against BlueTrack (or any real
   environment); it only belongs in a
   disposable database like BlueTrackTest.

   RUN THIS AFTER Database/01 through Database/22 (skipping 09, which is
   SQL Agent job scheduling and doesn't belong in a disposable database --
   see App/Migrator/Program.cs's skipScriptNames argument).

   WHAT THIS DOES: seeds the role/permission matrix that layers 3
   (API/contract tests) and 4 (Playwright E2E) need to exercise every
   permission boundary -- Design_Testing_Strategy.md's own call-out that
   the real 18_BlueTrack_DevFakeAuthSeed.sql "only seeds one disabled
   placeholder row, not the role/permission matrix (Viewer/Analyst/
   Approver/Admin, at minimum) this layer needs."

   Synthetic identities only (Design_Testing_Strategy.md's own principle):
   TestUser.Viewer / TestUser.Analyst / TestUser.Approver / TestUser.Admin,
   distinct from any real person's Windows account. These are matched via
   DevFakeAuth's own mechanism (App/Api/Auth/GroupIdentifierExtractor.cs's
   GetDevFakeAuthIdentifiers: ClaimsPrincipal.Identity.Name, nothing else),
   which doesn't actually require a real Negotiate/WindowsIdentity login --
   only that the authenticated principal's Identity.Name matches one of
   these rows and the request carries the bluetrack:provider_type=
   DevFakeAuth marker claim (see App/Api/Auth/NegotiateProviderResolver.cs).
   That's what lets both the in-process contract-test TestAuthHandler and
   the real-HTTP dev-only test sign-in endpoint mint any of these four
   identities without needing four real Windows accounts.

   Unlike 18_BlueTrack_DevFakeAuthSeed.sql (which leaves DevFakeAuth
   disabled by default -- a deliberate admin-screen action for real
   environments), this script enables it: a disposable test database has
   no other reason to exist.

   Permission bundles below are a test-fixture judgment call, not a
   business decision about real Viewer/Analyst/Approver/Admin permissions
   (Design_Authorization_Model.md's example bundles are still explicitly
   "illustrative... confirm before building as literal default rows" for
   any REAL environment) -- scoped entirely to BlueTrackTest, never copied
   to a real environment's role definitions.

   Guarded throughout -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO


/* ============================================================================
   1. Enable the DevFakeAuth identity provider.
   ============================================================================ */
IF NOT EXISTS (SELECT 1 FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth')
BEGIN
    INSERT INTO web.identity_provider_config (ProviderType, DisplayName, IsEnabled, DisplayOrder)
    VALUES ('DevFakeAuth', 'Dev Fake Auth (Development only)', 1, 99);
END
ELSE
BEGIN
    UPDATE web.identity_provider_config SET IsEnabled = 1 WHERE ProviderType = 'DevFakeAuth';
END
GO


/* ============================================================================
   2. Test-only roles: Viewer / Analyst / Approver, each a permission
      subset. Admin already exists (07_BlueTrack_WebInterface_Seed.sql)
      bundling every confirmed permission -- reused as-is below.
   ============================================================================ */
IF NOT EXISTS (SELECT 1 FROM web.app_role WHERE RoleName = 'Viewer')
BEGIN
    INSERT INTO web.app_role (RoleName, Description)
    VALUES ('Viewer', 'Test fixture role (BlueTrackTest only) -- read-only access.');
END
IF NOT EXISTS (SELECT 1 FROM web.app_role WHERE RoleName = 'Analyst')
BEGIN
    INSERT INTO web.app_role (RoleName, Description)
    VALUES ('Analyst', 'Test fixture role (BlueTrackTest only) -- read-only plus Account Progress editing.');
END
IF NOT EXISTS (SELECT 1 FROM web.app_role WHERE RoleName = 'Approver')
BEGIN
    INSERT INTO web.app_role (RoleName, Description)
    VALUES ('Approver', 'Test fixture role (BlueTrackTest only) -- Analyst plus Risk Exception approval.');
END
GO


/* ============================================================================
   3. Bundle permissions into each test role.
   ============================================================================ */
DECLARE @ViewerRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Viewer');
DECLARE @AnalystRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Analyst');
DECLARE @ApproverRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Approver');

INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT @ViewerRoleKey, p.PermissionKey
FROM web.app_permission p
WHERE p.PermissionName IN ('ViewDashboard', 'ViewAuditLog')
  AND NOT EXISTS (SELECT 1 FROM web.role_permission rp WHERE rp.RoleKey = @ViewerRoleKey AND rp.PermissionKey = p.PermissionKey);

INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT @AnalystRoleKey, p.PermissionKey
FROM web.app_permission p
WHERE p.PermissionName IN ('ViewDashboard', 'ViewAuditLog', 'EditAccountProgress')
  AND NOT EXISTS (SELECT 1 FROM web.role_permission rp WHERE rp.RoleKey = @AnalystRoleKey AND rp.PermissionKey = p.PermissionKey);

INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT @ApproverRoleKey, p.PermissionKey
FROM web.app_permission p
WHERE p.PermissionName IN ('ViewDashboard', 'ViewAuditLog', 'EditAccountProgress', 'ApproveExceptions')
  AND NOT EXISTS (SELECT 1 FROM web.role_permission rp WHERE rp.RoleKey = @ApproverRoleKey AND rp.PermissionKey = p.PermissionKey);
GO


/* ============================================================================
   4. Map synthetic TestUser.* identities to each role, scoped to the
      DevFakeAuth provider.
   ============================================================================ */
DECLARE @DevFakeAuthProviderKey INT = (SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth');
DECLARE @ViewerRoleKey2 INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Viewer');
DECLARE @AnalystRoleKey2 INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Analyst');
DECLARE @ApproverRoleKey2 INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Approver');
DECLARE @AdminRoleKey2 INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

INSERT INTO web.identity_group_role_map (ProviderKey, IdentityGroupName, AppRoleKey)
SELECT v.ProviderKey, v.IdentityGroupName, v.AppRoleKey
FROM (VALUES
    (@DevFakeAuthProviderKey, N'TestUser.Viewer', @ViewerRoleKey2),
    (@DevFakeAuthProviderKey, N'TestUser.Analyst', @AnalystRoleKey2),
    (@DevFakeAuthProviderKey, N'TestUser.Approver', @ApproverRoleKey2),
    (@DevFakeAuthProviderKey, N'TestUser.Admin', @AdminRoleKey2)
) AS v(ProviderKey, IdentityGroupName, AppRoleKey)
WHERE NOT EXISTS (
    SELECT 1 FROM web.identity_group_role_map m
    WHERE m.ProviderKey = v.ProviderKey AND m.IdentityGroupName = v.IdentityGroupName AND m.AppRoleKey = v.AppRoleKey
);
GO

PRINT 'DevFakeAuth test role/permission matrix seeded (TestUser.Viewer / .Analyst / .Approver / .Admin).';
