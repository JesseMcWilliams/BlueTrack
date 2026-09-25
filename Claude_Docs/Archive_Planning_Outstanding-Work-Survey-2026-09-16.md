# Outstanding Work Survey

**Point-in-time snapshot, generated 2026-09-16.** Not a live-maintained tracker like `Testing_Audit-Findings.md` — re-run the same kind of review later if this goes stale rather than hand-editing it as a running log.
> **Archived.** Open items from this snapshot were carried forward to `Planning_Backlog.md` on 2026-09-25.


## Not yet merged

- **PR #27** (AD Account Discovery + the Accept/Dismiss onboarding workflow, `feature/ad-account-discovery`) is the only branch ahead of `main` right now — every other branch that existed has already merged.

## Real gaps — no mechanism exists yet

- **No rollback story for schema deployments.** The numbered `Database/*.sql` scripts have no down-scripts, and `Deploy/Install-BlueTrack.ps1` doesn't add one either. On this app's single-server topology (no redundancy to fail over to), a bad deploy's only recovery today is "restore the backup, redeploy the previous build." Explicitly flagged as still open in `Design_Deployment-Methodology.md`.
- **No segregation-of-duties on Risk Exception approval** — the same person can create a change and approve the exception excusing it. Design not started at all (`Testing_Audit-Findings.md`).
- **Nobody owns granting the `db_backupstatus_reader` msdb role** — needed for the Deployment page's backup-status check to actually work, but it's a DBA action nothing in this app can self-grant, and no process assigns who does it or when.

## Built, but never proven against a real live service

- **Azure Key Vault, AWS Secrets Manager, CyberArk Conjur** secrets backends — real SDK integrations, but each has only ever been tested against an unreachable placeholder endpoint (confirms error-handling, not a real secret round-trip). By contrast, CyberArk CP, CyberArk CCP, and Windows DPAPI *are* verified live.
- **SAML and OIDC** — real, working code, but pointed at placeholder IdP config since no real identity provider was ever available to test against. SAML Single Logout is explicitly not built at all.
- **Break-glass** — exists only as a *decision* (credential lives in CyberArk itself, a logon triggers an alert). There's no distinct implemented code path or controller for it anywhere — confirmed by a repo-wide search, not just an assumption.
- **`Deploy/Install-BlueTrack.ps1`'s destructive steps** (IIS site/App Pool/database creation) — reviewed and dry-run-traced, but never run end-to-end against a genuinely blank server.
- **AD Account Discovery's multi-domain support** — the code correctly loops over every enabled domain config, but only one real domain exists in this environment to test against, so true multi-domain behavior is unproven.

## Deliberately deferred (not gaps — confirmed choices)

- Bulk edit on Account Progress (D-52).
- Per-field permission granularity on the field-metadata system (D-20).
- Multi-select bulk actions across list-page rows — mentioned as a planned next feature (D-136) but nothing's built toward it yet.

## Waiting on real external data/specs

- The Self-Hosted CyberArk pivot logic uses **placeholder property names**, unconfirmed against real `stg_sh_objectproperties` data.
- The direct Account→Target **ETL feed's file shape** is still undefined — the external source it'd come from hasn't been specified.
- `dbo.stg_discovered_accounts` — an old, content-free stub for "some other account discovery source," never wired to a load procedure. Effectively superseded by the new `web.discovered_account` table the AD Account Discovery feature built, rather than something still waiting to be finished.

## Process/tooling

- CI (`ci.yml`) runs on every push/PR but **isn't a required check** — nothing stops a merge if tests fail.
- The `runs-on: [self-hosted, Windows, X64]` labels in `ci.yml` were never confirmed against the actual registered runner's labels.
- Playwright E2E has a **known, recurring flaky-proxy issue** — accepted as a real limitation, not chased past one retry, per established project convention.
- No automated versioning — the API's `<Version>` is bumped by hand.
- Target Match Review's "Merged" resolution and the CSV import-mapping UX are both plain manual forms — no autocomplete/typeahead or "auto-detect headers" experience exists anywhere in this app yet.

## Minor doc hygiene

- `Testing_Audit-Findings.md` itself is slightly stale — it still lists the environment-config-pattern gap as open, which `Deploy/Install-BlueTrack.ps1` actually resolved on 2026-09-11.
- `Design_Risk-Scoring.md` has grown large enough that it's flagged as a future split candidate.
- Decision Register entries vary a lot in length/detail — noted, not urgent.
