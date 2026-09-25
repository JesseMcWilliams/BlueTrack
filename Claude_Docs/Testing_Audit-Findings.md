# BlueTrack: Audit Findings (open)

> **Stage: Testing.** Open findings from documentation, deployment and live-verification audits. Add new findings here; when one is resolved, move it to `Archive_Testing_Audit-Findings.md`.

## Deployment

- [ ] **Found 2026-09-24, while verifying D-162/D-163 end to end with a real SQL grant (D-164)**: `Database/41_BlueTrack_GrantAppServiceAccountAccess.sql` correctly grants the app's own database permissions, but the real host still fails every DB-touching request with `Login failed for user 'DOMAIN\HOSTNAME$'. Reason: Could not find a login matching the name provided` -- not a permissions error, and not fixed by the login existing, being enabled, having the correct SID (confirmed three independent ways), or toggling the App Pool's `loadUserProfile`. Matches a known, general limitation of `ApplicationPoolIdentity` for outbound Windows-authenticated connections to remote services like SQL Server. **Not fixed** -- needs a real environment decision (switch the App Pool to a dedicated domain service account or gMSA), not something to guess at further; see D-164 and `User_Docs/Admin_OperationsGuide.md`'s "Granting the app's own SQL Server access" section.
