# BlueTrack: Backlog

> **Stage: Planning.** Work that isn't built or proven yet. Carried forward from the 2026-09-16 survey (`Archive_Planning_Outstanding-Work-Survey-2026-09-16.md`); items that survey listed but the audit tracker records as resolved were left in the archive. Items tagged *(verify)* may already be done; confirm before starting. When an item ships, delete it here and record it in the relevant `Design_` doc.

## Requested, not started
- **Data Sources admin page** (requested 2026-10-08; design agreed 2026-10-09; phases 1-2 built 2026-10-09, phase 3 2026-10-13 (D-211), phase 4 not started; its details settled in D-212): scheduled CSV feeds over the existing import pipelines (groups, servers, applications, account links), an account inventory feed that adds other systems' accounts as Discovered with their source and links them to CyberArk accounts on an exact username + address match, a possible-match remediation report, and the CyberArk sources' settings editable on the page. Design, decisions and phasing: `Planning_Data-Sources.md`.

- **SAML SP certificate requested by the App Pool account** (requested 2026-10-12): an installer step that has the App Pool's account request the SP signing certificate from the CA into its own store (`CurrentUser\My`), so only that account holds the key. BlueTrack already looks there first (D-205); the step itself isn't built. Needs the pool's Load User Profile, and a template the account may enroll in.

- **A field's Required Permission can't be set in the UI** (found 2026-10-13): it's enforced (D-198), but the Edit Field page doesn't show it, so it's set with SQL on `web.account_progress_field_metadata.RequiredPermission`.

## From the audit findings (moved here 2026-10-13, at the user's request)
- **Validate LDAPS against a remote DC** (Low): BlueTrack's `UseSsl` bind can't be proven on the dev host, which is the DC itself (binds from a DC to its own LDAPS fail; Authentik's LDAPS sync from another machine works). Run the check from DCACYBSQL01 (another domain): resolve and reach the DC on 636, trust its LDAPS certificate, bind with an account in the DC's domain (no trust between the domains, so not a trusted connection), then let AD Account Discovery run.
- **`ApplicationPoolIdentity` can't reach a SQL Server on another machine** (found 2026-09-24): `Login failed for user 'DOMAIN\HOSTNAME$'` although the login exists with the right SID. A known limitation of `ApplicationPoolIdentity` for outbound Windows authentication. Needs an environment decision: a dedicated domain account or gMSA for the App Pool (the Installation guide covers the gMSA).
- **CI runs approved pull-request code on this host** (Medium, accepted for now): the repo is public with a self-hosted runner. Mitigated: approval required for outside contributors, `permissions: contents: read`, `actions/checkout` pinned. Remaining: review a PR's changes (`ci.yml`, `package.json`, tests, scripts) before approving its run; making the repository private was declined.
- **Admin-only troubleshooting endpoints return exception text** (Info, kept on purpose): backup, credential test, SMTP test, feed test; that text is how an admin diagnoses the failure. Revisit only if those pages are opened to non-admins.

## Built, but never proven against a real live service
- **Azure Key Vault, AWS Secrets Manager, CyberArk Conjur** secrets backends: tested only against unreachable placeholder endpoints (error handling, not a real secret round-trip). CyberArk CP, CCP and Windows DPAPI are verified live. Before using them for real (from the 2026-10-12 review):
  - Their bootstrap credentials (`ProtectedCredential`) stay Machine-scope DPAPI with a constant entropy (`WindowsDpapiProtector.cs`), never upgraded to User scope as D-116 does for stored credentials, so any local process that can read `web.secrets_store` or a backup can decrypt them.
  - Azure and AWS build a new client per call with SDK default retries and timeouts, so one lookup can block for minutes; set explicit retry and timeout options.
  - Conjur may send its access token in the wrong format: `CyberArkConjurSecretsProvider` uses the raw authenticate response, while Conjur's docs call for it base64-encoded (or `Accept-Encoding: base64`). Unconfirmed; check against a real appliance.
- **SAML and OIDC**: proven end to end on the dev host against the lab Authentik on 2026-10-13: `/login/saml` and `/login/oidc` -> Authentik sign-in -> ACS / `signin-oidc` -> a BlueTrack session (account `AI_Admin_User`, which has no BlueTrack group, so no roles). Still for the user to check: a sign-in with a mapped user (`adm.jesse`) and Authentik's MFA prompt. SAML Single Logout isn't built.
- **Break-glass**: exists only as a decision (credential in CyberArk, logon triggers an alert). No distinct code path exists.
- **Installer on a fresh server**: `Db.AppPoolAccess` (D-171) and `Iis.Reset` (D-175) have only run by hand or with `-WhatIf`; the new `Iis.SiteRestart` (D-188) ran on the dev host only. DCACYBSQL01's app pool SQL login was granted by hand. Check all three on the next real install.
- **AD Account Discovery multi-domain**: code loops over every enabled domain, but only one real domain exists to test against.

## Waiting on real external data or specs
- Self-Hosted CyberArk pivot logic uses **placeholder property names**, unconfirmed against real `stg_sh_objectproperties` data.
- The direct Account→Target **ETL feed's file shape** is undefined; its source hasn't been specified.
- `dbo.stg_discovered_accounts`: a content-free stub, effectively superseded by `web.discovered_account`. Candidate for removal.

## Process and tooling
- Playwright E2E has a known recurring flaky-proxy issue, accepted as a limitation (see `Reference_Lessons-Learned.md`).
- No automated versioning: the API's `<Version>` is bumped by hand.
- Target Match Review's "Merged" resolution and the CSV import-mapping UX are plain manual forms, with no typeahead or header auto-detect.
