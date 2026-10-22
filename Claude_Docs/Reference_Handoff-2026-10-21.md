# BlueTrack: Handoff, 2026-10-21

> **Stage: Reference (point-in-time snapshot).** Written at the end of the 2026-10-21 session for whoever picks the project up next (a person or another Claude session), and brought up to date after the session's second batch (D-289 to D-297). Supersedes `Archive_Reference_Handoff-2026-10-19.md` and `Archive_Planning_Handoff-2026-10-18.md`. Facts about the code live in the code and the other `Claude_Docs/` files; this note points at them.

## 1. Where things stand

- **Released: v1.5.0** (signed tag, GitHub release, rebuilt `Published_Docs`), tag on `7cddb67`. Earlier the same day: **v1.3.4** and **v1.4.0** (EVD removal, D-278).
- **`main`** is at `b63d0be` (PR #228), with everything below merged (PRs #219 to #228); **Unreleased** in `User_Docs/Release_Notes.md` lists it, including the upgrade steps. Not released yet.
- **Dev site** (`https://bluetrack.company.com`) was redeployed from `d4517ac` (PR #225; later merges are docs, tests and tools only), About `1.5.0-dev.15+d4517ac.dirty` (uncommitted docs in the tree at build time). Smoke test, `Tools/Test-BlueTrackHealth.ps1` (13 pass), `Tools/Test-BlueTrackSso.ps1` (SAML and OIDC sign-in and sign-out) and the new live Playwright checks (6 pass) all passed. Deployed with **`-InstallSqlServerAudit $false`**: `Db.SqlServerAudit` hasn't run anywhere real (section 3).
- **Data on the dev site:** the redeploy recreated the nightly job, so its history is empty; tonight (2026-10-22 02:00) is the first scheduled night on this build. The existing audit log was sealed in the background after the deploy.
- **Tests at the last full run:** API (xUnit) 1,381, web (Vitest) 861, E2E (Playwright) 101 (with `governance-and-formats.spec.js`); CI green on every PR.
- **Decision register:** the last row is **D-297**; next is D-298. **Database scripts:** the last is `55_BlueTrack_AnalystFieldAccess.sql`; next is `56`. Test seed `Database/Test/06` added.

## 2. What the 2026-10-21 session delivered

| Release | Piece | Decisions |
|---|---|---|
| 1.3.4, 1.4.0 | Self-Hosted Direct phases 3 to 5: the Load reads the REST fields; the EVD source and the shadow pull removed; the installer refuses a site still on the EVD; Upgrade Guide with a version table and a rehearsed backup/restore rollback | D-272 to D-278 |
| 1.5.0 | Resolve/Undo for Self-Hosted members; classic PVWA account details; AD users of Safe-member groups; CyberArk Applications; Progress Rules; editable Blueprint stages; rejected-sign-in logging and alerts, SAML certificate reminder; CPM health | D-279 to D-288 |
| Unreleased | The user's answers to the questions backlog and the requirements review | D-289, D-290 |
| Unreleased | Per-field permissions seeded, then corrected: Source of Record (Admin), Actual Completion Date (Admin, Approver) and Risk Level (Admin, Analyst) need their own permission; read-only on the account page, not offered in bulk edit | D-291, D-296 |
| Unreleased | **Remember each browser's last method** (Admin > Identity Providers) | D-292 |
| Unreleased | **Platform Mapping suggestions** (other source first, then keywords; group platforms skipped; Use / Fill in, saved only by Save) | D-293 |
| Unreleased | **Number and date formats**: dates and numbers on the pages in the viewer's format; **Number and date format** (Global Application Configuration, default en-US) for emails and run summaries | D-294 |
| Unreleased | **Tamper-evident audit log**: seals (HMAC chain), **Verify** on the Audit Log page, AuditChainBroken notification, purge keeps the chain whole; **SQL Server Audit** to the Windows Security log (installer step `Db.SqlServerAudit`, Manual/07) | D-295 |
| Unreleased | **CycloneDX SBOMs** in `Sbom/` (`Tools/New-BlueTrackSbom.ps1`, run by the release tool) | D-297 |
| (tests) | Playwright spec for the round; live read-only checks of a deployed site (`npm run test:live`, `App/E2E/live`) | |
| (docs) | `User_Docs/` updated for all of the above; the user-docs backlog keeps one row: the next release's Upgrade Guide section 5 row | |

**Changed on the dev site by request (data, not code):** Platform Mapping (58 platforms mapped; the group platforms and one test platform left unmapped on purpose); CpmHealth, SignInRejected and SamlCertificateExpiring turned on but nothing is sent (**no SMTP and no recipients**; on hold until a test mail environment exists, D-289); two draft Progress Rules saved **disabled** (the user said not to enable them).

## 3. Open items

1. **`Db.SqlServerAudit` not run for real.** It sets the Windows audit policy, grants the SQL Server account *Generate security audits* (`secedit`) and Security-log registry access, and needs a **SQL Server restart** before the audit writes. Tested only with `-WhatIf` and by running Manual/07 against `BlueTrackTest_Local` (objects created, `TARGET_CREATION_FAILED` as Windows wasn't prepared; removed again). Needs the user's go-ahead on this shared host (CI uses the same SQL Server).
2. **AuditChainBroken** notification is off (and there's no SMTP).
3. **Release 1.5.1** (releases bump the patch number only): write the Upgrade Guide section 5 row first (from Release Notes, Unreleased > Upgrading; `Planning_User-Docs-Backlog.md`), in its own PR; the release tool now also writes the SBOMs.
4. **Requirements review backlog** (`Planning_Backlog.md`): Reload Rights doesn't query the IdP or AD live (D-13); stale `Planning_API-Access-For-Scripts.md` (rename to `Design_API-Access.md`); PSM data for Privilege Cloud; suggestions (application-owner emails, least-privilege view, sponsor view).
5. **Not proven live:** Azure, AWS and Conjur secrets backends; every email (no SMTP); SIEM forwarding against a real SIEM; OAuth2 SMTP; LDAPS from another machine and a real second AD domain; identity provider tokens for API access; the AD group read and CyberArk Applications on a large Vault.
6. **Privilege Cloud Direct**; **nested AD group path** on "Via AD group"; three UI items left out on purpose (`Archive_Planning_UI-Workflow-Review.md`).
7. Intermittent E2E failures seen once each this session (nav links; the Audit Log export's Event Type filter still free text) passed on re-run; the second may have been load from the first big seal of the log, now paced (250 ms between full batches).

## 4. Conventions that are easy to get wrong

All are in `CLAUDE.md` or the docs it links; these bit during this work.

- **Branches and PRs:** never on `main`; `YYYY-MM-DD-<topic>`; `gh pr merge N --merge --delete-branch`. Commit, push, PR, merge, deploy and release only when the user asks; approval for one piece doesn't carry to the next.
- **Ask, don't assume:** design choices go to the user as numbered options; keep their numbers (they shifted the numbering once in a reply: match answers by content and say so). Writes to the dev site's data need a go-ahead; read-only SQL and PVWA GETs don't.
- **Docs per change** (`CLAUDE.md` table): D-row, the subsystem's `Design_` doc, `Database/README.md` for a script, a line under Unreleased; the Upgrade Guide's section 5 row before each release, in its own PR.
- **SQL:** never edit a script that has run anywhere real (CI's test database counts once merged: script 51 was corrected by a new script 55, not edited). Redefining a procedure copies its latest version; `usp_PurgeAuditLog` was last redefined in script 54, `usp_RunFullLoad` in 50.
- **Audit log:** the app's SQL account can't change or delete audit rows or seals (Manual/02). Tests that purge must seal first (`AuditChainService.SealAllAsync`). Tests that delete their own audit rows break the shared test database's chain, so chain tests verify from their own first seal and look only at their own events.
- **Server-written text** uses `ServerTextCulture` (D-294); audit details, IDs, dedup keys and CSV/Excel stay invariant. In the SPA, `formatDate` is for display only: date inputs need `yyyy-mm-dd`.
- **Stages:** use `dbo.fn_StartStageKey()` and `dbo.fn_MilestoneStageKey/Order(...)`, never a name or position (D-284). **Progress Rule fields** must agree in `fn_ProgressAccountFields`, `ProgressRuleFields` and `progressRuleLabels.js`.
- **Localization:** literal `t('...')` keys only, no unused keys, plurals `"one | many"` called as `t(key, { count: formatCount(n) }, n)`.
- **Live testing:** `Live-Testing.local.md` only for live work; never copy its details or real data values into tracked files.

## 5. How to work here (commands)

| Task | Command |
|---|---|
| API tests | `dotnet test App/Api.Tests` (one class: `--filter "FullyQualifiedName~Name"`); uses `BlueTrackTest_Local`: rebuild with `Tools/Reset-BlueTrackLocalTestDb.ps1 -Confirm:$false` (through `powershell -Command '& ./Tools/...'`) after a new or changed script. |
| Apply one script to the local test DB while iterating | replace `$DatabaseName$` with `BlueTrackTest_Local` and run `sqlcmd -S localhost -E -C -I -b -i <file>` with a Windows path (`cygpath -w`); rebuild the DB before committing. |
| Web / E2E | `npm run test` in `App/Web`; `npm test` in `App/E2E` (not while CI runs, including the run on `main` after a merge). |
| Live checks of the dev site | `BLUETRACK_LIVE_URL=https://<site> npm run test:live` in `App/E2E` (Windows sign-in as you; Verify adds one audit entry); `Tools/Test-BlueTrackHealth.ps1 -SiteUrl ...`; `Tools/Test-BlueTrackSso.ps1` with the CP parameters in `Live-Testing.local.md`. |
| Redeploy locally | A wrapper script running `& .\Deploy\Install-BlueTrack.ps1 -ConfigFile .\Deploy\State\answers.BlueTrack.json -InstallSqlServerAudit $false` (a `[bool]` can't be passed through `powershell -File`), **without `-Force`**. Drop the `-InstallSqlServerAudit` argument once the user decides item 3.1. |
| SBOMs | `Tools/New-BlueTrackSbom.ps1` (after package changes; the release tool runs it). |
| Release | Upgrade Guide row PR first; then `Tools/New-BlueTrackRelease.ps1 -Version X.Y.Z -Push`, merge, `... -Tag -Push`. |

## 6. Things that cost time this session

1. **Python edit scripts:** `"""` in C# or SQL ends a Python `"""` string; backslashes are escapes (`\N` broke one); backticks inside a bash heredoc broke the shell's parse once. Write the script with the Write tool, or use the Edit tool.
2. **CRLF files:** the working tree is CRLF (autocrlf); a Python `split('\n')` on a CRLF file leaves `\r`; read with `newline=''`, normalise, write back with the file's own ending (`crlfio.py` in the session scratchpad did this).
3. **`sqlcmd`** needs `-C`, and `-I` for QUOTED_IDENTIFIER. **`| Select-Object -First 1`** on a native command leaves a failing `$LASTEXITCODE` (`Reference_Lessons-Learned.md`).
4. **Playwright:** `getByLabel` on a `<label>` wrapping a `<select>` can match several elements or none; use `getByRole('combobox', { name, exact: true })`. A Vue page's table-cell text in a `<p>` isn't at the start of the line (`/^Last check/` failed). `page.request` doesn't trust a CA the browser trusts (live checks).
5. **The shared test database:** other suites delete audit rows, so "the chain is intact" can't be asserted globally; process-wide state (the format culture) mustn't be changed by a test.

## 7. Where to look

- Rules and folder map: `CLAUDE.md`. Architecture: `Claude_Docs/Design_Architecture.md`.
- Tamper evidence: `Claude_Docs/Design_Audit-Logging.md` ("Tamper evidence (D-295)"), `App/Api/Audit/AuditChain.cs`, `Database/54_*`, `Database/Manual/07_*`, `Deploy/Modules/BlueTrack.Database.psm1` (`Install-BlueTrackSqlServerAudit`).
- Formats: `App/Api/Formatting/ServerTextCulture.cs`, `App/Web/src/utils/formatDate.js`, `formatLocale.js`; `Design_Localization.md`.
- Platform Mapping suggestions: `App/Api/Data/PlatformMappingSuggester.cs`.
- Self-Hosted Direct: `Claude_Docs/Design_CyberArk-Direct-Integration.md`. Notifications: `Design_Notifications.md`. Upgrades: `User_Docs/Admin_UpgradeGuide.md`. SBOMs: `Design_Dependencies-And-External-Integrations.md` ("SBOMs").

## 8. Suggested next steps

The user decides the order.

1. Check tonight's first scheduled night on this build (Admin > Data Sources; Reports > CPM Health).
2. Decide on running `Db.SqlServerAudit` on the dev host (with a SQL Server restart at a quiet time), then run `npm run test:live` and `sys.dm_server_audit_status` to confirm it records.
3. Release the Unreleased work (Upgrade Guide row first).
4. Pick from the open items in section 3.
