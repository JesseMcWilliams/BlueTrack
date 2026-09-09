# BlueTrack — Audit Findings Tracker

A checklist of findings from the 2026-09-05 documentation and deployment-methodology audit (two background research passes over every `Design_*.md` doc plus the current de facto deployment process), so progress against them is trackable over time rather than living only in conversation history. Check items off as they're resolved; add new findings here rather than letting them live only in chat.

## Design documentation — staleness

**2026-09-09 follow-up pass** (after the Risk Scoring feature, D-119, shipped across 5 phases — two research passes, one scoped to D-119's own cross-cutting doc impact, one a fresh general sweep of the rest):

- [x] `Design_Authorization_Model.md`: permission catalog listing and the Admin example bundle both predate D-119 and don't list `ManageTargets`/`ManageAccessGroups`/`ViewRiskReport` — all three are real, seeded, Admin-granted permissions (`App/Api/Auth/Permissions.cs`, `Database/20_BlueTrack_RiskScoringSchema.sql`). Identical gap shape to the `ViewDeploymentInfo` fix already logged above (D-98) — this doc has now drifted the same way twice. Fixed.
- [x] `Design_Application_Structure.md`: the Admin-Facing page inventory and the "all eight Admin sub-pages" Implementation Status claim don't include Targets/Access Groups/Target Match Review/Import Mapping Profiles — `AdminHub.vue` now lists 14 admin sections total, not 8. Its Reports sub-navigation list (3 report types) is also missing the new Risk Score report. Fixed — also caught and fixed two more pre-existing gaps in the same lists while there (Deployment/Notifications/Credentials & LDAP admin pages; Unresolved Entitlement Members report), not caused by D-119 but the same class of drift.
- [x] `Design_Testing_Strategy.md`: Implementation Status counts are stale again. Actual as of this pass: `dotnet test` reports 338 passing (46 test files carry `[Fact]`/`[Theory]` attributes; the doc's own "N tests" framing should say which of attribute-count vs. actual-runtime-count — a `[Theory]` with `MemberData` is one attribute but many runtime cases, e.g. `AdminControllersPermissionTests`), Vitest is 18 tests/2 files (still accurate), Playwright is 49 `test()` cases across 7 spec files (doc says 39). Fixed.
- [x] `Design_Secrets_Storage.md`: Implementation Status doesn't mention `WindowsDpapiVaultSecretsProvider` (D-118) as a real `IVaultSecretProvider` — that addition is only documented in `Design_Credentials_Management.md`, and it's directly relevant here since it backs this doc's own `web.secrets_store` health check. Fixed with a cross-reference note (full detail stays in `Design_Credentials_Management.md`).
- [x] `Design_Accessibility_And_Theming.md`: documents `data-theme="light" | "dark" | "high-contrast"`; the actual implementation (`themes.css`, `stores/theme.js`) uses `"Light"`/`"Dark"`/`"HighVisibility"` — different casing and a different name for the third value. Fixed — this doc's own Data Model table already had the correct values, so this was an internal inconsistency, not just doc-vs-code.
- [x] `Design_Decision_Register.md`'s closing footer narrative carries two back-to-back paragraphs covering the identical D-89–D-109 range (one long, one a shorter retelling of the same ground) — real duplication, not just verbosity. Notable since this file's own history (line below) already logged trimming this exact footer once, 2026-09-05; it appears to have regrown rather than actually been resolved. The longer of the two paragraphs also still says D-101–D-105 (Risk Scoring) is "not yet implemented," which is now false — all 5 phases are done (D-119). Fixed — duplicate paragraph removed (its one unique fact, the Notification Framework superseded note, merged into the surviving paragraph), "not yet implemented" corrected.
- [x] `Design_Decision_Register.md`'s "Outstanding Decisions — Not Yet Designed At All" section reads "None currently" — contradicted by this very Tracker's own "No segregation-of-duties control on exception approval — design not started" item below. A live instance of the drift this register exists to prevent. Fixed — now lists the three real open items already tracked here, with a pointer to keep this Tracker as the source of truth for the checklist itself.

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
- [ ] `Design_Risk_Scoring.md` (314 lines) is the largest single doc and the most likely to need a future split (data model/matching vs. import-mapping/ETL) if it keeps growing. Not urgent — flagged for awareness only. **Update 2026-09-09**: now 351 lines after all 5 D-119 implementation phases landed — grew, but only modestly (+37 lines, since each phase's Implementation Status entry stayed a tight paragraph); still not urgent.

## Design documentation — functional gaps

- [x] Notification/alerting delivery mechanism was never designed despite three decisions assuming it exists (D-19 Risk Exception reminders, D-24 break-glass alerts, D-26 rejected SAML alerts) — see new `Design_Notification_Framework.md`. **Correction 2026-09-09**: this checkbox is misleading as written — what actually got *built* (D-115/D-116/D-118) uses a different schema and different class names than `Design_Notification_Framework.md` proposed (`web.notification_config`/`notification_recipient`/`dim_notification_type`/`notification_log` and `NotificationRepository`/`SmtpNotificationSender`/`INotificationCheck`, not that doc's `dim_notification_event_type`/`notification_channel` and `NotificationDispatcher`/`INotificationChannel`/`SmtpNotificationChannel`). `Design_Notifications.md` is the doc that matches reality; `Design_Notification_Framework.md` was superseded but carries no note saying so — new item below.
- [x] `Design_Notification_Framework.md` was superseded by what actually shipped under D-115/D-116/D-118 (documented in `Design_Notifications.md`) but isn't marked superseded, retired, or reconciled — a reader following it would look for `NotificationDispatcher`/`INotificationChannel`/`SmtpNotificationChannel`, none of which exist. (2026-09-09) Fixed — added a superseded notice pointing to `Design_Notifications.md`, with a matching cross-reference added there too. Left in place (not deleted) for historical context on the original D-19/D-24/D-26 problem framing.
- [ ] No segregation-of-duties control on exception approval (the person who made a change can also approve the exception excusing it) — design not started.
- [x] `Design_Risk_Scoring.md`'s own open items (scoring algorithm, 3 of 4 ETL feed shapes) — resolved as of 2026-09-09: D-119 built both scoring candidates behind a dispatcher, and unified the ETL feeds with the admin bulk-CSV-upload mechanic rather than needing real external file formats. See that document's own Implementation Status section.

## Deployment methodology

- [x] No deployment doc existed at all — see new `Design_Deployment_Methodology.md`.
- [x] DPAPI's machine-binding disaster-recovery risk (D-65) — accepted as a known risk; hardened by making the protection scope configurable to the specific account running the application pool rather than machine-wide. See D-106 and `Design_Secrets_Storage.md`.
- [x] `msdb` backup-status permission had no assigned owner (D-97) — designed as a proper SQL Server role (assignable to the app pool account or a separate admin account) rather than a one-off ad-hoc grant. See D-107.
- [x] IIS hosting mechanics were never decided (reverse proxy vs. static hosting) — resolved: static hosting for the built SPA, API behind IIS via ANCM. See D-108 and `Design_Deployment_Methodology.md`.
- [ ] No rollback mechanism exists anywhere (Migrator or numbered scripts) — flagged in `Design_Deployment_Methodology.md`'s Open Questions, not resolved this pass.
- [ ] No environment-specific `appsettings.*.json` pattern exists beyond Development — flagged, not resolved this pass.

## Suggestions to improve the tool

- [x] Notification framework, consolidated to one source, with modern SMTP support (OAuth2/STARTTLS via MailKit rather than the legacy `SmtpClient`) — designed and, per the correction above, actually *built* under `Design_Notifications.md`'s different (but real, shipped) schema, not `Design_Notification_Framework.md`'s proposed one.
- [ ] Segregation-of-duties on exception approval — not started (same item as above).
- [x] DPAPI DR risk — addressed via the app-pool-account-scoped hardening option above (the underlying machine-binding limitation is still an accepted risk by design, not eliminated).
- [x] `msdb` permission owner — addressed via the new role design above.
- [x] IIS hosting decision — resolved (static).

---
*Created 2026-09-05 following a documentation and deployment-methodology audit. Update this file's checkboxes as items are resolved — do not let findings live only in conversation history.*
*2026-09-09 follow-up pass: after D-119 (Risk Scoring, all 5 phases) shipped, two research passes re-checked every design doc against the current code — one scoped to D-119's own cross-cutting impact (permission catalogs, page inventories), one a fresh general sweep of the rest. New findings added above under each existing category; nothing from the 2026-09-05 pass needed re-opening except where noted.*
