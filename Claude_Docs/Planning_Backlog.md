# BlueTrack: Backlog

> **Stage: Planning.** Work that isn't built or proven yet. Carried forward from the 2026-09-16 survey (`Archive_Planning_Outstanding-Work-Survey-2026-09-16.md`); items that survey listed but the audit tracker records as resolved were left in the archive. Items tagged *(verify)* may already be done; confirm before starting. When an item ships, delete it here and record it in the relevant `Design_` doc.

## Requested, not started
- **Data Sources admin page** (requested 2026-10-08; design agreed 2026-10-09; phases 1-2 built 2026-10-09, phases 3-4 not started): scheduled CSV feeds over the existing import pipelines (groups, servers, applications, account links), an account inventory feed that adds other systems' accounts as Discovered with their source and links them to CyberArk accounts on an exact username + address match, a possible-match remediation report, and the CyberArk sources' settings editable on the page. Design, decisions and phasing: `Planning_Data-Sources.md`.

## Built, but never proven against a real live service
- **Azure Key Vault, AWS Secrets Manager, CyberArk Conjur** secrets backends: tested only against unreachable placeholder endpoints (error handling, not a real secret round-trip). CyberArk CP, CCP and Windows DPAPI are verified live.
- **SAML and OIDC**: working code, pointed only at placeholder IdP config. SAML Single Logout isn't built.
- **Break-glass**: exists only as a decision (credential in CyberArk, logon triggers an alert). No distinct code path exists.
- **Installer on a fresh server**: `Db.AppPoolAccess` (D-171) and `Iis.Reset` (D-175) have only run by hand or with `-WhatIf`; the new `Iis.SiteRestart` (D-188) ran on the dev host only. DCACYBSQL01's app pool SQL login was granted by hand. Check all three on the next real install.
- **The `/api/...` login loop** (D-172, D-175, D-188): `Iis.SiteRestart` is the current best fix, not yet seen to prevent a recurrence. If it comes back, capture Failed Request Tracing for `401` before changing anything (see `Reference_Lessons-Learned.md`).
- **AD Account Discovery multi-domain**: code loops over every enabled domain, but only one real domain exists to test against.

## Deliberately deferred (confirmed choices, not gaps)
- Per-field permission granularity on the field-metadata system (D-20).

## Waiting on real external data or specs
- Self-Hosted CyberArk pivot logic uses **placeholder property names**, unconfirmed against real `stg_sh_objectproperties` data.
- The direct Account→Target **ETL feed's file shape** is undefined; its source hasn't been specified.
- `dbo.stg_discovered_accounts`: a content-free stub, effectively superseded by `web.discovered_account`. Candidate for removal.

## Process and tooling
- **Turn on CI as a required check** (decided 2026-10-09): `main`'s branch protection should require the `ci.yml` `test` job, including for admins. Not set yet: it needs repository-admin access in GitHub.
- Playwright E2E has a known recurring flaky-proxy issue, accepted as a limitation (see `Reference_Lessons-Learned.md`).
- No automated versioning: the API's `<Version>` is bumped by hand.
- Target Match Review's "Merged" resolution and the CSV import-mapping UX are plain manual forms, with no typeahead or header auto-detect.
