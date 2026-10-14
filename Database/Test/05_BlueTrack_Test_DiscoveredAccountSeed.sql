/* ============================================================================
   05_BlueTrack_Test_DiscoveredAccountSeed.sql

   Test-only synthetic data (Design_Testing-Strategy.md) -- NOT part of the
   real environment's numbered-script sequence. Never run this against
   BlueTrack (or any real environment).

   RUN THIS AFTER 04_BlueTrack_Test_UnresolvedEntitlementSeed.sql (and after
   25_BlueTrack_AccountClassification.sql).

   WHAT THIS DOES (D-233): one discovered account for the Playwright account
   classification spec, as an AD Account Discovery run with classification
   on would leave it: e2eadm.jsmith in the domain e2e-classification.test
   (Status New, adminCount 1), and its owner jsmith (Jane Smith) read from AD
   only as an owner (web.ad_owner_lookup). The spec adds a name rule, sees the
   class and owner on the report, and removes the rule and restores the
   settings, so it ends as it started. No discovery run in the test suites
   reaches this domain (it has no web.ldap_config row), so the rows stay.

   Guarded -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO

IF NOT EXISTS (SELECT 1 FROM web.discovered_account WHERE DomainName = 'e2e-classification.test' AND SamAccountName = 'e2eadm.jsmith')
    INSERT INTO web.discovered_account (DomainName, SamAccountName, DistinguishedName, DisplayName, IsEnabled, UserPrincipalName, DirectoryAttributes, AttributesReadDate)
    VALUES ('e2e-classification.test', 'e2eadm.jsmith', 'CN=e2eadm.jsmith,OU=Admins,DC=e2e-classification,DC=test', 'Jane Smith (admin)', 1,
            'e2eadm.jsmith@e2e-classification.test', '{"adminCount":"1"}', SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM web.ad_owner_lookup WHERE DomainName = 'e2e-classification.test' AND SamAccountName = 'jsmith')
    INSERT INTO web.ad_owner_lookup (DomainName, SamAccountName, DistinguishedName, DisplayName, IsEnabled)
    VALUES ('e2e-classification.test', 'jsmith', 'CN=jsmith,OU=People,DC=e2e-classification,DC=test', 'Jane Smith', 1);
GO
