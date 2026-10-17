# BlueTrack: Handoff, 2026-10-17

> **Stage: Planning.** Point-in-time snapshot for the next Claude session, written 2026-10-17. Supersedes `Archive_Planning_Handoff-2026-10-15.md` (PRs #124 to #142). Once every open item below is closed, rename this to `Archive_Planning_Handoff-2026-10-17.md` with `git mv`.

## What was done since the last handoff
| PR | Decisions | What |
|---|---|---|
| #143, #144 | (none) | Handoff update; release notes' Upgrading lists scripts 11 to 26 and the Hosting Bundle by hand. |
| #145, #147, #152 | (none) | Releases v1.0.5, v1.0.6 and v1.0.7 (`Tools/New-BlueTrackRelease.ps1`, D-226). |
| #146 | D-235 | `Update-BlueTrack.ps1` leaves the download folder's permissions alone unless `-LockDownFolder`. |
| #148 | D-236 | Feed accounts accept a user name plus address of up to 400 characters. |
| #149 | D-237 | Skip group rows on an account inventory feed. |
| #150 | D-238 | Assign an account to an application; its exceptions cover the account. |
| #151 | D-239 | Bulk-assign accounts to an application (bulk edit and a CSV import). |
| this PR | D-240 | Local test runs use `BlueTrackTest_Local`; CI keeps `BlueTrackTest`. `Tools/Reset-BlueTrackLocalTestDb.ps1` builds it. |

## State
- **`main`** at the #152 merge (v1.0.7) plus this PR. Last full local runs on 2026-10-17 against `BlueTrackTest_Local`: xUnit 1075/1075, Playwright 88/88. Vitest and PSScriptAnalyzer weren't re-run for this change (the new script is clean).
- **Not re-checked on 2026-10-17:** the dev site's version and health, and the first scheduled run of the one-step nightly job (`SELECT TOP 5 * FROM dbo.etl_run_log ORDER BY 1 DESC;`).
- **Off until configured on the dev site:** the D-222 notification types, account classification, SIEM forwarding.

## Open items
1. **Check the nightly job and the site:** `Tools\Test-BlueTrackHealth.ps1 -SiteUrl https://bluetrack.company.com -SqlServerInstance WIN-K5POLANERI5.Company.com`, and the `etl_run_log` query above.
2. **LDAPS from DCACYBSQL01** (backlog): the user runs `Tools\Test-BlueTrackLdaps.ps1` there, then adds the domain in BlueTrack.
3. **Live tests still owed** (backlog, "Built, but never proven"): SIEM forwarding, account classification against the real AD, OAuth2 SMTP, the release script against GitHub (it ran for v1.0.5 to v1.0.7, so confirm and remove that item), break-glass email, Azure/AWS/Conjur.
4. **Backlog** (`Planning_Backlog.md`): API access for scripts and server-side localization (deferred, need a design), a scheduled feed for Account to Application assignments, a minimum-version prerequisite check, D-229 follow-ups, and the real-data findings.

## Gotchas for the next session
- **Local tests use their own database (D-240).** `dotnet test` and `npm test` default to `BlueTrackTest_Local`; build or reset it with `Tools\Reset-BlueTrackLocalTestDb.ps1` (also after a new numbered script). CI owns `BlueTrackTest`, so a local run no longer breaks a CI run's data. Still don't run Playwright locally while CI runs: the CPU and the ports (4173, 7033) are shared. Check with `gh run list --status in_progress`.
- **Redeploy from a clean `main`.** The installer builds the working copy. From Bash: `powershell -Command "& ./Install-BlueTrack.ps1 -Step Db.Backup,Db.Migrate,Build.Api,Build.Web,Iis.SiteRestart"` (with `-File` the step list arrives as one string). Add `Db.NightlyJob` when the job script changes and `Db.AppSettings` when the settings change. Run `Tools\Test-BlueTrackHealth.ps1` afterwards.
- **Installed site settings live in `appsettings.Site.json`** (D-230), next to the published API. Don't add `appsettings.<Environment>.json` files to an install or set `ASPNETCORE_ENVIRONMENT=Development` on IIS: Development turns on the test sign-in endpoint and DevFakeAuth.
- **Deleting a PR's head branch closes the PR without merging.** On 2026-10-14 a branch-delete list included two branches whose PRs were still open; both closed and had to be pushed back and reopened. Only list branches whose PRs show MERGED.
- **Waiting on CI with `gh pr view --json statusCheckRollup`:** a reopened or re-pushed PR has more than one check run; test "every check COMPLETED" (`[.statusCheckRollup[]|select(.status!="COMPLETED")]|length == 0`), not one exact status string.
- **`.gitignore` ignores `*.csv`** so real exports are never committed; synthetic test fixtures go under `App/Api.Tests/Fixtures/` (the only exception). Check `git ls-files` after adding one.
- **Never copy `Live-Testing.local.md` details into tracked files** (lab account, AppID, Safe, Object); use placeholders in examples. Real CyberArk data: report schema-level facts only (names of columns, counts), never values.
- **Backslashes through Bash** mangle heredocs and inline Python (`\r\n` became line breaks, `\04` an octal character, `DOMAIN\name` a line break). Write such text with the Write/Edit tools, then grep for control characters.
- **POSTs to the live API** need `X-Requested-With: XMLHttpRequest` (D-197).
- **E2E label clashes:** `getByLabel` is case-insensitive and matches substrings; after a navigation, wait for something only the new page has before filling (see `Reference_Lessons-Learned.md`, 2026-10-13). A UI test that changes shared data and undoes it must wait for each save.
- **One remaining one-off E2E flake:** "Admin deletes an account ... undeletes it in bulk" timed out once under load and passed on re-run.
- **The test database has no CyberArk accounts with an address**; feed-linking tests add a temporary `PRIVCLOUD` account and remove it (xUnit runs one test at a time).
- **Live SSO checks without a browser:** `Tools\Test-BlueTrackSso.ps1` (authentik's flow API; the lab account's Credential Provider values are in `Live-Testing.local.md`). For the two front-channel Single Logout legs use a browser: headless Chromium from `App/E2E/node_modules/playwright`, waiting for authentik's "Not you?" before filling the password. IIS writes its log (`C:\inetpub\logs\LogFiles\W3SVC2`) about a minute late.
- **After a reboot**, check SQL Server Agent too (the health check does).
- **Merge with `gh pr merge N --merge --delete-branch`** (the user's standing rule) unless another open PR is stacked on that branch; scripted wait-and-merge loops too.
- **A query that must not wait on in-flight rows has to seek past them** (D-234 lesson): CI's fresh database chose a clustered scan that blocked on an open transaction while the dev box used the index. Force the index; don't use `READPAST` where skipping a locked committed row loses data.
- **Stacked PRs:** merging one with `--delete-branch` closes the next; merge without `--delete-branch` and delete branches once the whole stack is merged. When an earlier PR changes the same doc lines, merge `main` into the first stacked branch and pass the merge down the stack.
- **CI runner wedge** (queued, runner idle, broker errors in `C:\actions-runner\_diag`): restart the runner service, after asking the user.
- **Rights cache lives in SQL Server** and outlasts a test run: a test that depends on a new grant clears it first (`UserRightsCache.InvalidateAllAsync`).
- **UI text is in message files** (D-227): new SPA text goes in `App/Web/src/locales/en/<area>.json`, never hard-coded; the key-coverage test fails otherwise (`Claude_Docs/Design_Localization.md`).
- **Release notes:** every user-visible change gets a line under Unreleased in `User_Docs/Release_Notes.md`, and one in `Planning_User-Docs-Backlog.md`.
