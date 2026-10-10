/* ============================================================================
   50_BlueTrack_AccessGroupUniquenessFix.sql

   RUN THIS AFTER 01-49. Guarded (every step checks first) -- safe to re-run.

   D-122, rebuilt 2026-10-09 from the never-merged branch
   fix/access-group-duplicate-sid (its script 27, applied by hand to the dev
   host's BlueTrack database in 2026-09; numbers 27 and 28 stay unused on
   main). On a database that already has it, every step below is skipped.

   A well-known Local group such as BUILTIN\Administrators has the same SID
   (S-1-5-32-544) on every server, but 20_BlueTrack_RiskScoringSchema.sql
   made GroupIdentifier unique on its own -- so the same local group couldn't
   be recorded for a second server ("nothing happens" when adding it).

   Uniqueness becomes (GroupName, GroupIdentifier, FoundOnTargetKey): the same
   SID is fine across different servers (different FoundOnTargetKey), but not
   twice on the same one. Plus a new system-generated InternalGuid, mirroring
   web.dim_target's InternalGuid column/constraint exactly (same type,
   same DEFAULT NEWID(), same per-table uniqueness) -- a stable internal
   identity for the row, independent of any external identifier.
   ============================================================================ */

USE $DatabaseName$;
GO

-- Drop the old GroupIdentifier-only uniqueness -- the actual root cause of
-- the reported bug. Name matches the CONSTRAINT clause in
-- 20_BlueTrack_RiskScoringSchema.sql exactly.
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_dim_access_group' AND parent_object_id = OBJECT_ID('web.dim_access_group'))
BEGIN
    ALTER TABLE web.dim_access_group DROP CONSTRAINT UQ_dim_access_group;
END
GO

-- InternalGuid: mirrors web.dim_target's InternalGuid (schema/constraint
-- name pattern) -- a stable, system-generated identity for the row,
-- separate from any real-world identifier that may legitimately repeat
-- (like a Local group's SID across servers).
IF COL_LENGTH('web.dim_access_group', 'InternalGuid') IS NULL
BEGIN
    ALTER TABLE web.dim_access_group ADD InternalGuid UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID();
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_dim_access_group_InternalGuid' AND parent_object_id = OBJECT_ID('web.dim_access_group'))
BEGIN
    ALTER TABLE web.dim_access_group ADD CONSTRAINT UQ_dim_access_group_InternalGuid UNIQUE (InternalGuid);
END
GO

-- FoundOnTargetKey is NULL for Domain-scope groups -- a plain composite
-- UNIQUE constraint would treat every NULL as distinct from every other
-- NULL (standard SQL Server behavior), which would silently let a Domain
-- group be duplicated (same Name+Identifier, both NULL FoundOnTargetKey).
-- A persisted computed column with ISNULL(..., -1) makes the DB-level
-- constraint NULL-safe too, matching the app-layer check in
-- AccessGroupRepository.EnsureNoDuplicateAsync (belt-and-suspenders, same as this app's other
-- governed-data constraints).
IF COL_LENGTH('web.dim_access_group', 'FoundOnTargetKeyForUniqueness') IS NULL
BEGIN
    ALTER TABLE web.dim_access_group ADD FoundOnTargetKeyForUniqueness AS (ISNULL(FoundOnTargetKey, -1)) PERSISTED;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_dim_access_group_NameIdentifierFoundOn' AND parent_object_id = OBJECT_ID('web.dim_access_group'))
BEGIN
    ALTER TABLE web.dim_access_group ADD CONSTRAINT UQ_dim_access_group_NameIdentifierFoundOn UNIQUE (GroupName, GroupIdentifier, FoundOnTargetKeyForUniqueness);
END
GO

PRINT '50_BlueTrack_AccessGroupUniquenessFix.sql complete.';
