/* ============================================================================
   19_BlueTrack_LdapTrustedConnectionSchema.sql

   RUN THIS AFTER 01-18. Guarded (column-exists check) -- safe to re-run,
   per D-58's normal incremental-script convention.

   Requested directly (2026-09-08): LDAP should be able to bind using a
   trusted connection (the app pool's own Windows identity, or the local
   computer account) instead of always requiring an explicit bind-account
   web.credential. Confirmed feasible by a live probe against this
   environment's real domain before building this: System.DirectoryServices.
   AccountManagement's PrincipalContext, given no explicit username/password,
   binds as whatever identity the current process is running under and can
   expand real group membership -- exactly the app-pool-identity case this
   column enables. UseTrustedConnection defaults to 0 (unchanged behavior --
   an explicit bind-account CredentialKey, D-116's original shape) so
   existing LDAP configuration (currently disabled in every real environment
   anyway) isn't silently altered.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.ldap_config', 'UseTrustedConnection') IS NULL
BEGIN
    ALTER TABLE web.ldap_config ADD UseTrustedConnection BIT NOT NULL CONSTRAINT DF_ldap_config_UseTrustedConnection DEFAULT 0;
END
GO

PRINT '19_BlueTrack_LdapTrustedConnectionSchema.sql complete.';
