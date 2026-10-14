# BlueTrack: Backlog

> **Stage: Planning.** Work that isn't built or proven yet. Carried forward from the 2026-09-16 survey (`Archive_Planning_Outstanding-Work-Survey-2026-09-16.md`); items that survey listed but the audit tracker records as resolved were left in the archive. Items tagged *(verify)* may already be done; confirm before starting. When an item ships, delete it here and record it in the relevant `Design_` doc.

## Requested, not started
Nothing at the moment.

## From the audit findings (moved here 2026-10-13, at the user's request)
- **Validate LDAPS against a remote DC** (Low): BlueTrack's `UseSsl` bind can't be proven on the dev host, which is the DC itself (binds from a DC to its own LDAPS fail; Authentik's LDAPS sync from another machine works). Run the check from DCACYBSQL01 (another domain): resolve and reach the DC on 636, trust its LDAPS certificate, bind with an account in the DC's domain (no trust between the domains, so not a trusted connection), then let AD Account Discovery run.
- **`ApplicationPoolIdentity` can't reach a SQL Server on another machine** (found 2026-09-24): `Login failed for user 'DOMAIN\HOSTNAME$'` although the login exists with the right SID. A known limitation of `ApplicationPoolIdentity` for outbound Windows authentication. Needs an environment decision: a dedicated domain account or gMSA for the App Pool (the Installation guide covers the gMSA).
- **CI runs approved pull-request code on this host** (Medium, accepted for now): the repo is public with a self-hosted runner. Mitigated: approval required for outside contributors, `permissions: contents: read`, `actions/checkout` pinned. Remaining: review a PR's changes (`ci.yml`, `package.json`, tests, scripts) before approving its run; making the repository private was declined.
- **Admin-only troubleshooting endpoints return exception text** (Info, kept on purpose): backup, credential test, SMTP test, feed test; that text is how an admin diagnoses the failure. Revisit only if those pages are opened to non-admins.

## Built, but never proven against a real live service
- **Azure Key Vault, AWS Secrets Manager, CyberArk Conjur** secrets backends: tested only against unreachable placeholder endpoints (error handling, not a real secret round-trip). CyberArk CP, CCP and Windows DPAPI are verified live. The 2026-10-12 review's hardening is done (D-215: User-scope DPAPI for their stored credentials, explicit Azure/AWS timeouts, Conjur's Base64 token header as CyberArk's own .NET client sends it); each still needs a real round trip before use.
- **SAML and OIDC**: proven end to end on the dev host against the lab Authentik on 2026-10-13: `/login/saml` and `/login/oidc` -> Authentik sign-in -> ACS / `signin-oidc` -> a BlueTrack session (account `AI_Admin_User`, which has no BlueTrack group, so no roles). Still for the user to check: a sign-in with a mapped user (`adm.jesse`) and Authentik's MFA prompt. SAML Single Logout built and proven against the same Authentik (D-217).
- **Installer on a fresh server**: `Db.AppPoolAccess` (D-171) and `Iis.Reset` (D-175) have only run by hand or with `-WhatIf`; the new `Iis.SiteRestart` (D-188) ran on the dev host only. DCACYBSQL01's app pool SQL login was granted by hand. Check all three on the next real install.
- **AD Account Discovery multi-domain**: tested with a fake two-domain directory (D-214, `AdAccountDiscoveryMultiDomainTests`); still unproven against a real second domain (DCACYBSQL01's domain could be one, once LDAPS works).

## Waiting on real external data or specs
- Self-Hosted CyberArk pivot logic uses **placeholder property names**, unconfirmed against real `stg_sh_objectproperties` data.
- The direct Account→Target **ETL feed's file shape** is undefined; its source hasn't been specified.
- `dbo.stg_discovered_accounts`: a content-free stub, effectively superseded by `web.discovered_account`. Candidate for removal.

## Process and tooling
- Playwright E2E has a known recurring flaky-proxy issue, accepted as a limitation (see `Reference_Lessons-Learned.md`).
- No automated versioning: the API's `<Version>` is bumped by hand.
- Target Match Review's "Merged" resolution and the CSV import-mapping UX are plain manual forms, with no typeahead or header auto-detect.
