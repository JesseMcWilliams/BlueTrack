# BlueTrack: Handoff, 2026-11-05

> **Stage: Reference (point-in-time snapshot).** Written at the end of the 2026-11-04/05 session for whoever picks the project up next (a person or another Claude session). Supersedes `Archive_Reference_Handoff-2026-10-21.md`, which covers everything up to 1.5.1 and the 2026-11-03/04 rounds (D-289 to D-331) and is still the place for that history. Facts about the code live in the code and the other `Claude_Docs/` files; this note points at them.

## 1. Where things stand

- **Released: v1.5.2** (2026-11-05; signed tag on `fbd4679`, the merge of PR #279; GitHub verified the signature; GitHub release published; `Published_Docs` and `Sbom/` rebuilt). It holds everything through PR #278: D-298 to D-336. Release numbers go up by 0.0.1 only.
- **`main`** is at `812d60b` (PR #281). **Unreleased** on `main`: D-337 (#280) and D-338 to D-340 (#281). The next release is **1.5.3**; its Upgrade Guide section 5 row isn't written yet (it needs: scripts 68 and 69, the new installer step `Db.SessionActivity`, and that the installer now saves `PowerBiReaderAccount`).
- **Not committed (the user said "Not yet", 2026-11-05):** branch `2026-11-05-dna` in the worktree `.claude/worktrees/dna` (node_modules are junctions to the main checkout: remove them with `cmd /c rmdir` before `git worktree remove`, so the main checkout's packages survive). It holds D-341 to D-347 and the Accounts search fix (section 2b). All suites passed on it last: API 1,508, web 977, E2E 114. Its docs (register, `Database/README.md`, `Design_Data-Sources.md`, Release Notes, user-docs backlog, lessons learned) are written, and this handoff is on it too.
- **Dev site** (`https://bluetrack.company.com`) runs **`main` at #281** (`812d60b`), installed 2026-11-05 with the saved answers plus `-PowerBiReaderAccount 'Company\Power_BI_Reader'` (the account already in the role); the answers file now holds it. `Db.SessionActivity` signed `usp_Get_EtlSessionActivity` on a real install; the app pool's login still lacks VIEW SERVER STATE (checked with `EXECUTE AS LOGIN`). Smoke test healthy; `Test-BlueTrackHealth.ps1` 13 pass after #277's deploy.
- **Decision register:** last on `main` **D-340**; on the DNA branch **D-347**; next **D-348**. **Database scripts:** last on `main` `69`; on the branch `70` (not run anywhere real yet, so it can still change); next `71`. Manual scripts: `10` (`10_BlueTrack_SignSessionActivity.sql`, on `main`).

## 2. What this session delivered

### 2a. Merged (2026-11-04 and 05)

| PR | Piece | Decisions |
|---|---|---|
| #276 | The installer keeps the API running (app pool AlwaysRunning, no idle time-out, `preloadEnabled`, `Web-AppInit` prerequisite step `Prereq.IisAppInit`): the dev site's nightly pull hadn't run since 2026-10-20 because IIS stopped the idle app | D-332 |
| #277 | Nested AD group path on "Via AD group" (script 66); application owners' review emails with two options (script 67); Reports > Safe Administrators; Trends' "Progress against target dates" chart | D-333 to D-336 |
| #278 | User docs for the layout (D-330) and the installer's missing-answer stop (D-331) | |
| #279 | **Release v1.5.2** | |
| #280 | **Where a pull or load is**: pull steps saved every 5 s (`cyberark_direct_pull_step`), a stopped pull names its step; the Load logs each of its 20 procedures (`etl_run_step`, kept after a rollback); `usp_Get_EtlSessionActivity` shows a running step's wait and blocker, signed by `Manual/10` (installer step `Db.SessionActivity`) so the app's login doesn't hold VIEW SERVER STATE (the user's choice); Admin > Data Sources, **Import and Load** section (script 68) | D-337 |
| #281 | Who can use this account lists each Safe-member group, expandable (D-339); Progress Rules: Source picked from the source systems, alphabetical fields, case-insensitive matching on any collation (script 69, D-338); folder beside the Safe; drop-down lists alphabetical (`utils/sortOptions.js`); `Update-BlueTrack.ps1` rerun reuses the same commit's download (D-340); the installer now saves `PowerBiReaderAccount` (it was missing from the saved answer names, so every unattended rerun stopped on it); a flaky audit-log performer test fixed | D-338 to D-340 |

### 2b. On the uncommitted DNA branch (`2026-11-05-dna`)

| Piece | Decisions |
|---|---|
| **The newer DNA report** (Windows, Unix, Domain and Database Scan sheets; header under a summary block, searched to row 30) and its extra columns; **DNA data feed** (type `DnaReport`, `DnaImportService` shared with the upload; Test lists the sheets); **sort** on DNA Accounts | D-341 |
| **Track in BlueTrack** from DNA Accounts (source `DNA`, first stage; one account per machine, never joined across hosts); **DNA tracking rules** applied after each import | D-342 |
| **Purge DNA data**, and **Keep DNA data (days)** enforced after the nightly feed run | D-343 |
| **Restricted DNA columns**: new permission `ViewDnaSensitiveColumns` (Admin); the API blanks ticked columns and won't search or sort them for anyone else | D-344 |
| **Hard-coded credentials** into `web.dna_hard_coded_credential`; DNA's masked line kept, any readable `password=`-style value masked again; **Reports > DNA Hard-Coded Credentials** (ViewDnaSensitiveColumns only, export audited) | D-345 |
| **Linking by Account Type**: local by user name and machine (full or short name); domain by user name and its domain (or its first part); groups and the like never | D-346 |
| **Unresolved SIDs** (the account's own SID: a trusted domain's account or a deleted one): flagged, filterable, a rule field, linked only to an account kept under that SID | D-347 |
| **Accounts search box**: the address is written after the typing pause, the page ignores its own late writes, and leaving the list mid-pause still remembers the search (`onBeforeRouteLeave`) | (lessons learned) |

The user's two DNA workbooks are in `C:\Code\References\Data_DNA` and are test data now, but they hold real host and user names and the repository is public: **never commit them**; the tests build made-up workbooks of the same shape. The reader gave 9,631 accounts and 715 hard-coded credentials (old format) and 5,083 and 302 (new), with no readable password.

### 2c. Changed on the dev site by request (data, not code)

- **Pull now** run (2026-11-04): rejected by the change guard (Safes 55 to 42, 20% allowed); the user said accept, and PullKey 11 loaded (13 date-stamped and workstation Safes gone, 8 built-in Safes new).
- The **CI runner service** was restarted with the user's go-ahead (a run queued for 2 hours with the runner idle).

## 3. Open items

1. **Commit the DNA branch** when the user says: PR, merge after CI, deploy (the user's pattern). Then the 1.5.3 Upgrade Guide row covers scripts 68 to 70, `Db.SessionActivity`, the new permission (Admins need Reload My Rights), and the saved `PowerBiReaderAccount`.
2. **Two merged branches still on GitHub and local**: `2026-10-21-field-permissions-signin` (#220) and `2026-10-21-platform-mapping-suggestions` (#221), both merged. Deleting them was blocked by auto mode (Git Destructive) on 2026-11-05; ask the user to delete them or allow it.
3. **The dev host's SQL Server TLS certificate has expired**: `sqlcmd` needs `-C`. Environment task (`Planning_Backlog.md`).
4. **Account `60_6`**: done (D-350): its folder comes from the accounts that link it; device type and creation method have no source and stay blank.
5. **More on stuck pulls and loads** (`Planning_Backlog.md`): no-progress alerts, a Deployment-page/health-script check, Cancel for a running load. D-337 built the first three of six proposals.
6. **Not proven live** (`Planning_Backlog.md`, "Built, but never proven"): as before, plus a DNA feed from a real folder and a real upload of the new workbook on the dev site, linking against real CyberArk domain accounts (D-346/347), and the D-337 waits during a real stuck load.
7. **User docs** (`Planning_User-Docs-Backlog.md`): rows for D-332 to D-347 and the search fix.

## 4. Conventions that are easy to get wrong

All are in `CLAUDE.md` or the docs it links; these bit during this work. The list in `Archive_Reference_Handoff-2026-10-21.md` section 4 still applies.

- **Ask, don't assume:** every design choice in this session went to the user as options with a recommendation (the AskUserQuestion tool); they answered each, and twice corrected a premise ("only domain accounts?", "the SID is the account's"). Check real data before asking (the DNA workbooks answered several questions).
- **Commit, push, PR, merge, deploy only when asked**; "Not yet" means hold the branch.
- **Monitoring SQL must never wait on what it watches**: `usp_Get_EtlSessionActivity` and the app read `etl_run_log`/`etl_run_step` `WITH (NOLOCK)` (the Load writes its steps inside its transaction).
- **SQL Server 2025 refuses the session views** without VIEW SERVER STATE (error 371) instead of filtering: check `HAS_PERMS_BY_NAME` first. A signed procedure loses its signature on every ALTER: `Db.SessionActivity` re-signs on each install.
- **DNA:** a credential column is dropped from account sheets; the hard-coded credentials sheet keeps only DNA's masked line, masked again. A DNA-tracked account's identity is machine + name (+ instance, database). Linking follows Account Type (D-346/347).
- **Public repository:** no customer or lab data in tracked files; made-up fixtures only.

## 5. How to work here (commands)

| Task | Command |
|---|---|
| API tests | `dotnet test App/Api.Tests` (one class: `--filter "FullyQualifiedName~Name"`) against `BlueTrackTest_Local`. After a new or changed script, or if a test fails on database state (the audit-chain test did once, after a day of runs), rebuild it: `powershell -File Tools/Reset-BlueTrackLocalTestDb.ps1`. |
| One script on the local test DB | replace `$DatabaseName$` with `BlueTrackTest_Local` (sed into the scratchpad), then `sqlcmd -S . -C -I -b -i <file>` run from that folder (an absolute `C:/Users/ADMINI~1/...` path given to `-i` failed with "file C: access denied"). |
| Web / E2E | `npm run test` in `App/Web`; `npx playwright test` in `App/E2E` (not while CI runs). |
| Deploy to the dev site | From the main checkout on an up-to-date `main`: `powershell -NoProfile -ExecutionPolicy Bypass -File Deploy/Install-BlueTrack.ps1 -ConfigFile Deploy/State/answers.BlueTrack.json` (the user allowed it on 2026-11-05). Don't edit the main checkout while it builds from it: work in a worktree. |
| Release | `Tools/New-BlueTrackRelease.ps1 -Version X.Y.Z -Push`, merge the PR after CI, then `-Version X.Y.Z -Tag -Push` (both ran on 2026-11-05 for 1.5.2). |
| Pull now / scripted API writes | Windows auth plus `X-Requested-With: XMLHttpRequest`; Pull now accepting a change: `POST /BlueTrack/api/admin/cyberark-sources/direct/pull` with `{"acceptChange":true}`. |
| Read DNA workbooks without openpyxl | Python's `zipfile` + `xml.etree` (see `account_types.py`-style scripts); this host's Python has no openpyxl. |

## 6. Things that cost time this session

1. **Shell quoting**: heredocs with backticks, `▾`/`▸` or nested quotes broke `bash -c`; Python strings turned `\1` in a Windows path into `\x01`/`\b` (a control character in `BlueTrack.Database.psm1` once). Write edit scripts with the Write tool and use raw strings for paths.
2. **CRLF**: files written by the Write tool are LF, and `sed -i` turns CRLF into LF; normalise every changed file before committing (a short Python loop over `git diff --name-only` and untracked files).
3. **Auto mode blocks**: the installer, `gh pr merge` and branch deletion were each blocked once; the user then allowed the installer and merges. Report and stop; don't work around.
4. **Vitest**: `vi.restoreAllMocks()` clears a `vi.fn().mockResolvedValue(true)` from a `vi.mock` factory, so set it again in `beforeEach`; a component that starts using the rights store needs `setActivePinia(createPinia())` in its tests; plurals are two forms only (`one | many`).
5. **E2E timing**: `toHaveCount(4)` can pass before a debounced search runs when the list already has 4 rows; the Accounts fix had to settle a pending search on route leave, not unmount.
6. **The CI runner** lost its broker connection again (2026-11-04: a run queued 2 hours, runner idle); restarting the service fixed it (`Planning_Backlog.md` has a note for the health script).
7. **Wrong citations**: a doc nearly cited D-139 for something it isn't; check a D-number's row before citing it.

## 7. Where to look

- Rules and folder map: `CLAUDE.md`. Architecture: `Claude_Docs/Design_Architecture.md`.
- Pull and load progress (D-337): `App/Api/CyberArkDirect/PullProgress.cs`, `SelfHostedDirectPullService.cs`, `PvwaClient.cs`; `Database/68_*`, `Database/Manual/10_*`; `App/Api/Data/EtlRunRepository.cs`, `EtlRunsController.cs`; `App/Web/src/views/admin/ImportLoadSection.vue`, `runProgress.js`; installer `Install-BlueTrackSessionActivity` in `Deploy/Modules/BlueTrack.Database.psm1`.
- Safe access groups (D-339): `SafeAccessRepository.cs`, `components/SafeAccessSection.vue`. Rule Sources (D-338): `ProgressRuleEdit.vue`, `Database/69_*`. Update rerun (D-340): `Deploy/Update-BlueTrack.ps1`; saved answers: `Deploy/Modules/BlueTrack.State.psm1`.
- DNA (D-341 to D-347, on the branch): `App/Api/Dna/` (`DnaReportReader.cs`, `DnaRepository.cs`, `DnaImportService.cs`, `DnaColumns.cs`, `DnaTrackRuleRepository.cs`), `DnaController.cs`, `DataFeedRunner.RunDnaAsync`; `Database/70_*`; `views/reports/DnaAccounts.vue`, `DnaHardCodedCredentials.vue`, `views/admin/DnaSection.vue`, `DnaTrackRules.vue`, `dnaLabels.js`; tests `DnaTrackingTests.cs`, `DnaImportTests.cs`.
- Accounts search (lessons learned, 2026-11-05): `views/AccountProgressList.vue` (`writeAddress`, `lastWritten`, `settleTyping`).

## 8. Suggested next steps

The user decides the order.

1. Commit the DNA branch (section 3, item 1), merge after CI, deploy, and upload the new workbook on the dev site to see it with real data (with the user's go-ahead: it writes DNA data).
2. The 1.5.3 Upgrade Guide row, then release 1.5.3 when the user asks.
3. Delete the two merged branches (section 3, item 2) once allowed.
4. Check tonight's Load on Admin > Data Sources > Import and Load: the first nightly run with all 20 procedures logged.
5. The user said more items are coming; the backlog's "More on stuck pulls and loads" is waiting for a decision.
