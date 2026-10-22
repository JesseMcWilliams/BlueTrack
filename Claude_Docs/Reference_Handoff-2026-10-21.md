# BlueTrack: Handoff, 2026-10-21

> **Stage: Reference (point-in-time snapshot).** Written at the end of the 2026-10-21 session for whoever picks the project up next (a person or another Claude session). Supersedes `Archive_Reference_Handoff-2026-10-19.md` and `Archive_Planning_Handoff-2026-10-18.md`. Facts about the code live in the code and the other `Claude_Docs/` files; this note points at them.

## 1. Where things stand

- **Released: v1.5.0** (signed tag verified by GitHub, GitHub release, rebuilt `Published_Docs`). `main` is at `7cddb67`, the merge of release PR #218, which is the tag's commit. Earlier the same day: **v1.3.4** and **v1.4.0** (EVD removal, D-278).
- **Dev site** (`https://bluetrack.company.com`) runs **1.5.0** built from that commit (About shows `1.5.0`). Health check passes; Windows, SAML and OIDC sign-in pass (`Tools/Test-BlueTrackHealth.ps1`, `Tools/Test-BlueTrackSso.ps1`). App pool identity is the gMSA (`Live-Testing.local.md`), unchanged by the deploys.
- **Data on the dev site:** a Self-Hosted Direct Pull now and the nightly job were run by hand at 17:20 on 2026-10-21 (import, load and snapshot succeeded). 2026-10-22 02:00 is the first scheduled night on 1.5.0: check Admin > Data Sources in the morning.
- **Tests at the last full run:** API (xUnit) 1,335, web (Vitest) 844, E2E (Playwright) 95, all passing; CI green on every PR (#214 to #218).
- **Decision register:** the last row is **D-290** (the user's answers, recorded after the release); D-291 to D-295 are reserved for the five pieces in section 3, item 1. **Database scripts:** the last is `50_BlueTrack_CpmHealth.sql`; next is `51`.
- **Unreleased** in `User_Docs/Release_Notes.md` is empty.

## 2. What the 2026-10-21 session delivered

| Release | Piece | Decisions |
|---|---|---|
| 1.3.4, 1.4.0 | Self-Hosted Direct phases 3 to 5: the Load reads the REST fields; the EVD source and the shadow pull removed; the installer refuses a site still on the EVD; Upgrade Guide (`User_Docs/Admin_UpgradeGuide.md`) with a version table and a rehearsed backup/restore rollback | D-272 to D-278 |
| 1.5.0 | Resolve and Undo for Self-Hosted members on Unresolved Entitlement Members | D-279 |
| 1.5.0 | Classic PVWA account details (device type, creation method, linked logon and reconcile accounts), read only for new or changed accounts (`categoryModificationTime`) and cached | D-280 |
| 1.5.0 | AD users of every Safe-member group, read in the nightly pull (or on their own at the pull time when Self-Hosted is off); "Who can use this account" on the account page | D-281 |
| 1.5.0 | CyberArk Applications (AppIDs) and their authentication methods (no hash values); Reports > CyberArk Applications | D-282 |
| 1.5.0 | **Progress Rules** (Admin > Progress Rules): conditions on 26 account fields move accounts from a stage to a later one each night; Test previews; the old fixed move to Onboarded is the seeded first rule | D-283 |
| 1.5.0 | **Editable Blueprint stages** (Admin > Blueprint Stages): rename, reorder, add, delete; start stage and KPI milestones replace lookups by name and position | D-284 |
| 1.5.0 | Rejected SAML/OIDC sign-ins logged (LogonFailed) and alerted (SignInRejected); step-up MFA left to the IdP; SAML certificate expiry reminder | D-285 to D-287 |
| 1.5.0 | **CPM health**: Reports > CPM Health, a CPM section on the account page, the CpmHealth digest; "Platform requires PSM" rule condition; one retry for a failed classic call | D-288 |
| (docs) | The 47-row user-docs backlog written into `User_Docs/` and fact-checked against the code; `Planning_User-Docs-Backlog.md` is empty | |

**Changed on the dev site by request (data, not code):**
1. Platform Mapping: the user approved Claude's proposed account type and source of record per platform; 58 platforms mapped, every account filled. The two sample group platforms (in each source) and one long-named test platform are left unmapped on purpose.
2. Notifications CpmHealth, SignInRejected and SamlCertificateExpiring turned on. Nothing is sent: **no SMTP host and no recipients**, and the user said not to set them up now.
3. Two draft Progress Rules saved **disabled** ("Managed by the CPM", "Verified and owned"); the user said not to enable them. Test showed few matches because most Self-Hosted accounts have never been verified by the CPM, so they have no CPM status.

## 3. Open items

1. **Chosen on 2026-10-21, being built in order** (D-289, D-290; `Planning_Backlog.md`, "Requested, not started"): seed per-field permissions (D-291), default sign-in provider setting (D-292), Platform Mapping suggestions (D-293), number and date formats (D-294), tamper-evident audit log (D-295).
2. **Requirements review backlog** (`Planning_Backlog.md`, "From the requirements review"): Reload Rights doesn't query the IdP or AD live (D-13); stale `Planning_API-Access-For-Scripts.md` (rename to `Design_API-Access.md`); PSM data for Privilege Cloud; suggestions (application-owner emails, least-privilege view, sponsor view). The questions backlog is answered (D-289).
3. **Not proven live:** Azure, AWS and Conjur secrets backends; break-glass and every other email (no SMTP); SIEM forwarding against a real SIEM; OAuth2 SMTP; LDAPS from another machine and a real second AD domain; option B (identity provider token for API access) against a real provider; the AD group read and CyberArk Applications on a large Vault.
4. **Privilege Cloud Direct** (the next CyberArk source; `Planning_Backlog.md`).
5. **Three UI items left out on purpose** in the UI review (`Archive_Planning_UI-Workflow-Review.md`, "Not built").
6. **Nested AD group path** on "Via AD group" (`Planning_Backlog.md`, D-281).

## 4. Conventions that are easy to get wrong

All are in `CLAUDE.md` or the docs it links; these bit during this work.

- **Branches and PRs:** never on `main`; `YYYY-MM-DD-<topic>`; `gh pr merge N --merge --delete-branch`. Commit, push, PR, merge, deploy and release only when the user asks; approval for one piece doesn't carry to the next.
- **Ask, don't assume:** design choices go to the user as numbered options (the user's global preference is numbered lists; keep their numbers). Writes to the dev site's data (pulls, loads, mappings, settings) need a go-ahead; read-only SQL and PVWA GETs don't.
- **Docs per change** (`CLAUDE.md` table): D-row, the subsystem's `Design_` doc, `Database/README.md` for a script, a line under Unreleased; the Upgrade Guide's section 5 gets a row before each release (do it in its own PR: the release tool needs a clean `main`).
- **SQL:** never edit a script that has run anywhere real (editing one that has only run on `BlueTrackTest_Local` is fine; rebuild that database). Redefining a procedure copies its latest version ("As in NN, plus ..."); the scripts here generated those copies from the source file instead of retyping them. `usp_RunFullLoad` was last redefined in script 50.
- **Stages:** nothing may look up a stage by name or fixed position any more. Use `dbo.fn_StartStageKey()`, `dbo.fn_MilestoneStageKey(N'Onboarded'|N'Managed'|N'Compliant')` and `dbo.fn_MilestoneStageOrder(...)` (D-284).
- **Progress Rule fields** live in two places that must agree: `dbo.fn_ProgressAccountFields` (SQL) and `ProgressRuleFields` (C#), plus a label in `App/Web/src/views/admin/progressRuleLabels.js`.
- **Localization:** literal `t('...')` keys only (no keys built at run time), no unused keys, plurals in the two-form `"one | many"` pattern.
- **After touching `App/Api/Auth/AuthenticationExtensions.cs`**, regression-test Windows, SAML and OIDC sign-in on the deployed site (done for 1.5.0).
- **Live testing:** `Live-Testing.local.md` only for live work; never copy its details or real data values (names of platforms, Safes, accounts) into tracked files.

## 5. How to work here (commands)

| Task | Command |
|---|---|
| API tests | `dotnet test App/Api.Tests` (one class: `--filter "FullyQualifiedName~Name"`); uses `BlueTrackTest_Local`: rebuild with `Tools/Reset-BlueTrackLocalTestDb.ps1 -Confirm:$false` after a new or changed script. |
| Apply one script to the local test DB while iterating | replace `$DatabaseName$` with `BlueTrackTest_Local` and run `sqlcmd -S localhost -E -C -b -i <file>` with a Windows path (`cygpath -w`); rebuild the DB before committing so the Migrator path is proven. |
| Web / E2E | `npm run test` in `App/Web`; `npm test` in `App/E2E` (not while CI runs). |
| Redeploy locally | `Deploy/Install-BlueTrack.ps1 -ConfigFile .\Deploy\State\answers.BlueTrack.json` from the repo root, **without `-Force`** (it recreates the app pool with the default identity). |
| Run tonight's job now | `EXEC dbo.usp_RunNightlyImportLoad` on `BlueTrack` (import, load, snapshot), after a Self-Hosted Direct pull. Needs the user's go-ahead. |
| Scripted calls to the live API | Windows auth (`-UseDefaultCredentials`); a POST, PUT or DELETE needs the header `X-Requested-With: XMLHttpRequest` (D-197), or it gets 403. |
| Release | Upgrade Guide row PR first; then `Tools/New-BlueTrackRelease.ps1 -Version X.Y.Z -Push`, merge, `... -Tag -Push`. |

## 6. Things that cost time this session

1. **Python edit scripts:** C# raw strings (`"""`) end a Python `"""` string, and backslashes in C#, SQL or paths (`\r`, `\U`, `Safe\Name`) are escapes. Write the edit script to a file with `'''` delimiters, or use the Edit tool for lines with backslashes. Scratchpad file names repeat across sessions: check before writing.
2. **CRLF files:** read with `newline=''`, normalise, write back with the file's own ending (a mixed file needs care); new files from the Write tool are LF, which git normalises.
3. **`sqlcmd` on this host** (ODBC 18) needs `-C` (trust the server certificate); its `QUOTED_IDENTIFIER` is off, so a filtered index fails to build. Don't use filtered indexes (they also tie every later write to that setting); enforce such rules in the API.
4. **`DATETIME2(0)` rounds** `SYSUTCDATETIME()` up to half a second into the future; a column compared with `notification_log.SentDate` must be full-precision `DATETIME2`.
5. **Test stubs called in parallel** (the pull reads 8 at a time) need a lock around shared lists, or counts come out short.
6. **PVWA's classic API** answered about 1 in 70 calls with a 302 under load and 200 a moment later; the pull now retries once. Searching by account name never found an account; user name plus address did.
7. **Hard-coded stage behaviour:** the old automatic move jumped from Discovered straight to Onboarded (skipping a stage), which is why a Progress Rule has a To stage, not only "next".
8. **The release tool** refuses a dirty or out-of-date `main`; doc fixes for the release go in their own PR first.

## 7. Where to look

- Rules and folder map: `CLAUDE.md`. Architecture: `Claude_Docs/Design_Architecture.md`.
- Self-Hosted Direct, including "Beyond REST" (classic details, applications, AD groups): `Claude_Docs/Design_CyberArk-Direct-Integration.md`.
- Progress Rules, stages and CPM health: D-283, D-284, D-288; `Database/49_*` and `50_*`; `App/Api/Data/ProgressRuleRepository.cs`, `App/Api/Controllers/ProgressRulesController.cs`, `App/Api/Data/CpmHealthRepository.cs`; pages `ProgressRules.vue`, `ProgressRuleEdit.vue`, `BlueprintStages.vue`, `reports/CpmHealth.vue`.
- Notifications: `Claude_Docs/Design_Notifications.md` (every type, its trigger and its dedup).
- Upgrades and rollback: `User_Docs/Admin_UpgradeGuide.md`.

## 8. Suggested next steps

None are urgent; the user decides the order.

1. Check the first scheduled 1.5.0 night (Admin > Data Sources; Reports > CPM Health).
2. When the CPM has verified more accounts, revisit the two disabled draft rules with **Test**.
3. Finish the five pieces in section 3, item 1, then pick from the other open items.
