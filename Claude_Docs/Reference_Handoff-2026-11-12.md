# BlueTrack: Handoff, 2026-11-12

> **Stage: Reference (point-in-time snapshot).** Written on 2026-11-12 for whoever picks the project up next (a person or another Claude session). Supersedes `Archive_Reference_Handoff-2026-11-10.md` (D-352, D-353), which, with `Archive_Reference_Handoff-2026-11-06.md`, `Archive_Reference_Handoff-2026-11-05.md` and `Archive_Reference_Handoff-2026-10-21.md`, stays the place for that history. Facts about the code live in the code and the other `Claude_Docs/` files; this note points at them.

## 1. Where things stand

- **Released: v1.5.4** (2026-11-12; signed tag on `72305ea`, GitHub verified the signature). Release numbers go up by 0.0.1 only.
- **`main`** is at `b2c98fc` (PR #297). **Unreleased:** D-354 to D-360 and a rule-condition fix; Release Notes' Unreleased section lists them. The next release is **1.5.5**; the user hasn't asked for it yet.
- **CI:** green on `main`. Base changes of a PR don't start CI (only opened, synchronize, reopened): after retargeting a stacked PR to `main`, close and reopen it.
- **Dev site** (`https://bluetrack.company.com`) runs `1.5.4-dev.13+b2c98fc`, installed 2026-11-12 from `main`, Smoke all OK. The Progress Rules log (D-359) starts empty there; it fills on the next load.
- **Decision register:** last **D-360**; next **D-361**. **Database scripts:** last `74`; next `75`. Manual scripts: last `10`. **Tools/Sql/** is new (D-356).
- **No open branches** other than this handoff's.

## 2. What the 2026-11-12 session delivered

| PR | Piece | Decisions |
|---|---|---|
| #291, #292 | The 2026-11-10 handoff; **release v1.5.4** (D-350 to D-353). | |
| #293 | `Get-BlueTrackResourceUsage.ps1` explains memory pressure: server start time, where memory in use is, top 15 processes, a Warn when SQL Server's target is under half its max server memory or physical memory is low, and when statement statistics cover under an hour; a reminder to sample. From the customer's first report, which named no query. | D-354 |
| #294 | **DNA pages** sort and page keys only, then read the page's columns: SQL Server's grant for a 500-row page fell from 98 MB to 6 MB. **DNA import** bulk-copies through `ListDataReader<T>` (no `DataTable` copy). | D-355 |
| #295 | **Safe access check:** Self-Hosted's MoveAccountsFrom + MoveAccountsInto equal Privilege Cloud's MoveAccountsAndFolders, both ways (script 73). `Tools/Sql/SafeAccessPolicyCheck.sql`: why a Safe passes or fails, member by member. | D-356 |
| #296 | **Fix:** a Progress Rule or DNA tracking rule condition on a days field failed to save (`v-model` on a number box sent a number to a string). **Duplicate** a Safe access policy or Safe role. Account Progress list: **CPM Status** and **Managed by the CPM** columns. **Progress Rules log** (script 74): last run, each rule's Last applied, a rule's Applied to; kept `ProgressRuleLogDays` (Settings, default 90). | D-357, D-358, D-359 |
| #297 | "Who can use this account": **User source** column (Vault or LDAP). | D-360 |

## 3. Open items

The backlog (`Planning_Backlog.md`) has the full list. In order:

1. **The customer's worker memory** (backlog, D-353 to D-355). The first report was a single reading on a server short of memory, with SQL Server's target far below its limit. The customer is upgrading to 1.5.4 (Workstation GC) and will re-run `Get-BlueTrackResourceUsage.ps1 -SampleSeconds 300` while looking at the DNA data. D-354 (the clearer report) and D-355 (DNA grant and import) reach them only in 1.5.5. Their report names their server and a user: never copy it into tracked files.
2. **The customer's Safe access policy** (D-356): they can work around it now by changing the roles PAM Admin Full and PAM Admin Limited to name MoveAccountsFrom and MoveAccountsInto (Self-Hosted Safes only), then Check now; 1.5.5 makes either form work.
3. **The customer's failed Import** (from #289): they should open Admin > Data Sources to see why.
4. **Release 1.5.5** (D-354 to D-360) when the user asks, after CI on `main` is green.
5. **Privilege Cloud user sources** (D-360): the dev data's Privilege Cloud users are all `CyberArk`; check what a real tenant reports (Identity or federated users) and map any other value.
6. **Privilege Cloud Direct with PSM for Privilege Cloud** (backlog, "Requested, not started"): needs a Privilege Cloud tenant; still no answer.
7. Then: the "Built, but never proven" items; the environment items (SMTP, SIEM, second PVWA, remote DC).

## 4. Conventions that are easy to get wrong

All are in `CLAUDE.md` or the docs it links; these bit recently. The lists in the archived handoffs still apply.

- **Ask, don't assume;** commit, push, PR, merge and deploy only when asked. Merge with `gh pr merge N --merge --delete-branch`; for a stacked PR, retarget it to `main` first, then close and reopen it so CI runs.
- **Line endings differ by file:** `User_Docs/Release_Notes.md`, the Decision Register, the backlogs, `Tools/README.md` and several `.vue` files are CRLF; others LF. Scripted edits must match each file; a `sed` that adds `\r` to an LF file leaves a stray CR.
- **Shell paths:** a relative path from `App/Web` (`../../../`) is `C:\`; write scratch files to the scratchpad by absolute path.
- **`sqlcmd` from Git Bash** mangles `-i`/`-o` paths; run it through `powershell -Command` for script files, and pass `-C` (the local certificate's name doesn't match).
- **Public repository:** no customer or lab data in tracked files; made-up fixtures only.

## 5. How to work here (commands)

| Task | Command |
|---|---|
| API tests | `dotnet test App/Api.Tests` (one class: `--filter "FullyQualifiedName~Name"`) against `BlueTrackTest_Local`; after a new or changed script, rebuild it with `powershell -File Tools/Reset-BlueTrackLocalTestDb.ps1`. |
| Web / E2E | `npm run test` in `App/Web`; `npx playwright test` in `App/E2E` (not while CI runs). |
| Deploy to the dev site | From the main checkout on an up-to-date, clean `main`: `powershell -NoProfile -ExecutionPolicy Bypass -File Deploy/Install-BlueTrack.ps1 -ConfigFile Deploy/State/answers.BlueTrack.json`. Stash uncommitted work first: the build uses the working tree. |
| Resource usage | `powershell -File Tools/Get-BlueTrackResourceUsage.ps1 -SampleSeconds 300` (read-only; `Tools/README.md`). |
| Safe access diagnosis | `Tools/Sql/SafeAccessPolicyCheck.sql` in the BlueTrack database, with `@SafeName` set. |
| Release | `Tools/New-BlueTrackRelease.ps1 -Version X.Y.Z -Push`, merge the PR after CI, then `-Version X.Y.Z -Tag -Push`. |

## 6. Where to look

- Rules and folder map: `CLAUDE.md`. Architecture: `Claude_Docs/Design_Architecture.md`.
- Resource tool (D-353, D-354): `Tools/Get-BlueTrackResourceUsage.ps1`, `Tools/README.md`.
- DNA paging and import (D-355): `App/Api/Dna/DnaRepository.cs`, `App/Api/Data/ListDataReader.cs`; `Claude_Docs/Design_Data-Sources.md`; Lessons Learned (memory grants).
- Safe access (D-356, D-357): `Database/73_BlueTrack_SafeAccessMovePermissions.sql`, `Tools/Sql/SafeAccessPolicyCheck.sql`, `App/Web/src/views/admin/SafeAccessPolicyEdit.vue`, `SafeRoleEdit.vue`; `Claude_Docs/Design_Safe-Access-Policies.md`.
- Account Progress columns (D-358): `App/Api/Data/AccountProgressRepository.cs`, `App/Web/src/views/AccountProgressList.vue`.
- Progress Rules log (D-359): `Database/74_BlueTrack_ProgressRuleLog.sql`, `ProgressRulesController`, `ProgressRules.vue`, `ProgressRuleEdit.vue`; `Claude_Docs/Design_Application-Structure.md`.
- User source (D-360): `App/Api/Data/SafeAccessRepository.cs`, `App/Web/src/components/SafeAccessSection.vue`; `Claude_Docs/Design_CyberArk-Direct-Integration.md`.
- Earlier work (D-352, D-353): `Archive_Reference_Handoff-2026-11-10.md`.
