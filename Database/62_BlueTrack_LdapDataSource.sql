/* ============================================================================
   62_BlueTrack_LdapDataSource.sql

   RUN THIS AFTER 01-61. Guarded -- safe to re-run.

   - D-324: an LDAP domain (Admin > Credentials & LDAP) is used as a data
     source -- read by AD Account Discovery on Admin > Data Sources -- only
     when UseAsDataSource is set. Its other uses (notification recipients,
     Reload Rights, the Safe groups' AD members) don't look at it. Every
     domain there before this script keeps being read (default 1), as it was.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.ldap_config', 'UseAsDataSource') IS NULL
    ALTER TABLE web.ldap_config ADD
        UseAsDataSource BIT NOT NULL CONSTRAINT DF_ldap_config_UseAsDataSource DEFAULT 1;
GO

PRINT '62_BlueTrack_LdapDataSource.sql complete.';
GO
