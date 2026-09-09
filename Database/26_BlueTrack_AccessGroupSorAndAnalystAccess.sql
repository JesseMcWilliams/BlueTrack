/* ============================================================================
   26_BlueTrack_AccessGroupSorAndAnalystAccess.sql

   RUN THIS AFTER 01-25. Guarded (`IF OBJECT_ID(...) IS NULL` for the new
   table, column-exists checks for the new columns on an existing table) --
   safe to re-run, per D-58's normal incremental-script convention.

   D-121: Targets/Access Groups are being promoted from Admin-only pages to
   their own top-level nav entries, with Analyst granted full parity with
   Admin on ManageTargets/ManageAccessGroups (confirmed directly -- same
   access Admin already has, including the two admin-only sub-pages that
   stay put, Target Match Review and Import Mapping Profiles, which Analyst
   now reaches too as an expected side effect, not a bug). Alongside that,
   two new Access Group fields: SOR Type (a new controlled dimension table,
   following web.dim_target_identifier_type's style but simpler -- no
   MatchPriority/RequiresReview) and SOR Address (plain free text naming
   where the group's Source of Record actually lives, e.g. 'company.com').
   Both are additive alongside the existing GroupScope/FoundOnTargetKey
   (D-119) -- those two stay exactly as they are.
   ============================================================================ */

USE $DatabaseName$;
GO

-- web.dim_sor_type: the controlled, extensible list of where an Access
-- Group's Source of Record lives (Domain/Local/App) -- an admin can add
-- more later, a new seeded row here rather than a schema change (same
-- "curated reference data gets a dimension table, never free text"
-- convention as web.dim_target_identifier_type, minus the match-priority/
-- review columns that table needs and this one doesn't).
IF OBJECT_ID('web.dim_sor_type', 'U') IS NULL
BEGIN
    CREATE TABLE web.dim_sor_type (
        SorTypeKey   INT IDENTITY(1,1) PRIMARY KEY,
        SorTypeName  NVARCHAR(50) NOT NULL UNIQUE
    );

    INSERT INTO web.dim_sor_type (SorTypeName) VALUES
        ('Domain'),
        ('Local'),
        ('App');
END
GO

-- web.dim_access_group already has real, populated rows in the live
-- BlueTrack database -- an additive ALTER TABLE, not a drop-and-recreate
-- (that convention is for brand-new table definitions only). Column-exists
-- guard style matches 18_BlueTrack_SmtpTlsOverridesSchema.sql (D-116
-- follow-up), the established precedent for adding columns to an existing
-- populated table.
IF COL_LENGTH('web.dim_access_group', 'SorTypeKey') IS NULL
BEGIN
    ALTER TABLE web.dim_access_group ADD SorTypeKey INT NULL REFERENCES web.dim_sor_type(SorTypeKey);
END
GO

IF COL_LENGTH('web.dim_access_group', 'SorAddress') IS NULL
BEGIN
    ALTER TABLE web.dim_access_group ADD SorAddress NVARCHAR(300) NULL;
END
GO

-- Grant the existing ManageTargets/ManageAccessGroups permissions to the
-- Analyst role -- same explicit catalog-grant guard style
-- 20_BlueTrack_RiskScoringSchema.sql's own Admin grants already use, just
-- targeting Analyst's RoleKey instead of Admin's, and for permissions that
-- already exist rather than newly-created ones.
DECLARE @AnalystRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Analyst');

DECLARE @ManageTargetsKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageTargets');
IF @AnalystRoleKey IS NOT NULL AND @ManageTargetsKey IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AnalystRoleKey AND PermissionKey = @ManageTargetsKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AnalystRoleKey, @ManageTargetsKey);
END
GO

DECLARE @AnalystRoleKey2 INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Analyst');
DECLARE @ManageAccessGroupsKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageAccessGroups');
IF @AnalystRoleKey2 IS NOT NULL AND @ManageAccessGroupsKey IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AnalystRoleKey2 AND PermissionKey = @ManageAccessGroupsKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AnalystRoleKey2, @ManageAccessGroupsKey);
END
GO

PRINT '26_BlueTrack_AccessGroupSorAndAnalystAccess.sql complete.';
