/* ============================================================================
   65_BlueTrack_LdapIntegrationFlag.sql

   RUN THIS AFTER 01-64. Guarded -- safe to re-run.

   - D-328: an LDAP domain can be a discovery place only. UseForIntegration
     says whether BlueTrack's own lookups use the domain (notification
     recipients, Reload Rights' AD read, the Safe groups' AD members); with
     it off, only AD account discovery reads the domain (UseAsDataSource,
     script 62). Every domain there before this script keeps both (default 1).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.ldap_config', 'UseForIntegration') IS NULL
    ALTER TABLE web.ldap_config ADD
        UseForIntegration BIT NOT NULL CONSTRAINT DF_ldap_config_UseForIntegration DEFAULT 1;
GO

PRINT '65_BlueTrack_LdapIntegrationFlag.sql complete.';
GO
