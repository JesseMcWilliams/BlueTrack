# BlueTrack: Handoff, 2026-11-06

> **Stage: Reference (point-in-time snapshot).** Written on 2026-11-06 for whoever picks the project up next (a person or another Claude session). Supersedes `Archive_Reference_Handoff-2026-11-05.md` (1.5.2, D-332 to D-347, the DNA work) and `Archive_Reference_Handoff-2026-10-21.md` (everything up to 1.5.1); both stay the place for that history. Facts about the code live in the code and the other `Claude_Docs/` files; this note points at them.

## 1. Where things stand

- **Released: v1.5.3** (2026-11-05; signed tag on `ea4359f`, the merge of PR #285; GitHub verified the signature; release published; `Published_Docs` and `Sbom/` rebuilt). It holds D-337 to D-349. Release numbers go up by 0.0.1 only.
- **`main`** is at `5c61c4e` (PR #286). **Unreleased:** D-350 (a shared linked account's folder) and D-351 (an Import's files). The next release is **1.5.4**.
- **Dev site** (`https://bluetrack.company.com`) runs `main` at `5c61c4e` (`1.5.3-dev.2`), installed 2026-11-06 with the saved answers; every health check OK, including the new Pull and Load check. The nightly pull (01:30) and Import, Load, Snapshot (02:00) ran on 2026-11-06 with all 20 Load procedures logged. A Pull now (PullKey 14) and its load ran at 07:02 local time to prove D-350. The DNA workbook of 2026-11-05 is imported there; it gives 40 risk findings (38 non-compliant, 2 Pass-the-Hash, all on accounts; no hard-coded secret matches the site's targets), already scored.
- **Decision register:** last **D-351**; next **D-352**. **Database scripts:** last `72`; next `73`. Manual scripts: last `10`.
- **No open branches** on GitHub or locally; no worktrees.

## 2. What the 2026-11-05/06 session delivered

| PR | Piece | Decisions |
|---|---|---|
| #282 | DNA: the newer report, data feed, sort, Track in BlueTrack, tracking rules, purge and retention, restricted columns (`ViewDnaSensitiveColumns`), hard-coded credentials report, linking by Account Type, unresolved SIDs; the Accounts search fix | D-341 to D-347 |
| #283 | **Stalls:** `RunStallMinutes` (Alert after, Import and Load section), the RunStalled email, the Deployment page's Pull and Load health check, **Cancel load** (`sp_stop_job`); `Test-BlueTrackHealth.ps1` warns when a CI run is queued with none in progress; user docs for D-332 to D-348 (script 71) | D-348 |
| #284 | **Admin > Risk Score** (was Risk Score Bands): Bands, Defaults (default scores, each DNA finding's risk value), Findings; `web.risk_finding` from DNA (hard-coded secrets on the system and the account they name, privileged when DNA or BlueTrack says so; Pass-the-Hash and non-compliant linked accounts), scored as values in the active algorithm; `Admin_SettingsReference` split into three parts (script 72) | D-349 |
| #285 | **Release v1.5.3** | |
| #286 | A shared logon/reconcile account (the lab's `60_6`) takes its folder from the accounts that link it: the classic search returns only its first match, and no documented 14.6 call reads one account's internal properties by ID | D-350 |

Also, by request: the CI runner service was restarted once more (2026-11-05, a release run queued with the runner idle; the new health-script warning caught it), and the user renewed the dev host's SQL Server certificate.

## 3. Open items

The backlog (`Planning_Backlog.md`) has the full list. The user's order on 2026-11-06:

1. **Review the first DNA risk scores** on the dev site (Admin > Risk Score > Findings, Reports > Risk Score): are 200 (non-compliant) and 400 (Pass-the-Hash) right?
2. **Nested AD group path** (D-333): proven live 2026-11-06. The pull's paths (`dbo.ad_group_member.NestedPath`) for CyberArk Vault Auditors matched Active Directory: Test Nested, and Test Nested > CyberArk Vault Power Users.
3. **The Import step's sub-steps**: done (D-351): an Import row lists its files from `import_log`.
4. Then: Privilege Cloud Direct with PSM for Privilege Cloud; the "Built, but never proven" items; the environment items (SMTP, SIEM, second PVWA, remote DC).

## 4. Conventions that are easy to get wrong

All are in `CLAUDE.md` or the docs it links; these bit recently. The lists in both archived handoffs still apply.

- **Ask, don't assume:** design choices go to the user as options with a recommendation; check real data first (the lab PVWA and the DNA workbooks answered several questions).
- **Commit, push, PR, merge, deploy only when asked**; "Not yet" means hold the branch. Merge with `gh pr merge N --merge --delete-branch`; if the local branch is checked out in a worktree, gh leaves the remote branch: delete it with `git push origin --delete`.
- **Worktrees:** a worktree's `node_modules` may be a junction into the main checkout. Remove junctions with `cmd /c rmdir <path>` (never a recursive delete through them) before removing the folder; `git worktree remove` often leaves gitignored build output behind.
- **The classic PVWA account search returns one account** (the first match; `Count` is the total). Don't expect lists from it.
- **Public repository:** no customer or lab data in tracked files; made-up fixtures only.

## 5. How to work here (commands)

| Task | Command |
|---|---|
| API tests | `dotnet test App/Api.Tests` (one class: `--filter "FullyQualifiedName~Name"`) against `BlueTrackTest_Local`; after a new or changed script, rebuild it with `powershell -File Tools/Reset-BlueTrackLocalTestDb.ps1`. |
| Web / E2E | `npm run test` in `App/Web`; `npx playwright test` in `App/E2E` (not while CI runs). |
| Deploy to the dev site | From the main checkout on an up-to-date `main`: `powershell -NoProfile -ExecutionPolicy Bypass -File Deploy/Install-BlueTrack.ps1 -ConfigFile Deploy/State/answers.BlueTrack.json`. Work in a worktree while it builds. |
| Release | `Tools/New-BlueTrackRelease.ps1 -Version X.Y.Z -Push`, merge the PR after CI, then `-Version X.Y.Z -Tag -Push`. |
| Pull now / scripted API writes | Windows auth plus `X-Requested-With: XMLHttpRequest`: `POST /BlueTrack/api/admin/cyberark-sources/direct/pull` with `{"acceptChange":false}`. |
| Lab PVWA reads | Read-only GETs are allowed (memory); the service user comes from the CP SDK (`Live-Testing.local.md` says where). Keep probe scripts in the scratchpad. |
| CI stuck queued | `Tools/Test-BlueTrackHealth.ps1` reports "CI queue"; ask, then restart the runner service. |

## 6. Where to look

- Rules and folder map: `CLAUDE.md`. Architecture: `Claude_Docs/Design_Architecture.md`.
- Stalls and Cancel load (D-348): `App/Api/Data/RunStallDetector.cs`, `Notifications/RunStalledCheck.cs`, `HealthChecks/PullAndLoadHealthCheck.cs`, `Controllers/EtlRunsController.cs`, `views/admin/ImportLoadSection.vue`, `Database/71_*`.
- Risk defaults and findings (D-349): `Database/72_*` (`web.usp_DeriveRiskFindings`, `ufn_ReachableRiskValues`), `Data/RiskDefaultsRepository.cs`, `Controllers/RiskDefaultsController.cs`, `views/admin/RiskScoreAdmin.vue`, `RiskScoreDefaults.vue`, `RiskFindings.vue`; `Claude_Docs/Design_Risk-Scoring.md`.
- Linked account folders (D-350): `PvwaClient.ReadAccountDetailsAsync` and `LinkFolders`.
- An Import's files (D-351): `EtlRunRepository.GetImportDetailAsync`, `EtlRunsController` (`{key}/import`), `ImportLoadSection.vue`.
- DNA (D-341 to D-347): `App/Api/Dna/`, `DnaController.cs`; `Database/70_*`.
