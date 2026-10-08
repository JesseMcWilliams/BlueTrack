# BlueTrack: Handoff, 2026-10-08

> **Stage: Planning.** Point-in-time snapshot for the next Claude session, covering 2026-10-05 to 2026-10-08. Once every item below is closed, rename to `Archive_Planning_Handoff-2026-10-08.md` with `git mv`.

## What was done
The installer was taken to a first real server, DCACYBSQL01 (Windows Server 2022, SQL Server on the same box). It is deployed there and its smoke test passes. Everything below is merged to `main` unless noted.

| PR | Decisions | What |
|---|---|---|
| #58 | D-168, D-169 | Installer as named steps (`-ListSteps`/`-Step`/`-StartAt`/`-Resume`), saved answers in `Deploy/State/`, `answers.sample.json`, offline/winget/download prerequisites. API builds without the CyberArk SDK. |
| #59 | D-170 | `Db.AppPoolAccess` step grants the app pool's SQL login; smoke-test failures explained by HTTP status. (Account choice corrected by D-171.) |
| #60 | D-171 | With SQL Server on the same machine, `ApplicationPoolIdentity` connects as `IIS APPPOOL\<pool>`, though SQL Server's error message names the computer account. |
| #61 | D-172, D-173, D-174 | Windows auth also on at the IIS site root (kept, but not the real fix; see D-175). Sign-in continues to the dashboard. Expired edit locks expire. |
| #62 | D-175 | Optional `Iis.Reset` step (`iisreset`); smoke test also checks the browser's `/api/...` path; `Build.Api` uses `app_offline.htm` to publish over a running API. |
| #63 | D-177 | The edit page takes over a lock the current user already holds; the theme E2E test waits for each save. |
| #64 | D-176 | Nightly import handles Privilege-Cloud-only or Self-Hosted-only sites (script 42, `usp_Import_All` flags, installer `ImportSources`). |
| #65 | (none) | Docs only: `Planning_Data-Sources.md` proposal, four backlog items, this handoff. Merged when its CI passed. |

## State
- `main` holds everything above. No other open PRs or branches.
- Suites at the last run: xUnit 445/445, Vitest 132/132, Playwright 57/57.
- This dev host: no `Deploy/State/` left over; the scratch database `ZzSourcesTest` and every throwaway IIS site were removed. SQL Server Agent is **stopped** here (left as found).

## Open items (numbering continues from the session)
182. **DCACYBSQL01, apply D-176:** pull `main`, then `.\Install-BlueTrack.ps1 -Step Db.Migrate,Db.NightlyJob`. It applies script 42, asks which sources the server has, and recreates the nightly job. Until then the old job imports both sources and fails on the missing one.
183. **DCACYBSQL01's app pool SQL login was granted by hand** (`IIS APPPOOL\BlueTrack-AppPool`, D-171). The corrected `Db.AppPoolAccess` step has not yet run on a fresh server.
184. **`Iis.Reset` (D-175) never ran for real**: it restarts every site on the host, so it was only tested with `-WhatIf`. Check it on the next real install.
185. **Browser sign-in loop cause not established** (D-172/D-175): a full `iisreset` after the deploy cleared it. If it recurs, capture IIS Failed Request Tracing for `401` before resetting.
186. **Data Sources page: on hold** (`Planning_Data-Sources.md`). All design questions answered except, per new system, its CSV layout and how its accounts match CyberArk's. The user said "not yet" to building phase 1.
187. **Backlog "Requested, not started"** (`Planning_Backlog.md`): group names on Group → Role Mapping, CSV import for Application ↔ Safe Mapping, an Account Progress search box (username/address).
188. **Backlog *(verify)* items:** CI runner labels in `ci.yml`; whether re-running `Build.Api` alone turns off Windows auth on `/BlueTrack` (its setting may live in the published API's `web.config`).
189. **Dev host lab site** (`bluetrack.company.com`) serves from `App\Web\dist` with no root `web.config`; deferred by the user (backlog).

## Gotchas for the next session
- **Bash tool and backslashes:** backslashes in heredocs, `sed` and inline Python passed through Bash were collapsed or stripped several times. Use the Edit/Write tools for any text containing `\`, or `Join-Path` in PowerShell.
- **After merging a PR**, update `main` with `git fetch origin main:main` rather than switching branches when `.claude/` tracking differs between commits; switching once left `.claude` "delete pending" until the session restarted.
- **Playwright on this host:** a contiguous block of `ECONNREFUSED ::1:4173` failures means the test web server went away, not a code bug; re-run. Back-to-back runs used to fail on leftover edit locks and the theme race; both fixed in #63.
- **`-WhatIf` and modules:** script modules don't inherit the script's `$WhatIfPreference`; `Install-BlueTrack.ps1` passes `-WhatIf` to every `*-BlueTrack*` call via `$PSDefaultParameterValues` (D-168). Keep that when adding module functions.
- **IIS config changes on this host** can fail the first request right after the change; repeat before concluding anything (lesson from the D-175 repro).
- **SQL Server "Login failed for user" names can be wrong:** trust the 18456 event's SID (D-171).
