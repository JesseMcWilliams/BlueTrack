# BlueTrack: Handoff, 2026-10-14

> **Stage: Planning.** Point-in-time snapshot for the next Claude session, written at the end of 2026-10-13 (19:30 PDT). Supersedes `Archive_Planning_Handoff-2026-10-13.md`, which covers 2026-10-12 to mid 2026-10-13 (v1.0.0, SSO go-live; its PR table runs from #96). Once every open item below is closed, rename this to `Archive_Planning_Handoff-2026-10-14.md` with `git mv`.

## What was done
On 2026-10-13 after the v1.0.0 release: two audit findings closed, **Data Sources finished** (phases 3-4), UI requests (About, nav order, mapping paging, locale number format), the rest of the backlog that didn't need a real environment (D-214 to D-217, including break-glass alerts and **SAML Single Logout, proven live**), and **`Tools/`**, command-line checks against a real install. Everything is merged to `main` and deployed on the dev site. Nothing is released yet: it's all under **Unreleased** in `User_Docs/Release_Notes.md`.

| PR | Decisions | What |
|---|---|---|
| #109 | (none) | The previous handoff's first version; open findings moved to the backlog. |
| #110 | D-210 | Secrets Store and Credentials Test buttons audit-logged as `ConnectionTest` (script 11); password length no longer returned. An unreadable SAML SP key is a 503 and a Degraded health check, not a 500. |
| #111 | D-211 | Data Sources phase 3: CyberArk source settings on the page (`web.cyberark_source_config`, script 12); the nightly job runs `EXEC usp_Import_Configured;`; the installer seeds the row only when none exists. |
| #112 | D-212 | Phase 4a: Account inventory feeds (script 13): other systems' accounts at Discovered under a source system; auto-advance only for CyberArk accounts. |
| #113 | D-212 | Phase 4b: exact user name + address matches link feed accounts to CyberArk accounts (script 14), move them to Onboarded, hide them (Linked feed accounts filter). |
| #114 | D-212 | Phase 4c: Reports > Possible Account Matches (short name / DNS), Link and Dismiss; `Planning_Data-Sources.md` became `Design_Data-Sources.md`. |
| #115 | D-213 | About in the user menu, nav order (Reports, then Admin last), Application ↔ Safe Mapping paged; set `<Version>` at each release. |
| #116 | D-214 | Installer transcripts kept 90 days; reporting views and the daily snapshot skip linked feed accounts (script 15); Required Permission on the Edit Field page; `IAdDirectory` and a two-domain discovery test; manual steps for an SP certificate held by the App Pool account. |
| #117 | D-215 | Azure/AWS/Conjur stored credentials: User-scope DPAPI with random entropy; Azure/AWS 2 retries, 10 s timeout; Conjur token Base64-encoded as CyberArk's own .NET client does. Unproven live. |
| #118 | D-216 | Break-glass accounts on Identity Providers (script 16): their sign-in writes `BreakGlassLogon` and emails the BreakGlassLogon notification's recipients. |
| #119 | D-217 | SAML Single Logout, SP- and IdP-initiated (front-channel), proven against the lab authentik; two E2E test fixes (label clash, unawaited saves). |
| #120 | D-217 | `/api/auth/saml/slo` without a SAML message is a 400. |
| #121 | D-218 | Every count on the pages uses the browser language's number format (`formatCount`). |
| #122 | D-219 | `Tools/`: `Test-BlueTrackHealth.ps1`, `Test-BlueTrackSso.ps1`, `Test-BlueTrackLdaps.ps1`, `Test-BlueTrackIntegrations.ps1` (PowerShell, read-only; `Tools/README.md`). |

## State
- **`main`** at the #122 merge (`1870744`) plus this handoff's PR; last CI on `main` passed. v1.0.0 is still the latest release.
- **Suites** at the last full run: xUnit 659/659, Vitest 217/217, Playwright 73/73, PSScriptAnalyzer 0 (`Deploy/` and `Tools/`).
- **Dev site** (`https://bluetrack.company.com`, API at `/BlueTrack`): `main` as of #122, database scripts up to 16. `Tools\Test-BlueTrackHealth.ps1` gave 12 pass, 0 fail after the last deploy. Default sign-in method: SAML (changed on the page by the user at some point; the earlier handoff said Windows).
- **CyberArk sources** (page settings): both sources on, `C:\Data\PrivCloud` with undated file names, EVD `CyberArkSH`; the page's Test passes for all files. The nightly Import and Load job (02:00) runs `EXEC usp_Import_Configured;` and **hasn't run yet** in its new form.
- **SQL Server Agent** is running and set to Automatic (it had stayed stopped after the 2026-10-13 reboot). The stray job `BlueTrack ($(DatabaseName)) - Audit Log Purge` was deleted at the user's request.
- **Break-glass** isn't set up on the dev site: no accounts listed, no recipients or target role for the BreakGlassLogon notification (`Test-BlueTrackIntegrations.ps1` warns about both).
- **authentik** (lab details in `Live-Testing.local.md`): apps `bluetrack-saml` and `bluetrack-oidc`. The `bluetrack-saml` provider was changed for Single Logout (with the user's OK): SLS URL `https://bluetrack.company.com/BlueTrack/api/auth/saml/slo`, binding Redirect, logout method `frontchannel_native`, logout request and response signed. Before: no SLS URL, `frontchannel_iframe`, unsigned. Its invalidation flow is still the default (ends only BlueTrack's session at authentik).

## Open items
1. ~~**Check the first nightly import**~~ Done 2026-10-14: the 02:00 run failed (Privilege Cloud Users/Groups layout change, Msg 7301), fixed by D-228 and deployed; `usp_RunNightlyImportLoad` run by hand succeeded (Import, Load, Snapshot). Check the first scheduled run of the new one-step job (02:00 2026-10-15) with `Tools\Test-BlueTrackHealth.ps1`.
2. **LDAPS from DCACYBSQL01** (backlog): the user will run `Tools\Test-BlueTrackLdaps.ps1` there (copy the `Tools` folder), then add the domain in BlueTrack. Manual steps also in `Live-Testing.local.md`, "Pending check".
3. **Break-glass end to end** (optional): list an account on Identity Providers, give the BreakGlassLogon notification a target role or recipients, sign in as it, and check the email arrives. Only the audit event is covered by tests.
4. **Next release:** `Tools/New-BlueTrackRelease.ps1 -Version x.y.z -Push`, merge its PR, then `-Tag -Push` (D-226; `Tools/README.md`). Upgrade steps are in the release notes' Unreleased section.
5. **Backlog** (`Planning_Backlog.md`), all needing a real environment: `ApplicationPoolIdentity` with a remote SQL Server (an environment decision), a live round trip for Azure, AWS and Conjur, a real second AD domain, and the installer steps not yet run on a fresh server.

## Gotchas for the next session
- **Wait for CI before local suites or a redeploy.** CI runs on this host and rebuilds the shared `BlueTrackTest`; opening, pushing or merging a PR starts a run (`gh run list --status in_progress`). Overlaps on 2026-10-13 failed both runs twice: the delete/undelete test left TestAccount01 deleted, which broke later API tests. Re-run the CI run alone, then the local suite.
- **Redeploy from a clean `main`.** The installer builds the working copy. From Bash: `powershell -Command "& ./Install-BlueTrack.ps1 -Step Db.Backup,Db.Migrate,Build.Api,Build.Web,Iis.SiteRestart"` (with `-File` the step list arrives as one string). Run `Tools\Test-BlueTrackHealth.ps1` afterwards.
- **Never copy `Live-Testing.local.md` details into tracked files** (lab account, AppID, Safe, Object). On 2026-10-13 they reached a pushed branch and were removed with an amend and force-push before the PR; use placeholders in examples.
- **Backslashes through Bash** mangle heredocs and inline Python (`\r\n` became line breaks, `\04` an octal character, `DOMAIN\name` a line break). Write such text with the Write/Edit tools, then grep for control characters.
- **POSTs to the live API** need `X-Requested-With: XMLHttpRequest` (D-197).
- **E2E label clashes:** `getByLabel` is case-insensitive and matches substrings; after a navigation, wait for something only the new page has before filling (see `Reference_Lessons-Learned.md`, 2026-10-13). A UI test that changes shared data and undoes it must wait for each save.
- **One remaining one-off E2E flake:** "Admin deletes an account ... undeletes it in bulk" timed out once under load and passed on re-run.
- **BlueTrackTest has no CyberArk accounts with an address**; feed-linking tests add a temporary `PRIVCLOUD` account and remove it (xUnit runs one test at a time).
- **Live SSO checks without a browser:** `Tools\Test-BlueTrackSso.ps1` (authentik's flow API; the lab account's Credential Provider values are in `Live-Testing.local.md`). For the two front-channel Single Logout legs use a browser: headless Chromium from `App/E2E/node_modules/playwright`, waiting for authentik's "Not you?" before filling the password. IIS writes its log (`C:\inetpub\logs\LogFiles\W3SVC2`) about a minute late.
- **After a reboot**, check SQL Server Agent too (the health check does).
- **Stacked PRs:** merging one with `--delete-branch` closed the next; open the next PR after the previous one merges, or retarget first.
- **CI runner wedge** (queued, runner idle, broker errors in `C:\actions-runner\_diag`): restart the runner service, after asking the user.
- **Rights cache lives in SQL Server** and outlasts a test run: a test that depends on a new grant clears it first (`UserRightsCache.InvalidateAllAsync`).
- **Release notes:** every user-visible change gets a line under Unreleased in `User_Docs/Release_Notes.md`, and one in `Planning_User-Docs-Backlog.md`.
