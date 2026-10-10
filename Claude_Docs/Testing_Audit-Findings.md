# BlueTrack: Audit Findings (open)

> **Stage: Testing.** Open findings from documentation, deployment and live-verification audits. Add new findings here; when one is resolved, move it to `Archive_Testing_Audit-Findings.md`.

## Deployment

- [ ] **Found 2026-09-24, while verifying D-162/D-163 end to end with a real SQL grant (D-164)**: `Database/41_BlueTrack_GrantAppServiceAccountAccess.sql` correctly grants the app's own database permissions, but the real host still fails every DB-touching request with `Login failed for user 'DOMAIN\HOSTNAME$'. Reason: Could not find a login matching the name provided` -- not a permissions error, and not fixed by the login existing, being enabled, having the correct SID (confirmed three independent ways), or toggling the App Pool's `loadUserProfile`. Matches a known, general limitation of `ApplicationPoolIdentity` for outbound Windows-authenticated connections to remote services like SQL Server. **Not fixed** -- needs a real environment decision (switch the App Pool to a dedicated domain service account or gMSA), not something to guess at further; see D-164 and `User_Docs/Admin_OperationsGuide.md`'s "Granting the app's own SQL Server access" section.

## Security review 2026-10-10 (open)

Five-area review (auth/session, SQL/data access, uploads/feeds/secrets, frontend/HTTP, deployment/CI). Fixed items are in `Archive_Testing_Audit-Findings.md` under the same date (D-197).

- [ ] **Medium (was High) -- CI runs pull-request code on the self-hosted runner (this host).** `.github/workflows/ci.yml` triggers on `pull_request` with `runs-on: [self-hosted, ...]` and the repo is public. **Mitigated 2026-10-10:** the repository now requires approval before any outside contributor's workflow runs, and `ci.yml` has `permissions: contents: read` and `actions/checkout` pinned to a commit. **Remaining:** approving a pull request still runs its code here, so review a PR's changes (especially to `ci.yml`, `package.json`, tests and scripts) before approving its run; making the repository private, which GitHub recommends for self-hosted runners, was declined for now.
- [ ] **Info -- admin-only troubleshooting endpoints return exception text** (backup, credential test, SMTP test, feed test): kept on purpose (D-197), since that text is how an admin diagnoses the failure.
- [ ] **Low -- `Update-BlueTrack.ps1` trusts TLS only for what it downloads:** nothing checks a signed commit or a published checksum. Needs signed tags or release assets with a SHA-256 to check against (D-198 pinned the prerequisite installers' signers only).
- [ ] **Open decision -- the Deployment page's "Backup App" can't work under the least-privilege grant:** checked on the dev host 2026-10-10, the app account has `db_datareader`/`db_datawriter` only and no `BACKUP DATABASE`. Either grant `db_backupoperator` in `Database/Manual/02` (backups go only to `FileAccess:AllowedRoots`, D-197) or remove the button.
- [ ] **Needs checking:** backup file ACLs on disk (`BlueTrack.Rollback.psm1` uses inherited permissions); `App/Api/Secrets/**` providers (a reviewer's read permission was blocked there); LDAPS binding once a DC with LDAPS is available (D-198).
