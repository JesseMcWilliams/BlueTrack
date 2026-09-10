/* ============================================================================
   29_BlueTrack_TargetTypeDimension.sql

   RUN THIS AFTER 01-26. Guarded (`IF OBJECT_ID(...) IS NULL` for the new
   table, column-exists checks for the new/old columns on an existing
   table) -- safe to re-run, per D-58's normal incremental-script
   convention. Numbered 29, skipping 27/28 -- both reserved on other
   unmerged parallel branches (`fix/access-group-duplicate-sid`,
   `feature/target-generation-from-cyberark`).

   D-124 (Phase 2, the TargetType part of the list-page UX bundle):
   web.dim_target.TargetType was a free NVARCHAR(50) column with zero
   database enforcement -- no CHECK constraint, no FK, no lookup table, the
   allowed values enforced only by a hardcoded array in Targets.vue. Two
   real problems, both confirmed directly: (1) "LdapDirectory" needs a
   distinct "Active Directory" sibling -- a generic LDAP directory and AD
   specifically are both real, valid target types; (2) "LdapDirectory"
   displayed exactly like that (camelCase, no space) everywhere it's shown.
   Fixed here by a real web.dim_target_type (code + a separate DisplayName),
   matching the dim_sor_type/dim_target_identifier_type convention already
   established in this app, plus a one-time data migration onto the new key
   for every existing row -- including the 2,378 CyberArk-derived rows from
   D-123, all literally 'Other' (Database/28_BlueTrack_TargetsFromCyberArkAddress.sql,
   on that other unmerged branch, not present here).
   ============================================================================ */

USE $DatabaseName$;
GO

-- web.dim_target_type: the controlled, extensible list of what kind of
-- real-world destination a Target represents. Matches the established
-- dim_sor_type/dim_target_identifier_type pattern -- a code plus a
-- separate DisplayName, so a value can read correctly in the UI
-- ('LDAP Directory') without the underlying stored/matched value needing
-- to contain a space, and so a new type is a seeded row, not a schema change.
IF OBJECT_ID('web.dim_target_type', 'U') IS NULL
BEGIN
    CREATE TABLE web.dim_target_type (
        TargetTypeKey  INT IDENTITY(1,1) PRIMARY KEY,
        TypeCode       NVARCHAR(50)  NOT NULL UNIQUE,  -- stable code, e.g. 'LdapDirectory' -- never shown to a user directly
        DisplayName    NVARCHAR(100) NOT NULL          -- e.g. 'LDAP Directory' -- what the UI actually renders
    );

    INSERT INTO web.dim_target_type (TypeCode, DisplayName) VALUES
        ('Server', 'Server'),
        ('Desktop', 'Desktop'),
        ('Database', 'Database'),
        ('Application', 'Application'),
        ('LdapDirectory', 'LDAP Directory'),
        ('ActiveDirectory', 'Active Directory'),
        ('Appliance', 'Appliance'),
        ('Other', 'Other');
END
GO

-- web.dim_target already has real, populated rows in the live BlueTrack
-- database (2,378+ at last count) -- an additive ALTER TABLE, not a
-- drop-and-recreate (that convention is for brand-new table definitions
-- only). Column-exists guard style matches 18_BlueTrack_SmtpTlsOverridesSchema.sql/
-- 26_BlueTrack_AccessGroupSorAndAnalystAccess.sql's precedent for adding
-- columns to an existing, populated table.
IF COL_LENGTH('web.dim_target', 'TargetTypeKey') IS NULL
BEGIN
    ALTER TABLE web.dim_target ADD TargetTypeKey INT NULL REFERENCES web.dim_target_type(TargetTypeKey);
END
GO

-- Migrate every existing row onto the new key by matching its raw
-- TargetType string against dim_target_type's TypeCode -- covers all 7
-- values ever used in real data (including the 2,378 'Other' rows).
-- Wrapped in dynamic SQL (matching 07_BlueTrack_SourceImport.sql/
-- 08_BlueTrack_WebSchema.sql's own sp_executesql idiom elsewhere in this
-- project): once a later re-run has already dropped TargetType (step 4
-- below), an ad-hoc batch referencing that column would fail to compile
-- even inside a skipped IF branch -- unlike the plain DDL-only guards used
-- for the ADD/DROP COLUMN statements themselves.
IF COL_LENGTH('web.dim_target', 'TargetType') IS NOT NULL
BEGIN
    DECLARE @MigrateSql NVARCHAR(MAX) = N'
        UPDATE dt
        SET dt.TargetTypeKey = dtt.TargetTypeKey
        FROM web.dim_target dt
        JOIN web.dim_target_type dtt ON dtt.TypeCode = dt.TargetType
        WHERE dt.TargetTypeKey IS NULL;';
    EXEC sp_executesql @MigrateSql;
END
GO

-- Once every row is confirmed migrated, make the new column NOT NULL.
-- Guarded twice: only while it's still nullable (a no-op on a re-run after
-- this already happened), and only once zero rows are left unmapped -- if
-- some real TargetType value turned out to have no matching TypeCode, this
-- intentionally stays nullable rather than guessing at a mapping or
-- silently losing data.
IF COLUMNPROPERTY(OBJECT_ID('web.dim_target'), 'TargetTypeKey', 'AllowsNull') = 1
   AND NOT EXISTS (SELECT 1 FROM web.dim_target WHERE TargetTypeKey IS NULL)
BEGIN
    ALTER TABLE web.dim_target ALTER COLUMN TargetTypeKey INT NOT NULL;
END
GO

-- Drop the old free-text column, now fully superseded by the FK. Only once
-- TargetTypeKey is confirmed NOT NULL -- if any row is still unmapped,
-- TargetType is left in place rather than silently losing the only copy of
-- data no key could be resolved for.
IF COL_LENGTH('web.dim_target', 'TargetType') IS NOT NULL
   AND COLUMNPROPERTY(OBJECT_ID('web.dim_target'), 'TargetTypeKey', 'AllowsNull') = 0
BEGIN
    ALTER TABLE web.dim_target DROP COLUMN TargetType;
END
GO

PRINT '29_BlueTrack_TargetTypeDimension.sql complete.';
