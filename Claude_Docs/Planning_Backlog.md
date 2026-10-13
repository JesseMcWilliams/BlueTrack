# BlueTrack: Backlog

> **Stage: Planning.** Work that isn't built or proven yet. Carried forward from the 2026-09-16 survey (`Archive_Planning_Outstanding-Work-Survey-2026-09-16.md`); items that survey listed but the audit tracker records as resolved were left in the archive. Items tagged *(verify)* may already be done; confirm before starting. When an item ships, delete it here and record it in the relevant `Design_` doc.

## Requested, not started
- **Data Sources admin page** (requested 2026-10-08; design agreed 2026-10-09; phases 1-2 built 2026-10-09, phases 3-4 not started): scheduled CSV feeds over the existing import pipelines (groups, servers, applications, account links), an account inventory feed that adds other systems' accounts as Discovered with their source and links them to CyberArk accounts on an exact username + address match, a possible-match remediation report, and the CyberArk sources' settings editable on the page. Design, decisions and phasing: `Planning_Data-Sources.md`.

- **SAML SP certificate requested by the App Pool account** (requested 2026-10-12): an installer step that has the App Pool's account request the SP signing certificate from the CA into its own store (`CurrentUser\My`), so only that account holds the key. BlueTrack already looks there first (D-205); the step itself isn't built. Needs the pool's Load User Profile, and a template the account may enroll in.

## Built, but never proven against a real live service
- **Azure Key Vault, AWS Secrets Manager, CyberArk Conjur** secrets backends: tested only against unreachable placeholder endpoints (error handling, not a real secret round-trip). CyberArk CP, CCP and Windows DPAPI are verified live. Before using them for real (from the 2026-10-12 review):
  - Their bootstrap credentials (`ProtectedCredential`) stay Machine-scope DPAPI with a constant entropy (`WindowsDpapiProtector.cs`), never upgraded to User scope as D-116 does for stored credentials, so any local process that can read `web.secrets_store` or a backup can decrypt them.
  - Azure and AWS build a new client per call with SDK default retries and timeouts, so one lookup can block for minutes; set explicit retry and timeout options.
  - Conjur may send its access token in the wrong format: `CyberArkConjurSecretsProvider` uses the raw authenticate response, while Conjur's docs call for it base64-encoded (or `Accept-Encoding: base64`). Unconfirmed; check against a real appliance.
- **SAML and OIDC**: configured on the dev host against the lab Authentik (2026-10-12). Both send a valid request that Authentik accepts and forwards to its sign-in page. A full browser sign-in with a test user, and the group-to-role result, still need to be checked. SAML Single Logout isn't built.
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
