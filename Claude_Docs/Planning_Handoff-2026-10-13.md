# BlueTrack: Handoff, 2026-10-13

> **Stage: Planning.** Point-in-time snapshot for the next Claude session, covering 2026-10-12 to 2026-10-13 (updated at the end of 2026-10-13). Supersedes `Archive_Planning_Handoff-2026-10-11.md` (its open items are carried into the list below). Once every item below is closed, rename to `Archive_Planning_Handoff-2026-10-13.md` with `git mv`.

## What was done
SSO went live against the lab Authentik, the user docs and published guides were brought up to date, and **v1.0.0 was released** (signed tag, verified by GitHub). Later on 2026-10-13: two audit findings closed, **Data Sources finished** (phases 3-4), and three UI requests (About, nav order, mapping paging). Everything through #114 is merged to `main` and deployed on the dev site.

| PR | Decisions | What |
|---|---|---|
| #96 | (none) | Previous handoff; DCACYBSQL01 item closed. |
| #97 | D-201 | Lock timeout 1-720 min, default 30 on new installs (script 08); the intermittent `AccountDeletionTests` failure was a lock timeout of 0. Secrets-store re-save no longer overwrites the credential with `***`. API tests no longer read the live database at startup (`UseSetting`). |
| #98 | D-202 | Secrets backends: `https://` only; CP/CCP fallback cache expires (24 h), keyed on backend/AppID/URL, cleared on save; no redirects, 30 s timeout. `.gitignore` exempts `App/Api/Secrets/` (its `Secrets/` rule had hidden two new files). |
| #99 | D-203 | Backup folder: broad groups' read access removed before each backup, or the backup is refused; deploy scripts set the folder's ACL; Backup Folder health check. |
| #100 | D-204 | Group-to-role mapping API and pages support OIDC and SAML providers. |
| #101 | D-205 | Sign-in page, Default sign-in method (script 09), direct addresses `/login/windows`, `/login/oidc`, `/login/saml`; SPA asks `/api/auth/session` before `/api/me`; SP certificate looked up in the App Pool account's store first. |
| #102 | D-205 | Sign-out sticks when Windows isn't the default (IIS keeps Windows auth on the connection). |
| #103 | (none) | SAML ACS read the response wrongly (500 after every sign-in); no Windows prompt on SSO addresses; OIDC display name fallback; `-Step Iis.Site` can rebind a certificate. |
| #104 | (none) | The first version of this handoff. |
| #105 | (none) | Every `User_Docs/` guide brought up to date (36 backlog rows); SP certificate help text. |
| #106 | D-206 | User menu (the user's name; Profile & Preferences, formerly My Profile; Sign out). |
| #107 | (none) | `Published_Docs/build_published_docs.py` builds the `.docx` guides from `User_Docs/`; new `Developer_HostSetup.md`. |
| #108 | D-207, D-208, D-209 | Getting Started and Administrator Settings guides; `Release_Notes.md`; mapping profile choice on Bulk Actions; Analysts get `ViewRiskReport` (script 10); Idle Timeout signs people out. |
| #109 | (none) | This handoff's first version; open findings moved to the backlog. |
| #110 | D-210 | Secrets Store and Credentials Test buttons audit-logged as `ConnectionTest` (script 11), password length no longer returned; an unreadable SAML SP key gives a 503 and a Degraded health check instead of a 500. |
| #111 | D-211 | Data Sources phase 3: CyberArk source settings on the page (`web.cyberark_source_config`, script 12); the nightly job runs `EXEC usp_Import_Configured;`; the installer seeds the row only when none exists. |
| #112 | D-212 | Phase 4a: Account inventory feeds (script 13): other systems' accounts at Discovered under a source system; auto-advance only for CyberArk accounts. |
| #113 | D-212 | Phase 4b: exact user name + address matches link feed accounts to CyberArk accounts (script 14), move them to Onboarded, hide them (Linked feed accounts filter). |
| #114 | D-212 | Phase 4c: Reports > Possible Account Matches (short name / DNS), Link and Dismiss; `Planning_Data-Sources.md` became `Design_Data-Sources.md`. |
| #115 | D-213 | About in the user menu (version), nav order (Reports, then Admin last), Application ↔ Safe Mapping paged; note to set `<Version>` at each release; this update of the handoff. |

