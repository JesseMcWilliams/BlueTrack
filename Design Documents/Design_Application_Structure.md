# Web Interface Design Document — Application Structure

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Defines the page/screen inventory, navigation structure, and cross-cutting UI conventions for the web interface — resolving Q-13. Individual screens' data models and business rules live in their own companion documents (Authentication, Authorization, Risk Exception Tracking, Secrets Storage, Audit Logging, Interface Extensibility); this document is the map of how they fit together as pages a user navigates between.

## Page Inventory

Confirmed 2026-08-27 as a first pass — revise here as screens are added or split further during build.

### Public / Unauthenticated

- Login (default-provider redirect with a small link to other enabled providers, including break-glass — see `Design_Authentication_Architecture.md` D-41)
- Access Denied / Error pages

### Analyst-Facing (Viewer / Analyst / Approver permissions)

- Dashboard (home)
- Account Progress — list/grid, filterable by stage/status/risk level/source/safe/application
- Account Progress — detail/edit (the field-metadata-driven form from `Design_Interface_Extensibility.md`)
- Risk Exceptions — list (Active/Expired/Revoked)
- Risk Exceptions — create/edit (account- or application-scoped, D-18/D-31)
- Risk Exceptions — approval worklist (requires `ApproveExceptions`). **Resolved 2026-09-01 (D-70):** shows every currently-Active exception — the schema has no separate "pending approval" state, so this is the permission-gated overview of what's in effect right now, distinct from the plain list (every status) and the overdue-review worklist below (only past-`ReviewDate` ones).
- Risk Exceptions — overdue-review worklist (D-19 — specifically exceptions past their `ReviewDate`, not the same as the Reports overdue worklist below)
- Reports (in-app analyst reporting, D-22 — distinct from Power BI) — see sub-navigation, D-56
- My Profile — self-service "Reload My Rights" (D-14)
- **Targets / Access Groups** — the Risk Scoring inventory admin pages, promoted out of the Admin hub to their own top-level nav entries (D-121), gated by `ManageTargets`/`ManageAccessGroups` respectively. Analyst now holds both permissions (full parity with Admin, D-121), so these are reachable outside the strict Admin-only set despite still being permission-gated. Access Groups also carries a SOR Type/SOR Address pair (D-121) alongside the existing D-119 `GroupScope`/`FoundOnTargetKey`.

### Admin-Facing (gated per-permission, per the D-05 permission model)

- Identity Providers — list/add/edit, enable/disable, test config
- Group → Role Mapping — CRUD + lookup/test tool + trigger Reload Rights for another user
- Roles & Permissions — manage `app_role`/`app_permission`/`role_permission`
- Application ↔ Safe Mapping — curate `dim_application`/`dim_safe.ApplicationKey` (D-31, D-44)
- Secrets Store Configuration — active backend + backend-specific settings
- Field Metadata Management — the governed field-definition list (Interface Extensibility)
- Audit Log Viewer — searchable/filterable, gated by `ViewAuditLog`
- Global Application Configuration — audit retention, read-logging toggle (D-35, both on `audit_config`), idle timeout (D-28), breadcrumb position (D-57, both on `app_config` below)
- Deployment — environment/version info, health checks, SQL Server backup status, gated by `ViewDeploymentInfo` (D-98, `Design_Admin_Deployment_Management.md`)
- Notifications — SMTP config, recipients, notification-type target roles, gated by `ManageNotifications` (D-115/D-116/D-118, `Design_Notifications.md`)
- Credentials & LDAP — vault-backend credential management plus LDAP trusted-connection config, gated by `ManageCredentials` (`Design_Credentials_Management.md`)
- Target Match Review / Import Mapping Profiles — the Risk Scoring import admin pages, gated by `ManageTargets` (D-119, `Design_Risk_Scoring.md`). Targets/Access Groups themselves **moved out** of this hub to their own top-level nav entries (D-121, see Analyst-Facing above) — these two stay here, unmoved.
- Risk Score Bands — admin-configurable named bands (e.g. Low/Medium/High/Critical) over the computed `EffectiveRiskScore`, surfaced on the Account Progress list and Risk Score report; NOT the same thing as `dim_risk_level`. Gated by `ManageRiskScoreBands` (D-120, `Design_Risk_Scoring.md` Phase F)

**Documentation audit correction, 2026-09-09**: this list only had the original 8 pages as of 2026-08-27 (D-43) and was never updated for the Deployment/Notifications/Credentials pages (D-95–D-118) or the four Risk Scoring pages above (D-119) — all 7 added now to match the real, current `AdminHub.vue` (14 admin sections total). **Updated again the same day (D-121)**: Targets/Access Groups moved out to top-level nav entries, leaving 14 admin sections in this hub.

### Proposed Top-Level Navigation

