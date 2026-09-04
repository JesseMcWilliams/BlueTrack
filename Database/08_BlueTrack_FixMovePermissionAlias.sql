/* ============================================================================
   08_BlueTrack_FixMovePermissionAlias.sql

   RUN THIS AFTER 01-07, against a database that already has real loaded
   data (staging + fact tables, web schema). Per D-58, this is a
   hand-written incremental script that only adds/corrects data -- it never
   drops or recreates anything, unlike 01.

   FIXES: permission_alias previously mapped both CAOMoveFrom and CAOMoveInto
   (two distinct raw Self-Hosted CAOwners flags) to the same canonical
   dim_permission entry, MoveAccountsAndFolders. Whenever a Self-Hosted safe
   owner had both flags granted (the common case), usp_Load_FactSafeEntitlement's
   permission unpivot produced the same (EntitlementKey, PermissionKey) pair
   twice in one INSERT, violating bridge_entitlement_permission's primary key.

   This script:
     1. Adds two new dim_permission entries (MoveAccountsFrom, MoveAccountsInto).
        Privilege Cloud's own MoveAccountsAndFolders entry is left untouched --
        PC only ever reports one combined flag, no from/into split -- so these
        two are Self-Hosted-only and not directly cross-source comparable to
        PC's combined flag.
     2. Repoints the two existing permission_alias rows at the new entries
        instead of MoveAccountsAndFolders.

   AFTER RUNNING THIS: re-run usp_Load_FactSafeEntitlement (or the full
   usp_RunFullLoad) -- it DELETEs and re-INSERTs fact_safe_entitlement and
   bridge_entitlement_permission for both sources on every run, so it will
   clean up and correctly reload whatever the earlier failed run left
   partially loaded.

   The same fix is also applied directly in 01_BlueTrack_CreateDatabase_Schema.sql
   so a future fresh deployment starts out correct without needing this file.
   ============================================================================ */

USE $DatabaseName$;
GO

IF NOT EXISTS (SELECT 1 FROM dim_permission WHERE PermissionName = 'MoveAccountsFrom')
BEGIN
    INSERT INTO dim_permission (PermissionName) VALUES ('MoveAccountsFrom');
END
IF NOT EXISTS (SELECT 1 FROM dim_permission WHERE PermissionName = 'MoveAccountsInto')
BEGIN
    INSERT INTO dim_permission (PermissionName) VALUES ('MoveAccountsInto');
END
GO

DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
DECLARE @MoveFromKey INT = (SELECT PermissionKey FROM dim_permission WHERE PermissionName = 'MoveAccountsFrom');
DECLARE @MoveIntoKey INT = (SELECT PermissionKey FROM dim_permission WHERE PermissionName = 'MoveAccountsInto');

UPDATE permission_alias
    SET PermissionKey = @MoveFromKey, Confidence = 'confirmed'
    WHERE SourceSystemKey = @SelfHostedKey AND RawPermissionName = 'CAOMoveFrom';

UPDATE permission_alias
    SET PermissionKey = @MoveIntoKey, Confidence = 'confirmed'
    WHERE SourceSystemKey = @SelfHostedKey AND RawPermissionName = 'CAOMoveInto';
GO

PRINT 'permission_alias corrected: CAOMoveFrom/CAOMoveInto now map to their own permissions. Re-run usp_Load_FactSafeEntitlement (or usp_RunFullLoad) to reload with the fix.';
