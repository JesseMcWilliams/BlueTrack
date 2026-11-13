# BlueTrack: Handoff, 2026-11-10

> **Stage: Archive (superseded by `Reference_Handoff-2026-11-12.md`).** Was the point-in-time snapshot of 2026-11-10. Written on 2026-11-10 for whoever picks the project up next (a person or another Claude session). Supersedes `Archive_Reference_Handoff-2026-11-06.md` (1.5.3, D-337 to D-351), which, with `Archive_Reference_Handoff-2026-11-05.md` and `Archive_Reference_Handoff-2026-10-21.md`, stays the place for that history. Facts about the code live in the code and the other `Claude_Docs/` files; this note points at them.

## 1. Where things stand

- **Released: v1.5.3** (2026-11-05; signed tag on `ea4359f`). Release numbers go up by 0.0.1 only.
- **`main`** is at `eb8bc35` (PR #290). **Unreleased:** D-350, D-351, D-352 and D-353; Release Notes' Unreleased section lists them. The next release is **1.5.4**; the user hasn't asked for it yet.
- **CI:** the run for `eb8bc35` (the merge of #290) sat queued for hours with the runner online and idle (the broker-connection wedge, memory). Check it before any release or local test run.
- **Dev site** (`https://bluetrack.company.com`) still runs `7c79666` (`1.5.3-dev.4`), installed 2026-11-06, with Server GC (its `runtimeconfig.json` was switched to Workstation GC only to measure, then restored). Its Smoke step was re-run on 2026-11-09 with the D-352 installer: Completed, every check OK. Reinstall from `main` to pick up D-352 and D-353.
- **Decision register:** last **D-353**; next **D-354**. **Database scripts:** last `72`; next `73`. Manual scripts: last `10`.
- **No open branches** other than this handoff's.

## 2. What the 2026-11-09/10 session delivered

| PR | Piece | Decisions |
|---|---|---|
| #289 | **A customer upgrade stopped at Smoke** because Pull and Load was Degraded ("The latest Import failed"). Smoke now prints `[DEGRADED]` and a warning and completes; it fails only on Unhealthy, a failed health call, or a site root that doesn't serve the SPA. The installer's resume hints now print the full command (`& '<installer path>' -SiteName '<site>' -Resume`), because `Update-BlueTrack.ps1` has no `-Resume`/`-ListSteps`. | D-352 |
| #290 | **Less memory and CPU** after the customer saw usage rise: DNA workbooks read row by row with `DocumentFormat.OpenXml`'s `OpenXmlReader` (`App/Api/Dna/DnaReportReader.cs`; same rows as before on both real workbooks, about a third of the allocations and 2-3x faster); the API runs Workstation GC (`ServerGarbageCollection` false; on the dev site peak working set 14% lower, idle private memory 31% lower, same speed); audit sealing and SIEM forwarding poll every 10 s (was 5); new read-only `Tools/Get-BlueTrackResourceUsage.ps1`. SBOM regenerated. | D-353 |

## 3. Open items

The backlog (`Planning_Backlog.md`) has the full list. In order:

1. **The customer's failed Import** (from #289): the upgrade itself was fine; the customer should open Admin > Data Sources (the Import row lists its files, D-351) to see why the latest Import failed.
2. **w3wp grows "primarily when running a query"** at the customer (backlog, D-353). Waiting on the customer's `Get-BlueTrackResourceUsage.ps1 -SampleSeconds 300` output, run while the query happens; the user is arranging it. Likeliest cause: report exports (`XlsxReportWriter` builds the whole workbook with ClosedXML). Use the report's top statements (by CPU and by rows) and the worker samples to find the page.
3. **Release 1.5.4** (D-350 to D-353) when the user asks, after CI on `main` is green.
4. **Privilege Cloud Direct with PSM for Privilege Cloud** (backlog, "Requested, not started"). Asked on 2026-11-06 whether a Privilege Cloud tenant is available; still no answer. Building it needs one.
5. Then: the "Built, but never proven" items; the environment items (SMTP, SIEM, second PVWA, remote DC).

## 4. Conventions that are easy to get wrong

All are in `CLAUDE.md` or the docs it links; these bit recently. The lists in the archived handoffs still apply.

- **Ask, don't assume:** design choices go to the user as numbered options with a recommendation; measure before changing runtime settings (the GC choice was measured on the dev site first).
- **Commit, push, PR, merge, deploy only when asked.** Merge with `gh pr merge N --merge --delete-branch`.
- **No revision notes in code comments** ("was 5 s before 1.5.4"): git has the history.
- **`User_Docs/Release_Notes.md` uses CRLF;** most other docs use LF. Scripted edits must match the file's line endings.
- **Restoring a dev-site config file by hand** (as for the GC test): keep a copy in the scratchpad and recycle `BlueTrack-AppPool` afterwards; check `/api/me` returns 200.
- **Public repository:** no customer or lab data in tracked files; made-up fixtures only.

## 5. How to work here (commands)

| Task | Command |
|---|---|
| API tests | `dotnet test App/Api.Tests` (one class: `--filter "FullyQualifiedName~Name"`) against `BlueTrackTest_Local`; after a new or changed script, rebuild it with `powershell -File Tools/Reset-BlueTrackLocalTestDb.ps1`. |
| Web / E2E | `npm run test` in `App/Web`; `npx playwright test` in `App/E2E` (not while CI runs). |
| Deploy to the dev site | From the main checkout on an up-to-date `main`: `powershell -NoProfile -ExecutionPolicy Bypass -File Deploy/Install-BlueTrack.ps1 -ConfigFile Deploy/State/answers.BlueTrack.json`. One step: add `-Step <Name>` (e.g. `Smoke`). |
| Resource usage | `powershell -File Tools/Get-BlueTrackResourceUsage.ps1 -SampleSeconds 300` (read-only; `-SkipSql`, `-NoQueryText`, `-OutFile`; `Tools/README.md`). |
| Release | `Tools/New-BlueTrackRelease.ps1 -Version X.Y.Z -Push`, merge the PR after CI, then `-Version X.Y.Z -Tag -Push`. |
| CI stuck queued | `Tools/Test-BlueTrackHealth.ps1` reports "CI queue"; ask, then restart the runner service. |

## 6. Where to look

- Rules and folder map: `CLAUDE.md`. Architecture: `Claude_Docs/Design_Architecture.md` (includes the GC choice).
- Smoke and resume hints (D-352): `Deploy/Modules/BlueTrack.Smoke.psm1` (`Test-BlueTrackDeployment` returns `Passed` and `Degraded`), the Smoke step and `$ResumeCommand` in `Deploy/Install-BlueTrack.ps1`; `Claude_Docs/Design_Deployment-Methodology.md`.
- DNA streaming (D-353): `App/Api/Dna/DnaReportReader.cs`; `Claude_Docs/Design_Data-Sources.md`.
- Poll intervals (D-353): `App/Api/Audit/AuditChainBackgroundService.cs`, `App/Api/Siem/SiemForwardingBackgroundService.cs`; `Claude_Docs/Design_Audit-Logging.md`.
- Resource tool (D-353): `Tools/Get-BlueTrackResourceUsage.ps1`, `Tools/README.md`.
- Earlier work (D-337 to D-351): `Archive_Reference_Handoff-2026-11-06.md`, section 6.
