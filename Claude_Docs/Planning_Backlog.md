# BlueTrack: Backlog

> **Stage: Planning.** Work that isn't built or proven yet. Carried forward from the 2026-09-16 survey (`Archive_Planning_Outstanding-Work-Survey-2026-09-16.md`); items that survey listed but the audit tracker records as resolved were left in the archive. Items tagged *(verify)* may already be done; confirm before starting. When an item ships, delete it here and record it in the relevant `Design_` doc.

## Requested, not started
- **Data Sources admin page** (requested 2026-10-08; design agreed 2026-10-09; not started): scheduled CSV feeds over the existing import pipelines (groups, servers, applications, account links), an account inventory feed that adds other systems' accounts as Discovered with their source and links them to CyberArk accounts on an exact username + address match, a possible-match remediation report, and the CyberArk sources' settings editable on the page. Design, decisions and phasing: `Planning_Data-Sources.md`.

## Built, but never proven against a real live service
- **Azure Key Vault, AWS Secrets Manager, CyberArk Conjur** secrets backends: tested only against unreachable placeholder endpoints (error handling, not a real secret round-trip). CyberArk CP, CCP and Windows DPAPI are verified live.
- **SAML and OIDC**: working code, pointed only at placeholder IdP config. SAML Single Logout isn't built.
- **Break-glass**: exists only as a decision (credential in CyberArk, logon triggers an alert). No distinct code path exists.
- **AD Account Discovery multi-domain**: code loops over every enabled domain, but only one real domain exists to test against.

## Deliberately deferred (confirmed choices, not gaps)
- Bulk edit on Account Progress (D-52).
- Per-field permission granularity on the field-metadata system (D-20).
- Multi-select bulk actions across list-page rows (D-136). Nothing built yet.

## Waiting on real external data or specs
- Self-Hosted CyberArk pivot logic uses **placeholder property names**, unconfirmed against real `stg_sh_objectproperties` data.
- The direct Account→Target **ETL feed's file shape** is undefined; its source hasn't been specified.
- `dbo.stg_discovered_accounts`: a content-free stub, effectively superseded by `web.discovered_account`. Candidate for removal.

## Process and tooling
- CI (`ci.yml`) isn't a required check, so a merge isn't blocked by failing tests.
- `runs-on: [self-hosted, Windows, X64]` labels in `ci.yml` never confirmed against the registered runner *(verify)*.
- Playwright E2E has a known recurring flaky-proxy issue, accepted as a limitation (see `Reference_Lessons-Learned.md`).
- No automated versioning: the API's `<Version>` is bumped by hand.
- This dev host's lab IIS site (`bluetrack.company.com`) serves straight from `App\Web\dist`, which every SPA build empties, so it currently has no site-root `web.config` (no D-166 rewrite or SPA fallback). Re-run `Iis.WebConfig` there, and consider pointing the site at a separate folder. Deferred 2026-10-07.
- `Iis.Site` turns on Windows auth for `/BlueTrack` through `IIS:\Sites\<site>\BlueTrack`, which likely writes to the published API's own `web.config`; re-running `Build.Api` alone may then overwrite it and silently turn Windows sign-in off *(verify)*. If confirmed, write it to `applicationHost.config` like the site-root setting (D-172).
- Target Match Review's "Merged" resolution and the CSV import-mapping UX are plain manual forms, with no typeahead or header auto-detect.