Dashboard | Accounts | Exceptions | Reports | Admin (groups the admin-facing pages) | user menu (profile / reload rights / logout)

**Updated 2026-09-09 (D-121)**: Targets and Access Groups were promoted out of the Admin hub to their own top-level entries — `Dashboard | Accounts | Exceptions | Reports | Targets | Access Groups | Admin | user menu` — each gated on its own permission (`ManageTargets`/`ManageAccessGroups`) rather than rendering unconditionally like the other top-level entries, since Analyst now holds both permissions too (full parity with Admin) and the whole point of the move was making these reachable without going through the Admin hub.

**Resolved 2026-08-27 (D-47, Q-28):** Admin is a **single hub page with sub-navigation**, not eight separate top-level entries — one "Admin" top-nav item, with a sidebar/tab strip inside it listing only the sections the signed-in user has permission for. This keeps the top-level nav from growing as more admin screens are added later, and pairs with the breadcrumb convention (D-45): `Admin / Identity Providers / ...`.

**Resolved 2026-08-27 (D-56, Q-29):** Reports also gets **sub-navigation** — three distinct report types confirmed so far, enough to warrant it rather than a single page:

- **Overdue/At-Risk Worklist** — accounts past `TargetRemediationDate` (a general progress-deadline concern, distinct from the Risk Exceptions overdue-review worklist above, which is specifically about exception `ReviewDate`).
- **Stage/Status Funnel Summary** — a progress-at-a-glance rollup of how many accounts sit at each Blueprint stage/status.
- **Reconciliation Review Queue** — unconfirmed `account_reconciliation` matches (`IsConfirmed = 0`) needing a human decision, gated by the existing `ConfirmReconciliation` permission.
- **Unresolved Entitlement Members** — Safe entitlements granted to a member this app can't resolve to a known user/group (D-107/D-108); no permission gate, read-only.
- **Risk Score** — sortable list of computed/override/effective account risk scores with a per-account contributor drill-down, gated by `ViewRiskReport` (D-119, `Design_Risk_Scoring.md`).

More report types can be added the same way later; this isn't meant to be exhaustive. **Documentation audit correction, 2026-09-09**: this list only had the original three as of 2026-08-27 — the last two were added since (D-107/D-108, D-119) but never made it back into this list.

## Data Model

### app_config

**New 2026-08-27 (D-60).** A gap found on review: `audit_config` (`Design_Audit_Logging.md`) only holds audit-specific settings (`RetentionDays`, `LogReadEvents`), but the Global Application Configuration page also needs to hold settings that have nothing to do with auditing. Kept as a separate table rather than folding into `audit_config`, so audit-specific and general settings stay cleanly separated as more global settings get added later.

**Updated 2026-09-05 (documentation audit)** — this listing had drifted from the real table as more global settings landed piecemeal (`ExceptionIdPattern`/`LockTimeoutMinutes` via `Database/12`/`15`); refreshed to match:

| Field | Type | Purpose |
|---|---|---|
| AppConfigKey | int, PK | Surrogate key (fixed singleton row, same pattern as `audit_config`) |
| IdleTimeoutMinutes | int, default 30 | Session idle timeout (D-28) |
| BreadcrumbPosition | text, controlled list, default 'TopLeft' | Breadcrumb position (D-45/D-57) |
| ExceptionIdPattern | text | Risk Exception ID display format (D-71, `Database/12_BlueTrack_ExceptionIdNumbering.sql`) |
| ExceptionIdSequenceYear / ExceptionIdNextSequence | int / int | Backing counter for the above, resets per calendar year |
| LockTimeoutMinutes | int | Account Progress edit-lock expiry (`Database/15_BlueTrack_LockTimeoutConfig.sql`) |
| ModifiedBy / ModifiedDate | FK to app_user / datetime | Change tracking (D-59) |

**Note on step-up MFA scope (D-29):** this is **not** included here. D-29 reads as a fixed architectural policy ("configuration settings and security settings require step-up, general workflow actions don't") rather than something an admin tunes at runtime the way idle timeout or breadcrumb position is — it's enforced by tagging which actions/endpoints are security-sensitive in code, not a config row. Flagging this reading explicitly in case that's wrong — if step-up scope is actually meant to be admin-adjustable, it belongs here too.

## Cross-Cutting UI Conventions

### Lists & Grids

**Resolved 2026-08-27 (D-42):** every list/grid screen (Account Progress, Risk Exceptions, Audit Log, etc.) supports sorting and filtering with **multiple simultaneous layers** — not a single sort column or a single active filter. A user should be able to stack filters (e.g., Stage = X AND Risk Level = Y) and sort by more than one column at once, rather than the UI forcing one-at-a-time replacement of the prior sort/filter.

### Breadcrumbs

