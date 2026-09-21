/* ============================================================================
   31_BlueTrack_MultiDomainLdapConfig.sql

   RUN THIS AFTER 01-30. Guarded (column/constraint-exists checks) -- safe to
   re-run, per D-58's normal incremental-script convention.

   Requested directly (2026-09-16), building the AD Account Discovery
   feature: web.ldap_config (D-116) was a true singleton -- one row, no
   domain-name column at all, LdapConfigRepository.GetAsync() a bare
   QuerySingleAsync with no WHERE clause. The discovery feature needs to
   query potentially multiple AD domains/forests, each with its own domain
   controller/search base/bind credential, so this can no longer be a
   singleton.

   Additive, not a drop/recreate: this table may already hold a real,
   admin-configured row in a live environment (D-58). DomainName gets a
   default of 'Default' so the existing singleton row keeps working
   unchanged -- it just becomes "the row for the default domain" instead of
   "the only row." New domains are added as additional rows via the updated
   admin UI, not by editing this seed.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.ldap_config', 'DomainName') IS NULL
BEGIN
    ALTER TABLE web.ldap_config ADD DomainName NVARCHAR(100) NOT NULL CONSTRAINT DF_ldap_config_DomainName DEFAULT 'Default';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE type = 'UQ' AND parent_object_id = OBJECT_ID('web.ldap_config') AND name = 'UQ_ldap_config_DomainName'
)
BEGIN
    ALTER TABLE web.ldap_config ADD CONSTRAINT UQ_ldap_config_DomainName UNIQUE (DomainName);
END
GO

PRINT '31_BlueTrack_MultiDomainLdapConfig.sql complete.';
