# BlueTrack: Handoff, 2026-10-15

> **Archived 2026-10-17:** superseded by `Planning_Handoff-2026-10-17.md`, which carries its open items.
>
> **Stage: Planning.** Point-in-time snapshot for the next Claude session, written at the end of 2026-10-14 (updated after PR #142). Supersedes `Archive_Planning_Handoff-2026-10-14.md` (PRs #109 to #122). Once every open item below is closed, rename this to `Archive_Planning_Handoff-2026-10-15.md` with `git mv`.

## What was done
On 2026-10-13 and 2026-10-14, after a review of what was missing: report export, Trends, more emails, OAuth2 for SMTP, typeahead and CSV header detection, versioning, localization (D-220 to D-227); then the fixes the live data and the deploys turned up (D-228 to D-230) and a security update; then the user docs, and four requests from the user: Audit Log export, entitlement member names and resolution, discovered-account classification and SIEM forwarding (D-231 to D-234). Everything is merged to `main` and deployed on the dev site. Nothing is released yet: it's all under **Unreleased** in `User_Docs/Release_Notes.md`.

| PR | Decisions | What |
|---|---|---|
| #124 | (none) | Backlog from the review; real-data findings; stale design docs fixed. |
| #125 | D-220 | Export CSV / Excel on every report (all matching rows, Export Max Rows limit, `ReportExport` audit); Reports listed on the left. Script 17. |
| #126 | D-221 | Reports > Trends (stages, % complete, risk level over time; Chart.js). Script 18. |
| #127 | D-222 | Emails: NightlyImportFailed, OverdueRiskExceptions, WeeklySummary; `dbo.etl_run_log`. Script 19. |
| #128 | D-223 | OAuth2 (XOAUTH2) for SMTP: Microsoft 365, Google Workspace, generic. Script 20. Unproven live. |
| #129 | D-224 | Snapshot every night even when the import fails (`usp_RunNightlyImportLoad`, one-step job); Site URL. Script 21. |
| #130 | D-225 | Target typeahead; CSV header auto-detect on Import Mapping Profiles. |
| #131 | D-226 | `Tools/New-BlueTrackRelease.ps1` (release PR, then signed tag); git build stamp on About/Deployment. |
| #132 | D-227 | vue-i18n: every SPA string in `src/locales/en/*.json`; Language picker. |
| #133 | D-228 | Privilege Cloud import reads columns by header (the export layout changed; Msg 7301); Test checks headers. Script 22. |
| #134 | D-229 | Linked targets in Target Match Review (Link / New Target / Ignore), replacing D-225's move. Script 23. |
| #135 | (none) | Backlog and handoff after D-229. |
| #136 | (none) | Security update: Microsoft packages to 10.0.13 (Negotiate, Cryptography.Xml, Cryptography.Pkcs), `source-map-js` patch. |
| #137 | D-230 | Installer settings in `appsettings.Site.json`, always loaded (the old `appsettings.<Environment>.json` was never read). |
| #138 | (none) | User docs for D-220 to D-230; `Admin_SettingsReference.md` split (sections 6+ in `Admin_SettingsReference_Notifications.md`, joined by `build_published_docs.py`); first version of this handoff. |
| #139 | D-231 | Export CSV / Excel on the Audit Log (applied filters, UTC timestamps to the second). |
| #140 | D-232 | Unresolved Entitlement Members: Name column; the load resolves members by manual resolution, then ID, then exact unique name; Resolve / Undo (ConfirmReconciliation). Corrects D-145. Script 24. |
| #141 | D-233 | Discovered Accounts: Account Class (Primary / Secondary / Admin / Unknown), Owner, Matched by; Admin > Account Classification rules (attribute, name, OU; owner by name or employee ID), off by default. Script 25. |
| #142 | D-234 | Admin > SIEM Forwarding: audit events as CEF in RFC 5424 syslog over TCP or TLS; rowversion watermark below `MIN_ACTIVE_ROWVERSION()`, single-row lease, purge keeps unsent rows. Script 26. |

## State
- **`main`** at the #142 merge (`d010e78`) plus this PR. Suites at the last full run: xUnit 1057/1057, Vitest 408/408, Playwright 88/88, PSScriptAnalyzer 0. Every topic branch is merged and deleted.
- **Dev host runtime:** .NET Hosting Bundle 10.0.13 and SDK 10.0.402 installed on 2026-10-14 (alongside 10.0.11); the site's worker runs 10.0.13. The Cryptography.Xml advisories are fixed only by the runtime, not the package.
- **Dev site** (`https://bluetrack.company.com`, API at `/BlueTrack`): `main` as of #142 (version `1.0.0-dev.81+d010e78`), scripts up to 26. Health check 13 pass, 0 fail (SIEM Forwarding: off). Environment shows **Development** (from `appsettings.Site.json`); IIS itself runs as ASP.NET Core Production, so the dev-only test sign-in is 404. **HSTS is on** (`max-age=2592000`). Site URL `https://bluetrack.company.com`.
- **Data:** the Privilege Cloud import works again (D-228); `usp_RunNightlyImportLoad` was run by hand twice on 2026-10-14 (the second after deploying #142): Import, Load and Snapshot succeeded each time; all 30 previously unresolved entitlement members (79 rows) now resolve by name (D-232); all eight sources loaded, 2,453 AddressDerived Account→Target links, Trends has 2026-09-05 and 2026-10-14. The export leaves the new Users `Enabled`/`Suspended` and Groups `Directory` columns empty.
- **Nightly job:** recreated as one step (`EXEC usp_RunNightlyImportLoad;`, 02:00); its first scheduled run is 2026-10-15.
- **Off until configured on the dev site:** the D-222 notification types (Admin > Notifications), account classification (Admin > Account Classification; no discovered accounts yet, because no Access Group has an AD group mapping), and SIEM forwarding (Admin > SIEM Forwarding).

## Open items
1. **Check the first scheduled run of the one-step nightly job** (02:00 2026-10-15): `Tools\Test-BlueTrackHealth.ps1 -SiteUrl https://bluetrack.company.com -SqlServerInstance WIN-K5POLANERI5.Company.com`, and `SELECT TOP 5 * FROM dbo.etl_run_log ORDER BY 1 DESC;` (Import, Load, Snapshot).
2. **LDAPS from DCACYBSQL01** (backlog): the user runs `Tools\Test-BlueTrackLdaps.ps1` there, then adds the domain in BlueTrack.
3. **Live tests still owed** (backlog, "Built, but never proven"): SIEM forwarding with the real collector (framing, CEF fields, certificate, firewall); account classification against the real AD (attributes filled, LDAPS binding, run time; needs Access Group mappings and a discovery run first); OAuth2 SMTP with a real Microsoft 365 / Google tenant; the release script against GitHub; break-glass email end to end; Azure/AWS/Conjur round trips.
4. **Next release:** `Tools/New-BlueTrackRelease.ps1 -Version x.y.z -Push`, merge its PR, then `-Tag -Push` (D-226). The release notes' Upgrading section lists the server steps, including the .NET 10.0.13 Hosting Bundle.
5. **Backlog** (`Planning_Backlog.md`): API access for scripts (deferred), server-side localization and "use browser default" language (deferred), D-229 follow-ups (link two existing targets by hand; a resolved review row blocks deleting its target), and the real-data findings (Self-Hosted platforms, ignored properties, unused exports).

## Gotchas for the next session
- **Wait for CI before local suites or a redeploy.** CI runs on this host and rebuilds the shared `BlueTrackTest`; opening, pushing or merging a PR starts a run (`gh run list --status in_progress`). Overlaps on 2026-10-13 failed both runs twice: the delete/undelete test left TestAccount01 deleted, which broke later API tests. Re-run the CI run alone, then the local suite.
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
- **BlueTrackTest has no CyberArk accounts with an address**; feed-linking tests add a temporary `PRIVCLOUD` account and remove it (xUnit runs one test at a time).
- **Live SSO checks without a browser:** `Tools\Test-BlueTrackSso.ps1` (authentik's flow API; the lab account's Credential Provider values are in `Live-Testing.local.md`). For the two front-channel Single Logout legs use a browser: headless Chromium from `App/E2E/node_modules/playwright`, waiting for authentik's "Not you?" before filling the password. IIS writes its log (`C:\inetpub\logs\LogFiles\W3SVC2`) about a minute late.
- **After a reboot**, check SQL Server Agent too (the health check does).
- **Merge with `gh pr merge N --merge --delete-branch`** (the user's standing rule) unless another open PR is stacked on that branch; scripted wait-and-merge loops too.
- **A query that must not wait on in-flight rows has to seek past them** (D-234 lesson): CI's fresh database chose a clustered scan that blocked on an open transaction while the dev box used the index. Force the index; don't use `READPAST` where skipping a locked committed row loses data.
- **Stacked PRs:** merging one with `--delete-branch` closes the next; merge without `--delete-branch` and delete branches once the whole stack is merged. When an earlier PR changes the same doc lines, merge `main` into the first stacked branch and pass the merge down the stack.
- **CI runner wedge** (queued, runner idle, broker errors in `C:\actions-runner\_diag`): restart the runner service, after asking the user.
- **Rights cache lives in SQL Server** and outlasts a test run: a test that depends on a new grant clears it first (`UserRightsCache.InvalidateAllAsync`).
- **UI text is in message files** (D-227): new SPA text goes in `App/Web/src/locales/en/<area>.json`, never hard-coded; the key-coverage test fails otherwise (`Claude_Docs/Design_Localization.md`).
- **Release notes:** every user-visible change gets a line under Unreleased in `User_Docs/Release_Notes.md`, and one in `Planning_User-Docs-Backlog.md`.
