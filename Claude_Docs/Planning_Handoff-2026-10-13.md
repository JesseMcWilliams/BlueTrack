# BlueTrack: Handoff, 2026-10-13

> **Stage: Planning.** Point-in-time snapshot for the next Claude session, covering 2026-10-12 to 2026-10-13. Supersedes `Archive_Planning_Handoff-2026-10-11.md` (its open items are carried into the list below). Once every item below is closed, rename to `Archive_Planning_Handoff-2026-10-13.md` with `git mv`.

## What was done
SSO went live against the lab Authentik, two rounds of fixes shipped, the user docs and published guides were brought up to date, and **v1.0.0 was released** (signed tag, verified by GitHub). Everything is merged to `main`.

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
| #108 | D-207, D-208, D-209 | Getting Started and Administrator Settings guides; `Release_Notes.md`; no mapping profile chosen now means the template's columns, with a profile choice on Bulk Actions; Analysts get `ViewRiskReport` (script 10); Idle Timeout signs people out (SPA timer, and idle OIDC/SAML sessions refused on the server; 5-720 min). |

## State
- `main` at `d5492cb`, tagged **v1.0.0** (signed with the user's SSH key; GitHub `verified=true`). Release: https://github.com/JesseMcWilliams/BlueTrack/releases/tag/v1.0.0, with the five `.docx` guides attached. Last CI on `main` passed.
- **Open PR: #109** (this doc and the findings-to-backlog move, docs only, CI passed), branch `2026-10-13-handoff-and-backlog`, checked out locally. Waiting for the user to approve the merge; then `gh pr merge 109 --merge --delete-branch` and `git fetch origin main:main` (or switch to `main`).
- **The host was rebooted** after this was written (requested 2026-10-13). After it's back: CI runner, `ssh-agent`, IIS and SQL Server all start automatically (checked). Confirm with `Get-Service 'actions.runner.*', ssh-agent, W3SVC, MSSQLSERVER`, check the dev site answers (`Invoke-RestMethod -UseDefaultCredentials https://bluetrack.company.com/BlueTrack/api/me`), and that the signing key is still in the agent (`C:\Windows\System32\OpenSSH\ssh-add.exe -l`; the Windows agent keeps keys across reboots). A CI run stuck "queued" after the reboot is the runner wedge below.
- `Update-BlueTrack.ps1 -Release v1.0.0` was run on the dev host: it verified the signed tag and downloaded it to `C:\Data\BlueTrack_Download\BlueTrack-v1.0.0` (not redeployed; the dev site already runs this code). Other installs can now update without `-AllowUnsignedBranch`.
- Suites at the last run: xUnit 614/614, Vitest 193/193, Playwright 71/71, PSScriptAnalyzer 0.
- **Dev site** (`https://bluetrack.company.com`, SPA at the root, API at `/BlueTrack`): v1.0.0 code, scripts 08-10 applied, default sign-in method Windows, Idle Timeout 30. TLS certificate from this host's CA (SAN `bluetrack.company.com`, RSA 3072, thumbprint `28DC0E33...`); the old one is unbound but still in `LocalMachine\My`. `C:\Data\Backups` limited to Administrators, SYSTEM, the SQL Server service account and `BlueTrackSvc$`.
- **Authentik** (lab details in `Live-Testing.local.md`): apps `bluetrack-saml` and `bluetrack-oidc`, groups `BlueTrack-Admin`/`-Analyst`/`-Approver`/`-Auditor`/`-Viewer`. Verified: SAML and OIDC round trips, the user's own MFA sign-in as `adm.jesse`, no Windows challenge on the SSO addresses, sign-out, and an idle SAML session refused on the server.
- **Release notes:** `User_Docs/Release_Notes.md` has an empty **Unreleased** section; CLAUDE.md asks for a line there with every user-visible change.
- **Open findings** were moved to `Planning_Backlog.md` ("From the audit findings") at the user's request; `Testing_Audit-Findings.md` is empty and still takes new findings.

## Open items
1. **LDAPS from DCACYBSQL01** (backlog, "From the audit findings"): the user will run it later. DCACYBSQL01 is in a different domain with no trust, so the check binds with an account in the DC's domain, not a trusted connection. The step-by-step (certificate export and import, a bind test outside BlueTrack, the BlueTrack LDAP settings, and what a pass looks like) is in `Live-Testing.local.md`, "Pending check". The user will send back the bind test's output or the discovery warning.
2. **Data Sources phases 3-4**: the next batch, per the user (`Design_Data-Sources.md`).
3. **Backlog** (`Planning_Backlog.md`): SP key 500, Test buttons not audit-logged, `ApplicationPoolIdentity` with remote SQL, SP certificate enrolled by the App Pool account, Required Permission not editable in the UI, Azure/AWS/Conjur hardening, SAML Single Logout, break-glass, multi-domain AD.

## Suggested order
2 is the next batch. 1 whenever the user can reach DCACYBSQL01. Take items from 3 as they fit, starting with the two small Lows (SP key 500, Test-button audit).

## Gotchas for the next session
- **Release notes:** every user-visible change gets a line under Unreleased in `User_Docs/Release_Notes.md`; at a release that becomes the version's section, `python Published_Docs/build_published_docs.py` rebuilds the `.docx` guides, and the release attaches them.
- **Stacked PRs:** merging one with `--delete-branch` closed the next. Retarget the next PR to `main` first; a retarget alone doesn't start CI (push, or close and reopen). See `Reference_Lessons-Learned.md`.
- **CI runner wedge** happened twice (queued, runner idle, broker errors in `C:\actions-runner\_diag`); restarting the runner service fixed it (ask the user first).
- **Check new files are tracked:** `git status --short --ignored` after adding files under a folder named like a secrets folder.
- **Rights cache lives in SQL Server** and outlasts a test run: a test that depends on a new grant must clear it first (`UserRightsCache.InvalidateAllAsync`).
- **Live sign-in tests:** headless Chromium from `App/E2E/node_modules/playwright`, with `--auth-server-allowlist=*.company.com` for Windows auth. Make requests from a page in the browser: Playwright's own request client uses Node's TLS, which doesn't trust the company CA. The lab Authentik account's password comes from the Credential Provider and is passed only through the process environment; keep Playwright error dumps out of the output (they print cookies).
- **Installer partial runs:** `-Step Build.Api,Build.Web,Iis.SiteRestart` redeploys without an `iisreset` (which also restarts PVWA on this host); add `Db.Backup,Db.Migrate` for a new SQL script.
- **Backslashes through Bash** still mangle heredocs and inline Python; write such text with the Write/Edit tools or a script file.
