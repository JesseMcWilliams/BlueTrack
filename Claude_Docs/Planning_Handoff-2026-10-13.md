# BlueTrack: Handoff, 2026-10-13

> **Stage: Planning.** Point-in-time snapshot for the next Claude session, covering 2026-10-12 to 2026-10-13. Supersedes `Archive_Planning_Handoff-2026-10-11.md` (its open items are carried into the list below). Once every item below is closed, rename to `Archive_Planning_Handoff-2026-10-13.md` with `git mv`.

## What was done
SSO went live against the lab Authentik, a round of security fixes shipped, and the dev site was redeployed and checked in a real browser. Everything is merged to `main`.

| PR | Decisions | What |
|---|---|---|
| #96 | (none) | Previous handoff; DCACYBSQL01 item closed. |
| #97 | D-201 | Lock timeout 1-720 min, default 30 on new installs (script 08); the intermittent `AccountDeletionTests` failure was a lock timeout of 0. Secrets-store re-save no longer overwrites the credential with `***`. API tests no longer read the live database at startup (`UseSetting`). |
| #98 | D-202 | Secrets backends: `https://` only; CP/CCP fallback cache expires (24 h), keyed on backend/AppID/URL, cleared on save; no redirects, 30 s timeout. Also exempted `App/Api/Secrets/` from `.gitignore`'s `Secrets/` rule, which had hidden two new files. |
| #99 | D-203 | Backup folder: broad groups' read access removed before each backup, or the backup is refused; deploy scripts set the folder's ACL; Backup Folder health check. |
| #100 | D-204 | Group-to-role mapping API and pages support OIDC and SAML providers. |
| #101 | D-205 | Sign-in page, Default sign-in method setting (script 09), direct addresses `/login/windows`, `/login/oidc`, `/login/saml`; SPA asks `/api/auth/session` before `/api/me`; SP certificate looked up in the App Pool account's store first. |
| #102 | D-205 | Sign-out sticks when Windows isn't the default (IIS keeps Windows auth on the connection). |
| #103 | (none) | SAML ACS read the response wrongly (500 after every sign-in); no Windows prompt on SSO addresses; OIDC display name fallback; `-Step Iis.Site` can rebind a certificate. |

## State
- `main` at `f1bf59b`; no open PRs; `main` is the only branch on GitHub. Last CI on `main` passed.
- Suites at the last run: xUnit 609/609, Vitest 179/179, Playwright 70/70, PSScriptAnalyzer 0.
- **Dev site** (`https://bluetrack.company.com`, SPA at the root, API at `/BlueTrack`): deployed from `main`, scripts 08-09 applied, default sign-in method Windows. New TLS certificate from this host's CA (SAN `bluetrack.company.com`, RSA 3072, thumbprint `28DC0E33...`, saved in the install answers); the old one is unbound but still in `LocalMachine\My`. `C:\Data\Backups` limited to Administrators, SYSTEM, the SQL Server service account and `BlueTrackSvc$`.
- **Authentik** (lab details in `Live-Testing.local.md`): apps `bluetrack-saml` and `bluetrack-oidc`, groups `BlueTrack-Admin`/`-Analyst`/`-Approver`/`-Auditor`/`-Viewer` mapped to the matching roles. Verified with the lab account: full SAML and OIDC round trips, no Windows challenge on the SSO addresses, sign-out.

## Open items
1. **User check:** sign in as `adm.jesse` through `/login/saml` (or the Authentik tile) with MFA; expect Admin. Windows sign-in from a machine other than this host.
2. **LDAPS:** binds from this DC to itself fail; Authentik's LDAPS sync from another machine works. Run the bind from DCACYBSQL01 (`Testing_Audit-Findings.md`).
3. **Open findings** (`Testing_Audit-Findings.md`): Test buttons not audit-logged (Low); unreadable SP key gives a 500 at SAML login (Low); CI runs approved PR code on this host (accepted); admin error text (accepted).
4. **Backlog** (`Planning_Backlog.md`): Data Sources phases 3-4 (next batch, per the user); SP certificate enrolled by the App Pool account; Azure/AWS/Conjur hardening before real use; break-glass; multi-domain AD; SAML Single Logout.
5. **Release:** user docs first (`Planning_User-Docs-Backlog.md`, many 2026-10-12/13 rows), then the signed release tag (`Deploy/README.md`, "Making a signed release"), which the user signs.

## Suggested order
1, then 2, then 5 (docs, then release). 4's Data Sources work is the batch after that.

## Gotchas for the next session
- **Stacked PRs:** merging one with `--delete-branch` closed the next. Retarget the next PR to `main` first; a retarget alone doesn't start CI (push, or close and reopen). See `Reference_Lessons-Learned.md`.
- **CI runner wedge** happened again (queued, runner idle, broker errors in `C:\actions-runner\_diag`); restarting the runner service fixed it (ask the user first).
- **Check new files are tracked:** `git status --short --ignored` after adding files under a folder named like a secrets folder.
- **Live sign-in tests:** headless Chromium from `App/E2E/node_modules/playwright`, with `--auth-server-allowlist=*.company.com` for Windows auth; the lab Authentik account's password comes from the Credential Provider and is passed only through the process environment.
- **Installer partial runs:** `-Step Build.Api,Iis.SiteRestart` redeploys the API without an `iisreset` (which also restarts PVWA on this host).
- **Backslashes through Bash** still mangle heredocs and inline Python; write such text with the Write/Edit tools or a script file.
