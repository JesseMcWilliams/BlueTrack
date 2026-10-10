# BlueTrack: Audit Findings (open)

> **Stage: Testing.** Open findings from documentation, deployment and live-verification audits. Add new findings here; when one is resolved, move it to `Archive_Testing_Audit-Findings.md`.

## Deployment

- [ ] **Found 2026-09-24, while verifying D-162/D-163 end to end with a real SQL grant (D-164)**: `Database/41_BlueTrack_GrantAppServiceAccountAccess.sql` correctly grants the app's own database permissions, but the real host still fails every DB-touching request with `Login failed for user 'DOMAIN\HOSTNAME$'. Reason: Could not find a login matching the name provided` -- not a permissions error, and not fixed by the login existing, being enabled, having the correct SID (confirmed three independent ways), or toggling the App Pool's `loadUserProfile`. Matches a known, general limitation of `ApplicationPoolIdentity` for outbound Windows-authenticated connections to remote services like SQL Server. **Not fixed** -- needs a real environment decision (switch the App Pool to a dedicated domain service account or gMSA), not something to guess at further; see D-164 and `User_Docs/Admin_OperationsGuide.md`'s "Granting the app's own SQL Server access" section.

## Security review 2026-10-10 (open)

Five-area review (auth/session, SQL/data access, uploads/feeds/secrets, frontend/HTTP, deployment/CI). Fixed items are in `Archive_Testing_Audit-Findings.md` under the same date (D-197).

- [ ] **Medium (was High) -- CI runs pull-request code on the self-hosted runner (this host).** `.github/workflows/ci.yml` triggers on `pull_request` with `runs-on: [self-hosted, ...]` and the repo is public. **Mitigated 2026-10-10:** the repository now requires approval before any outside contributor's workflow runs, and `ci.yml` has `permissions: contents: read` and `actions/checkout` pinned to a commit. **Remaining:** approving a pull request still runs its code here, so review a PR's changes (especially to `ci.yml`, `package.json`, tests and scripts) before approving its run; making the repository private, which GitHub recommends for self-hosted runners, was declined for now.
- [ ] **Medium -- SMTP settings can send a stored credential to any host.** Saving SMTP config needs only `ManageNotifications`; any `SmtpCredentialKey` can be chosen and, with STARTTLS off, its password goes to the configured host in clear (`SmtpNotificationSender.cs:60-68`). Only a jump for a custom role with `ManageNotifications` but not `ManageCredentials`. Fix: require `ManageCredentials` to change host/credential; refuse Basic auth without TLS.
- [ ] **Low -- the app's database account can rewrite the audit log** (`db_datawriter`, EXECUTE on whole schemas, `Database/Manual/02`). Fix: `DENY UPDATE, DELETE` on the audit tables; EXECUTE only on the procedures the API calls.
- [ ] **Low -- settings that do nothing:** field-metadata `RequiredPermission` is saved but never enforced; LDAP `UseSsl` is stored but never applied (`LdapContextFactory`).
- [ ] **Low -- credential, LDAP and SMTP changes aren't audit-logged** (`CredentialsController`, `NotificationsController`).
- [ ] **Low -- no upload size or row limits** on the CSV imports (`CsvFileReader` reads every row into memory).
- [ ] **Low -- the OIDC/DevFakeAuth cookie doesn't set `SecurePolicy = Always`** (`AuthenticationExtensions.cs`).
- [ ] **Low -- `POST /api/reports/risk-score/recalculate` is gated by a view permission** (`ViewRiskReport`).
- [ ] **Low -- downloads aren't pinned to a signer or signed commit:** `Update-BlueTrack.ps1` trusts TLS only; `BlueTrack.Prereqs.psm1` checks Authenticode status but not the signer.
- [ ] **Info -- admin-only troubleshooting endpoints return exception text** (backup, credential test, SMTP test, feed test): kept on purpose (D-197), since that text is how an admin diagnoses the failure.
- [ ] **Needs checking:** ITfoxtec's SAML audience/replay checks (`AllowedAudienceUris` is empty); the app account's real SQL rights (the backup feature needs more than `Manual/02` grants); backup file ACLs on disk; `App/Api/Secrets/**` providers (a reviewer's read permission was blocked there).
