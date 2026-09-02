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

### Admin-Facing (gated per-permission, per the D-05 permission model)

- Identity Providers — list/add/edit, enable/disable, test config
- Group → Role Mapping — CRUD + lookup/test tool + trigger Reload Rights for another user
- Roles & Permissions — manage `app_role`/`app_permission`/`role_permission`
- Application ↔ Safe Mapping — curate `dim_application`/`dim_safe.ApplicationKey` (D-31, D-44)
- Secrets Store Configuration — active backend + backend-specific settings
- Field Metadata Management — the governed field-definition list (Interface Extensibility)
- Audit Log Viewer — searchable/filterable, gated by `ViewAuditLog`
- Global Application Configuration — audit retention, read-logging toggle (D-35, both on `audit_config`), idle timeout (D-28), breadcrumb position (D-57, both on `app_config` below)

### Proposed Top-Level Navigation

Dashboard | Accounts | Exceptions | Reports | Admin (groups the admin-facing pages) | user menu (profile / reload rights / logout)

**Resolved 2026-08-27 (D-47, Q-28):** Admin is a **single hub page with sub-navigation**, not eight separate top-level entries — one "Admin" top-nav item, with a sidebar/tab strip inside it listing only the sections the signed-in user has permission for. This keeps the top-level nav from growing as more admin screens are added later, and pairs with the breadcrumb convention (D-45): `Admin / Identity Providers / ...`.

**Resolved 2026-08-27 (D-56, Q-29):** Reports also gets **sub-navigation** — three distinct report types confirmed so far, enough to warrant it rather than a single page:

- **Overdue/At-Risk Worklist** — accounts past `TargetRemediationDate` (a general progress-deadline concern, distinct from the Risk Exceptions overdue-review worklist above, which is specifically about exception `ReviewDate`).
- **Stage/Status Funnel Summary** — a progress-at-a-glance rollup of how many accounts sit at each Blueprint stage/status.
- **Reconciliation Review Queue** — unconfirmed `account_reconciliation` matches (`IsConfirmed = 0`) needing a human decision, gated by the existing `ConfirmReconciliation` permission.

More report types can be added the same way later; this isn't meant to be exhaustive.

## Data Model

### app_config

**New 2026-08-27 (D-60).** A gap found on review: `audit_config` (`Design_Audit_Logging.md`) only holds audit-specific settings (`RetentionDays`, `LogReadEvents`), but the Global Application Configuration page also needs to hold settings that have nothing to do with auditing. Kept as a separate table rather than folding into `audit_config`, so audit-specific and general settings stay cleanly separated as more global settings get added later.

| Field | Type | Purpose |
|---|---|---|
| AppConfigKey | int, PK | Surrogate key (or a fixed singleton row, same pattern as `audit_config`) |
| IdleTimeoutMinutes | int, default 30 | Session idle timeout (D-28) |
| BreadcrumbPosition | text, controlled list, default 'TopLeft' | Breadcrumb position (D-45/D-57) |
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

Every page in the inventory above is now built: Reports (its three sub-pages), Risk Exceptions (list/create/edit/approval/overdue-review), and all eight Admin sub-pages, each backed by a real controller/repository and verified against the live Dev database. Two gaps in this document's own cross-cutting conventions remain unimplemented:

- **D-42 (multi-layer filter/sort)** — every list/grid page shipped with only basic single-value filtering (e.g. Account Progress by stage), not the stacked multi-column filter/sort this section calls for. Needs a proper dynamic query builder or an OData-style endpoint.
- **Frontend permission-gating** — no page yet reads `rights.permissionNames` from `/api/me` to hide/disable UI a user can't use; permission-gated pages currently rely entirely on the API's real policy enforcement returning a 403, which the frontend surfaces as a plain error message rather than not showing the control at all.

## Open Questions

None remaining as of 2026-08-27.
