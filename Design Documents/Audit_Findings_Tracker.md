# BlueTrack — Audit Findings Tracker

A checklist of findings from the 2026-09-05 documentation and deployment-methodology audit (two background research passes over every `Design_*.md` doc plus the current de facto deployment process), so progress against them is trackable over time rather than living only in conversation history. Check items off as they're resolved; add new findings here rather than letting them live only in chat.

## Design documentation — staleness

- [x] `Design_Testing_Strategy.md`: Implementation Status test counts were stale (claimed 57 xUnit/1 Vitest file/1 E2E spec; actual ~197 xUnit facts, 2 Vitest files, 7 E2E spec files) — updated.
- [x] `Design_Testing_Strategy.md`: Open Question #2 ("Account Progress editing coverage still open") was already resolved by `account-progress-and-risk-exceptions.spec.js` — closed out.
- [x] `Design_Application_Structure.md`: "not verified in an actual browser, no automation tooling" claim was false since Playwright was added — corrected.
- [x] `Design_Application_Structure.md`: missing a Dashboard.vue Implementation Status entry despite D-99 pointing at this doc — added.
- [x] `Design_Application_Structure.md`: `app_config` schema listing was missing `LockTimeoutMinutes`/`ExceptionIdPattern`/`ExceptionIdSequenceYear`/`ExceptionIdNextSequence` — updated to match `Database/12`/`15`.
- [x] `Design_Authorization_Model.md`: permission catalog and Admin's example bundle were both missing `ViewDeploymentInfo` (D-98) — added.
- [x] Cosmetic: the three docs whose *content* actually evolved past their stamped "Open Questions: none remaining as of 2026-08-27" date (`Design_Application_Structure.md`, `Design_Authentication_Architecture.md`, `Design_Authorization_Model.md`) had that stamp refreshed. `Design_Data_Editing_Behavior.md`/`Design_Risk_Exception_Tracking.md` also carry the same stamp but nothing in either has changed since — left alone, since the date isn't actually misleading there, just old.
- [x] Found while doing the DPAPI hardening work (not part of the original audit): `Design_Secrets_Storage.md` claimed `WindowsDpapiProtector` "has no caller yet" — actually used by both `IdentityProviderRepository` (OIDC's `PlaintextSecret`) and `SecretsStoreRepository` (Conjur/Azure/AWS credentials) since D-84 — corrected.

## Design documentation — consolidation

- [x] `Open_Questions.md` (root) — fully redundant with the Decision Register (every question already answered, citing D-numbers with equal or greater detail). Retired.
- [x] Decision Register footer narrative — trimmed; it had grown into a second, ~600-word retelling of what the D-101–D-105 rows above it already said.
- [ ] Decision Register rows are unevenly long — D-01–D-58 read as one-liners, several recent rows (D-84, D-89, D-101–D-105) run 150–330 words like implementation logs rather than index entries. Not done this pass — real editing effort, lower urgency than the items above; revisit if the register keeps growing at this rate.
- [ ] `Design_Risk_Scoring.md` (314 lines) is the largest single doc and the most likely to need a future split (data model/matching vs. import-mapping/ETL) if it keeps growing. Not urgent — flagged for awareness only.

## Design documentation — functional gaps

- [x] Notification/alerting delivery mechanism was never designed despite three decisions assuming it exists (D-19 Risk Exception reminders, D-24 break-glass alerts, D-26 rejected SAML alerts) — see new `Design_Notification_Framework.md`.
- [ ] No segregation-of-duties control on exception approval (the person who made a change can also approve the exception excusing it) — design not started.
- [ ] `Design_Risk_Scoring.md`'s own open items (scoring algorithm, 3 of 4 ETL feed shapes) — tracked in that document itself, not duplicated here.

## Deployment methodology

- [x] No deployment doc existed at all — see new `Design_Deployment_Methodology.md`.
- [x] DPAPI's machine-binding disaster-recovery risk (D-65) — accepted as a known risk; hardened by making the protection scope configurable to the specific account running the application pool rather than machine-wide. See D-106 and `Design_Secrets_Storage.md`.
- [x] `msdb` backup-status permission had no assigned owner (D-97) — designed as a proper SQL Server role (assignable to the app pool account or a separate admin account) rather than a one-off ad-hoc grant. See D-107.
- [x] IIS hosting mechanics were never decided (reverse proxy vs. static hosting) — resolved: static hosting for the built SPA, API behind IIS via ANCM. See D-108 and `Design_Deployment_Methodology.md`.
- [ ] No rollback mechanism exists anywhere (Migrator or numbered scripts) — flagged in `Design_Deployment_Methodology.md`'s Open Questions, not resolved this pass.
- [ ] No environment-specific `appsettings.*.json` pattern exists beyond Development — flagged, not resolved this pass.

## Suggestions to improve the tool

- [x] Notification framework, consolidated to one source, with modern SMTP support (OAuth2/STARTTLS via MailKit rather than the legacy `SmtpClient`) — designed, see `Design_Notification_Framework.md`.
- [ ] Segregation-of-duties on exception approval — not started (same item as above).
- [x] DPAPI DR risk — addressed via the app-pool-account-scoped hardening option above (the underlying machine-binding limitation is still an accepted risk by design, not eliminated).
- [x] `msdb` permission owner — addressed via the new role design above.
- [x] IIS hosting decision — resolved (static).

---
*Created 2026-09-05 following a documentation and deployment-methodology audit. Update this file's checkboxes as items are resolved — do not let findings live only in conversation history.*
