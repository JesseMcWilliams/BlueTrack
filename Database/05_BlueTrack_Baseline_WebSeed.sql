/* ============================================================================
   05_BlueTrack_Baseline_WebSeed.sql

   BASELINE (D-195, 2026-10-09). One of the six scripts that replaced the
   numbered scripts 01-51 before the first release. It builds the schema those
   scripts left behind, in final form: later columns and constraints are part
   of each CREATE TABLE, and each procedure, view and function appears once,
   as its last version. The history of every change is in git and in
   Claude_Docs/Design_Decision-Register.md.

   Runs only against an EMPTY database (App/Migrator gives it one). A database
   built from the old scripts has these baseline scripts marked as applied by
   App/Migrator instead of running them (see its header).

   The web interface's starting data: the WindowsIntegrated provider (plus
   disabled DevFakeAuth, OIDC and SAML placeholders), the bootstrap Admin role
   with every permission, mapped to BUILTIN\Administrators (S-1-5-32-544), the
   confirmed default roles (Viewer, Analyst, Approver, Auditor) and their
   permissions, the Account Progress field metadata, and the data feeds'
   system user. Change the Admin mapping on the Group Role Mappings page,
   not here. To map a local user to Admin through DevFakeAuth, see
   Database/Manual/06_BlueTrack_DevFakeAuthUserMapping.sql.
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

   Design_Authorization-Model.md's example bundles (Viewer/Analyst/Approver/
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



/* ============================================================================
   1. The four roles themselves.
   ============================================================================ */
INSERT INTO web.app_role (RoleName, Description)
SELECT v.RoleName, v.Description
FROM (VALUES
    ('Viewer', 'Read-only access to the dashboard.'),
    ('Analyst', 'Viewer plus editing Account Progress records.'),
    ('Approver', 'Analyst plus approving Risk Exceptions and confirming reconciliation matches.'),
    ('Auditor', 'Read-only access to the Audit Log Viewer only -- no dashboard/account-progress access implied.')
) AS v(RoleName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.app_role r WHERE r.RoleName = v.RoleName);
GO



/* ============================================================================
   2. Their permission bundles (Design_Authorization-Model.md's Example
      Permission Bundles table, confirmed as literal default rows).
   ============================================================================ */
DECLARE @ViewerRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Viewer');
DECLARE @AnalystRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Analyst');
DECLARE @ApproverRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Approver');
DECLARE @AuditorRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Auditor');

INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT v.RoleKey, p.PermissionKey
FROM (VALUES
    (@ViewerRoleKey, 'ViewDashboard'),
    (@AnalystRoleKey, 'ViewDashboard'),
    (@AnalystRoleKey, 'EditAccountProgress'),
    (@AnalystRoleKey, 'ManageTargets'),         -- D-121: Analyst has Admin's access to Targets
    (@AnalystRoleKey, 'ManageAccessGroups'),    -- and Access Groups
    (@ApproverRoleKey, 'ViewDashboard'),
    (@ApproverRoleKey, 'EditAccountProgress'),
    (@ApproverRoleKey, 'ConfirmReconciliation'),
    (@ApproverRoleKey, 'ApproveExceptions'),
    (@AuditorRoleKey, 'ViewAuditLog')
) AS v(RoleKey, PermissionName)
JOIN web.app_permission p ON p.PermissionName = v.PermissionName
WHERE NOT EXISTS (
    SELECT 1 FROM web.role_permission rp
    WHERE rp.RoleKey = v.RoleKey AND rp.PermissionKey = p.PermissionKey
);
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


IF NOT EXISTS (SELECT 1 FROM web.identity_provider_config WHERE ProviderType = 'OIDC')
BEGIN
    INSERT INTO web.identity_provider_config (ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues, SecretReference)
    VALUES (
        'OIDC',
        'Microsoft Entra ID',
        0,
        2,
        N'{"authority":"","clientId":"","callbackPath":"/signin-oidc","groupsClaimType":"groups"}',
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
        N'{"spEntityId":"","spCertificateThumbprint":"","idpEntityId":"","idpSingleSignOnDestination":"","idpSingleLogoutDestination":"","idpCertificateThumbprint":"","groupClaimType":"http://schemas.xmlsoap.org/claims/Group"}',
        NULL
    );
END
GO


INSERT INTO web.account_progress_field_metadata
    (FieldName, DisplayLabel, FieldType, ReferenceTable, IsRequired, RequiredPermission, DisplayOrder)
SELECT v.FieldName, v.DisplayLabel, v.FieldType, v.ReferenceTable, v.IsRequired, NULL, v.DisplayOrder
FROM (VALUES
    ('CurrentStageKey',        'Blueprint Stage',        'Dropdown', 'dim_blueprint_stage',   1, 10),
    ('CurrentStatusKey',       'Status',                  'Dropdown', 'dim_progress_status',   1, 20),
    ('RiskLevelKey',           'Risk Level',               'Dropdown', 'dim_risk_level',        0, 30),
    ('AccountTypeKey',         'Account Type',              'Dropdown', 'dim_account_type',      0, 40),
    ('SORKey',                 'Source of Record',           'Dropdown', 'dim_source_of_record',  0, 50),
    ('OwnerName',              'Owner Name',                   'Text',      NULL,                    0, 60),
    ('BusinessUnit',           'Business Unit',                 'Text',      NULL,                    0, 70),
    ('TargetRemediationDate',  'Target Remediation Date',        'Date',      NULL,                    0, 80),
    ('ActualCompletionDate',   'Actual Completion Date',           'Date',      NULL,                    0, 90),
    ('Notes',                  'Notes',                              'TextArea',  NULL,                    0, 100)
) AS v(FieldName, DisplayLabel, FieldType, ReferenceTable, IsRequired, DisplayOrder)
WHERE NOT EXISTS (
    SELECT 1 FROM web.account_progress_field_metadata fm WHERE fm.FieldName = v.FieldName
);

PRINT 'account_progress_field_metadata seeded with Account Progress editable fields.';
GO

-- The data feeds' system user (D-181): scheduled feed runs are attributed to
-- it. ExternalIdentifier S-1-0-0 is Windows' NULL SID, which no real account
-- has, so nobody can sign in as it.
DECLARE @WinIntProviderKey INT = (SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'WindowsIntegrated');
INSERT INTO web.app_user (ProviderKey, ExternalIdentifier, DisplayName)
VALUES (@WinIntProviderKey, 'S-1-0-0', 'BlueTrack Data Feeds (system)');
GO

PRINT '05_BlueTrack_Baseline_WebSeed.sql complete.';
GO