## State
- `main` at the #114 merge (`31f94a0`); #115 open on `2026-10-13-nav-about-mapping-paging` until merged. Last CI on `main` passed. v1.0.0 is the latest release; everything since is under **Unreleased** in `User_Docs/Release_Notes.md`.
- **Dev site** (`https://bluetrack.company.com`, API at `/BlueTrack`): #114's code, scripts 08-14 applied. The nightly job (`BlueTrack (BlueTrack) - Import and Load`, 02:00) was recreated by `Db.NightlyJob`: its Import step is `EXEC usp_Import_Configured;`. CyberArk sources saved: both on, `C:\Data\PrivCloud`, EVD `CyberArkSH`; the file names were since changed on the page to undated ones (e.g. `Export Entitlements.csv`). The user put the exports in the folder on 2026-10-13, and the page's Test passes for all of them.
- **Not yet proven:** a real import through `usp_Import_Configured` (the first is tonight's 02:00 run; check its history in `msdb.dbo.sysjobhistory`), and an account inventory feed against real files (none is set up on the dev site).
- Suites at the last run: xUnit 649/649, Vitest 208/208, Playwright 73/73, PSScriptAnalyzer 0.
- **Authentik** (lab details in `Live-Testing.local.md`): apps `bluetrack-saml` and `bluetrack-oidc`, groups `BlueTrack-Admin`/`-Analyst`/`-Approver`/`-Auditor`/`-Viewer`; SAML and OIDC verified end to end.
- **Authentik `bluetrack-saml` provider changed for Single Logout (D-217, 2026-10-13, with the user's OK):** SLS URL `https://bluetrack.company.com/BlueTrack/api/auth/saml/slo`, SLS binding Redirect, logout method `frontchannel_native`, sign logout request and response on. Before: no SLS URL, `frontchannel_iframe`, both unsigned. The helper `Ak.ps1` (Authentik API session via the Credential Provider) was copied from an earlier session's scratchpad.
- The stray SQL Agent job `BlueTrack ($(DatabaseName)) - Audit Log Purge` (no steps, never ran) was deleted on 2026-10-13 at the user's request.

## Open items
1. **Check tonight's nightly import** (02:00 2026-10-14): the first run through `usp_Import_Configured`. A failure names the file or database.
2. **LDAPS from DCACYBSQL01** (backlog, "From the audit findings"): the user will run it later; steps in `Live-Testing.local.md`, "Pending check".
3. **Next release:** set `<Version>` in `App/Api/BlueTrack.Api.csproj` to the release's version before tagging, turn Unreleased into the version's section, rebuild `Published_Docs/`. Upgrade step for existing installs: `-Step Db.NightlyJob` once (in the release notes).
4. **Backlog** (`Planning_Backlog.md`): Power BI views double-count linked feed accounts, `ApplicationPoolIdentity` with remote SQL, SP certificate enrolled by the App Pool account, Required Permission not editable in the UI, Azure/AWS/Conjur hardening, SAML Single Logout, break-glass, multi-domain AD.

## Gotchas for the next session
- **Release notes:** every user-visible change gets a line under Unreleased in `User_Docs/Release_Notes.md`; at a release that becomes the version's section, `<Version>` is set, and `python Published_Docs/build_published_docs.py` rebuilds the `.docx` guides.
- **Installer partial runs from Bash:** pass the steps through `powershell -Command "& ./Install-BlueTrack.ps1 -Step A,B"`; with `-File` the comma list arrives as one string ("Unknown step(s)"). The installer builds from the working copy, so redeploy from a clean `main` (stash or commit other work first). `-Step Db.Backup,Db.Migrate,Build.Api,Build.Web,Iis.SiteRestart` applies new scripts without an `iisreset` (which also restarts PVWA on this host).
- **POSTs to the live API** need `X-Requested-With: XMLHttpRequest` (D-197 CSRF check); without it they're 403.
- **BlueTrackTest has no CyberArk accounts with an address** (its seed is `DISCOVERY` accounts), so feed-linking tests add a temporary `PRIVCLOUD` account and remove it; xUnit runs one test at a time, so that's safe.
- **Stacked PRs:** merging one with `--delete-branch` closed the next; retarget first, or (as on 2026-10-13) open the next PR only after the previous one merges.
- **After a reboot, check SQL Server Agent too** (`Get-Service SQLSERVERAGENT`): it was Manual and stayed stopped after the 2026-10-13 reboot, so no nightly job would have run; the user set it to Automatic and started it.
- **CI runner wedge** (queued, runner idle, broker errors in `C:\actions-runner\_diag`): restart the runner service (ask the user first). A run queued behind `main`'s own run is normal: there's one runner.
- **Check new files are tracked:** `git status --short --ignored` after adding files under a folder named like a secrets folder.
- **Rights cache lives in SQL Server** and outlasts a test run: a test that depends on a new grant must clear it first (`UserRightsCache.InvalidateAllAsync`).
- **Live sign-in tests:** headless Chromium from `App/E2E/node_modules/playwright`, with `--auth-server-allowlist=*.company.com` for Windows auth; make requests from a page in the browser (Node's TLS doesn't trust the company CA). Keep Playwright error dumps out of the output (they print cookies).
- **Backslashes through Bash** mangle heredocs and inline Python (`\r\n` became real line breaks, `\04` an octal character, several times on 2026-10-13); write such text with the Write/Edit tools, then grep for control characters.
- **Another one-off E2E flake** (PR C's run): "Identity Providers admin page > Admin can create, edit, and delete a provider" stayed on `/new` after Save once; passed alone and in a full re-run.
- **One-off E2E flake:** "Admin deletes an account ... undeletes it in bulk" failed once in a full run (5.7 s) and passed on re-run, alone and in the full suite.
