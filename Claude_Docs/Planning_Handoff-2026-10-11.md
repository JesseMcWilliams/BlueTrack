# BlueTrack: Handoff, 2026-10-11

> **Stage: Planning.** Point-in-time snapshot for the next Claude session, covering 2026-10-09 to 2026-10-12. Once every item below is closed, rename to `Archive_Planning_Handoff-2026-10-11.md` with `git mv`.

## What was done
A release-readiness pass: the SQL scripts were consolidated into a baseline, the browser sign-in loop was fixed for good, and two rounds of security-review fixes shipped. Everything below is merged to `main`. Earlier PRs from 2026-10-09 (#66 to #83, D-178 to D-191) are in `Design_Decision-Register.md`.

| PR | Decisions | What |
|---|---|---|
| #84 | D-192 | `Update-BlueTrack.ps1`: download a release or branch and offer to redeploy. |
| #85 | D-122, D-123, D-193, D-194 | Access group uniqueness per server; Targets and `AddressDerived` links from account addresses (scripts 50, 51); 409 for conflicts across the API. |
| #86 | D-195 | Scripts 01-51 consolidated into a six-script baseline (`Database/01`-`06`); Migrator records it as applied on an old database; manual scripts in `Database/Manual/` (numbered 01-06 in run order); `Deploy/Compare-BlueTrackSchema.ps1` (drift check). Tag `pre-sql-baseline` marks the last old-script commit. |
| #87 | (none) | Every `Deploy/` script passes PSScriptAnalyzer with 0 findings. |
| #88 | (none) | npm/Vite output shown in UTF-8 during install (no more `Γ£ô`). |
| #89 | D-196 | The SPA calls `/BlueTrack/api/...` directly; the site-root `/api` rewrite is gone (it caused the sign-in loop, `401.1` / Win32 85). Confirmed fixed on DCACYBSQL01 (#90). |
| #91 | D-197 | Security review, part 1: `ViewDashboard` enforced on 18 read actions; local-only sign-in redirects; CSRF header (`X-Requested-With`) on unsafe `/api` calls; rights cache max 15 min and cleared on role/mapping/provider change; IdP claims stripped; `FileAccess:AllowedRoots`; security headers; deploy hardening; CI token read-only, checkout pinned. |
| #92 | D-198 | Security review, part 2: SMTP credential rules; audit logging for SMTP/credential/LDAP changes; append-only audit log and `db_backupoperator` for the app's SQL account; field `RequiredPermission` enforced; LDAP `UseSsl` applied; 250,000-row / 30 MB import limits; HTTPS-only auth cookie; SAML audience and replay checks; installer signer pinning. Also fixed #91's `sqlcmd -C` splatting bug. |
| #93 | D-199, D-200 | Updates install only signed release tags (`-AllowUnsignedBranch` for branches); scheduled backups (daily time, keep N days; script 07; history on the Deployment page). |
| #94 | (none) | Backlog: DCACYBSQL01's outstanding steps; shipped items removed. |
| #95 | (none) | This handoff doc. |

## State
- `main` at `7fad0f4`; no open PRs, and `main` is the only branch on GitHub. Last CI on `main` passed.
- Checked against the aPeTemplate (`ape-project-setup` skill) on 2026-10-12: `CLAUDE.md`, `.gitignore` and the doc layout already match it; nothing changed. The template's `Testing_Plan.md` isn't added because `Design_Testing-Strategy.md` covers it.
- Suites at the last run: xUnit 568/568, Vitest 167/167, Playwright 70/70, PSScriptAnalyzer 0.
- This dev host: live `BlueTrack` database and IIS site match `main` (script 07, D-198 grants, security headers). Scheduled backups are off here (no time set). HSTS is on here, since its certificate isn't self-signed.
- No signed release exists yet, so updates are branch-only (`-AllowUnsignedBranch`) until one is made (`Deploy/README.md`, "Making a signed release").
- `User_Docs/` hasn't been updated for this session's changes; `Planning_User-Docs-Backlog.md` lists them.

## Open items
1. **Done 2026-10-12:** the user brought DCACYBSQL01 up to `main`.
2. **Open security findings** (`Testing_Audit-Findings.md`): CI on the self-hosted runner still runs an approved PR's code here (the repo stays public by the user's choice); admin troubleshooting endpoints show error text on purpose; still to check -- backup file ACLs, the `App/Api/Secrets/**` providers, LDAPS against a real DC.
3. **Never proven live** (`Planning_Backlog.md`): `Db.AppPoolAccess` and `Iis.Reset` on a fresh server; SAML/OIDC against a real IdP; Azure Key Vault, AWS, Conjur backends; break-glass; multi-domain AD discovery.
4. **Requested, not started:** Data Sources phases 3-4 (`Planning_Data-Sources.md`).
5. **One intermittent test failure seen once:** `AccountDeletionTests.DeleteThenUndelete_...` deleted 1 of 2 accounts on one full run; three reruns passed. Cause not found.
6. **No signed release tag yet.** The user signs it (`Deploy/README.md`, "Making a signed release"); until then updates need `-AllowUnsignedBranch`.
7. **User docs:** `User_Docs/` and then `Published_Docs/` need this session's changes from `Planning_User-Docs-Backlog.md` before the release.

## Suggested order
Set by the user on 2026-10-12: next round is 2 (this dev host is a domain controller, so LDAPS can be checked here), 3 (SAML/OIDC against the lab's Authentik IdP) and 5. Then 7, then 6 (the release). 4 is the batch after that.

## Gotchas for the next session
- **Backslashes through Bash:** heredocs, `sed` and inline Python mangled `\` again (Python also reads `\U` in `.\Update-...` as an escape). Use the Edit/Write tools for any text with backslashes.
- **A one-element array from `if` isn't an array when splatted** to a native command: type it `[string[]]` (lesson in `Reference_Lessons-Learned.md`). Run every installer step a change touches, not just the feature's.
- **The CI runner isn't an administrator:** tests can't write under `Program Files`; give a test folder to SQL Server with `icacls` instead (`ScheduledBackupTests`).
- **CI stuck "queued" with the runner idle:** the runner lost its broker connection; ask the user, then restart the `actions.runner.*` service.
- **`gh run watch` right after a push** can start before the run exists; find the run by the pushed commit's SHA first.
- **E2E sets `X-Requested-With` globally** (Playwright `extraHTTPHeaders`), so it doesn't prove the SPA adds the header; `apiBase.test.js` and a real-browser check against IIS do.
- **Permission rules block reading `.env*` files** (`App/Web/.env.production` holds only `VITE_API_BASE=/BlueTrack`).
- **Stale `origin/*` branches** show in `git branch -a` after merges with `--delete-branch`; `git fetch --prune` clears them (`git ls-remote --heads origin` shows what really exists).
- **The aPeTemplate skill is named `ape-project-setup`**; "aPeTemplate" isn't a skill name.
- **Changing a database grant** needs `-Step Db.AppPoolAccess` re-run on each install; a normal update doesn't re-apply `Database/Manual/` scripts.