**Resolved 2026-08-27 (D-45):** every page shows a breadcrumb trail so the user always knows where they are and can navigate back up the hierarchy quickly. Position defaults to the top-left of the page. **Resolved 2026-08-27 (D-57):** configurability is **admin-wide only**, via the Global Application Configuration page (alongside audit retention/idle timeout) — not a per-user preference, which would need user-settings infrastructure this app doesn't otherwise have yet. Fully resolves former Q-30.

### Login Provider Display

**Resolved 2026-08-27 (D-41, cross-referenced from `Design_Authentication_Architecture.md`):** displayed provider/module names are admin-configurable (`DisplayName` on `identity_provider_config`, already part of the Authentication Architecture data model) — this is reaffirmed here as a UI-structure requirement, not a new field.

## Implementation Status (added 2026-09-01)

Every page in the inventory above is now built: Reports (now five sub-pages, not three — see the Documentation audit correction above), Risk Exceptions (list/create/edit/approval/overdue-review), and Admin (now fourteen sub-pages, not eight — same correction), each backed by a real controller/repository and verified against the live Dev database. One gap in this document's own cross-cutting conventions remains unimplemented:

- **D-42 (multi-layer filter/sort) — fully resolved 2026-09-04**, across all three list/grid pages named in the original inventory (Account Progress, Risk Exceptions, Audit Log). All three now stack multiple simultaneous filters with AND (Account Progress: Stage/Status/Risk Level/Owner-contains; Risk Exceptions: Status/Scope Type; Audit Log already had event type/entity/user/date range) plus true multi-column sort — click a header to sort by it alone, shift-click another to add it as a secondary key, with numbered arrow badges showing priority. `AccountProgressRepository`/`RiskExceptionRepository`/`AuditRepository` share one query-string parser (`App/Api/SortParser.cs`) but each keeps its own column whitelist for what's actually safe to sort by — the requested field comes straight from the query string, so the whitelist is the SQL-injection guard, not just tidiness (verified on both Account Progress and Audit Log with actual injection attempts in the `sort` parameter, safely ignored both times). Building the Risk Exceptions version caught a real regression from D-77: `RiskExceptionRepository.GetActiveAsync` (the Approval Worklist) started throwing a 500 the moment D-77 added an `@AccountKey` filter to the shared SQL text without updating that caller to supply it — found by re-testing the Approval Worklist while extending the pattern here, not by anything that would have caught it at build time.

**Frontend permission-gating — resolved 2026-09-04 (D-78).** A Pinia store (`App/Web/src/stores/rights.js`) loads `/api/me` once per session and exposes `hasPermission(name)`, mirroring the API's own real `[Authorize(Policy = ...)]` gates rather than a separately-invented list. Wired into: the Admin hub sidebar (each section checks its actual required permission), the Reports hub (Reconciliation Review link checks `ConfirmReconciliation`), the Risk Exceptions list's "+ New Exception" link (`ApproveExceptions`), and the Account Progress edit form (skips attempting to acquire the edit lock entirely for a viewer without `EditAccountProgress`, rather than surfacing a raw 403 as if someone else had it locked). My Profile now has a real self-service "Reload My Rights" button (D-14) backed by the same store. Found and fixed a real gap along the way: the Reconciliation Review Queue's own API endpoint had no `ConfirmReconciliation` policy at all despite D-56 calling for one — the frontend gate would have been cosmetic without it.

Verified: the API endpoints involved (including the newly-gated one) respond correctly for a permission-holding session, and every changed Vue module transforms cleanly under Vite's dev server (no syntax/import errors). At the time this was written, this environment had no browser-automation tooling, so the hide/show behavior itself was checked by careful review rather than by watching it render — **superseded 2026-09-04**: Playwright was added shortly after (D-87/D-88), and every permission-gated hide/show behavior described above is now covered by real browser tests (`admin-pages.spec.js`, `permission-boundaries.spec.js`, `reports-pages.spec.js`).

**Dashboard.vue — built 2026-09-05 (D-99), added here since this document's Page Inventory names it above but the original Implementation Status entry (2026-09-01) predates it.** Three summary cards built entirely from existing endpoints, no new backend rollups: accounts-by-stage totals (`GET /api/reports/stage-status-summary`), an overdue/at-risk accounts count with a link into that worklist (`GET /api/reports/overdue-at-risk`), and risk exceptions needing attention — an overdue-review count any authenticated user sees, plus an active-exceptions-awaiting-approval count shown only to users holding `ApproveExceptions` (checked client-side so most roles never attempt a call that would 403). See `Design_Decision_Register.md` D-99 and the Login page's own D-100 (also built the same day, closing out every remaining literal-placeholder page named in this document's Page Inventory).

## Open Questions

None remaining as of 2026-08-27 — still true as of the 2026-09-05 updates above (Playwright coverage, Dashboard.vue, the `app_config` schema refresh); none of them raised a new open question.
