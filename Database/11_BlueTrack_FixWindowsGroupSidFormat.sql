/* ============================================================================
   11_BlueTrack_FixWindowsGroupSidFormat.sql

   RUN THIS AFTER 01-10.

   D-69: identity_group_role_map.IdentityGroupName for the WindowsIntegrated
   provider must hold the raw Windows group SID (e.g. 'S-1-5-32-544' for
   BUILTIN\Administrators) -- matching Design_Authorization_Model.md's
   Claims Normalization Pipeline, which calls the raw Windows claim shape
   "Windows token SIDs," and App/Api/Auth/GroupIdentifierExtractor.cs, which
   reads SIDs straight off WindowsIdentity.Groups (no AD/LDAP translation
   call needed -- they're already on the access token).

   A row seeded earlier (07_BlueTrack_WebInterface_Seed.sql's step 4, with
   its placeholder replaced by hand) used the friendly name 'Administrators'
   instead of a SID -- same intended group (BUILTIN\Administrators,
   well-known SID S-1-5-32-544), wrong machine-readable form. This script
   corrects that row in place rather than deleting/re-inserting it, so its
   MappingKey is preserved.

   Guarded -- safe to re-run: only touches a row that still has the old
   'Administrators' value for the WindowsIntegrated provider.
   ============================================================================ */

USE $DatabaseName$;
GO

UPDATE igrm
SET IdentityGroupName = 'S-1-5-32-544'
FROM web.identity_group_role_map igrm
JOIN web.identity_provider_config ipc ON ipc.ProviderKey = igrm.ProviderKey
WHERE ipc.ProviderType = 'WindowsIntegrated'
  AND igrm.IdentityGroupName = 'Administrators';

PRINT 'identity_group_role_map: corrected WindowsIntegrated group identifier(s) from friendly name to SID (D-69).';
