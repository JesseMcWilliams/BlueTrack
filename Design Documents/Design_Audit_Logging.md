# Web Interface Design Document — Audit Logging

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Auditing is a major part of this tool, not an afterthought: user logons and the actions users take in the application both need to be logged, with a full field-level record of what changed — who changed it, from what value, to what value, and when. Retention of that log is controlled by an admin, not hardcoded, via a global application configuration page. (D-10, D-11, D-12)

**Resolved 2026-08-27** (Q-26): by default, logging is scoped to **writes, logons, and approvals** — not every read/view. Logging reads is available as a configurable option, but **off by default**. This fully resolves Q-26. (D-35)

This is distinct from two things already in the schema:
- `fact_account_progress_history`, which only snapshots Stage/Status/Risk Level once daily from the ETL side — not a per-edit, per-field log of web-app activity.
- `import_log`, which only covers the ETL import/load process, not application-level user activity.

## Design Principles

- Consistent with how this project treats other governed/curated data: audit event types are a proper dimension table, not free text (see `[[bluetrack-conventions]]`: dimension tables for governed reference data, not ad hoc fields).
- The audit log itself should be effectively append-only from the application's perspective — normal application code paths write to it but never update or delete existing rows (deletion only happens through the retention/purge process described below).
- **Resolved 2026-08-27 (D-63), Q-32:** tamper-evidence is provided by **standard database access control**, not cryptographic hash-chaining. The app's service account gets insert-only grants on `audit_event`/`audit_field_change`; only the purge job's identity (D-62) can delete, and only aged-out rows. SQL Server **Ledger tables** would give engine-managed cryptographic tamper-evidence essentially for free, but require SQL Server 2022+/Azure SQL — the confirmed target is SQL Server 2017–2019, so that option isn't available. Hand-rolling hash-chained rows was considered and rejected for now: real engineering (serialized inserts, verification tooling) plus added complexity to the already-decided retention purge (D-62), which would need chain-checkpointing to delete old rows without breaking the chain — consistent with this project's pattern of deferring that kind of speculative complexity (D-52, D-33) until a concrete need arises. Revisit if a specific compliance requirement demands cryptographic proof, or if the SQL Server target ever moves to 2022+.
- Field-level diffs are captured for edits to governed data (e.g., `fact_account_progress` fields), not just a description of the action — consistent with the full field-level diff decision (D-10).
- Viewing the audit log is itself a permission-gated action (e.g., a new `ViewAuditLog` permission in `app_permission`), not open to every authenticated user by default.

## Proposed Data Model

**Illustrative starting point, not a fixed requirement** — the same caveat this project's other design docs use for a first-pass schema. Confirm table/column names against actual conventions before this becomes real DDL.

### dim_audit_event_type

| Field | Type | Purpose |
|---|---|---|
| AuditEventTypeKey | int, PK | Surrogate key |
| EventTypeName | text, unique | Logon, LogonFailed, FieldEdit, ExceptionApproved, ProviderConfigChanged, ReloadRights, plus **ExceptionReviewExtended** and **ExceptionRevoked** (added 2026-09-01, D-73 — the original catalog covered exception creation but not the workflow's other two actions, found while wiring real audit logging into the Risk Exceptions pages) |
| Description | text | What this event type represents, for the admin screen |

### audit_event

| Field | Type | Purpose |
|---|---|---|
| AuditEventKey | bigint, PK | Surrogate key |
| AuditEventTypeKey | FK to dim_audit_event_type | What kind of event this is |
| OccurredAt | datetime2 | When the event happened |
| PerformedByUserKey | FK to app_user | **Resolved 2026-08-27 (D-59):** `app_user`, not `dim_user` — see `Design_Authentication_Architecture.md`. Who did it — the authenticated identity, not a raw provider claim |
| EntityName | text, nullable | Which table/entity was affected, if applicable (e.g. `fact_account_progress`, `risk_exception`) |
| EntityKey | text, nullable | The affected row's key, if applicable |
| SourceIpAddress | text, nullable | Client IP, if captured |
| Detail | text, nullable | Free-form context for events that aren't a field edit (e.g. "Reload Rights triggered for user X") |
| Reason | text, nullable | **New 2026-08-27** (D-51): a structured, required-when-applicable justification for an edit — e.g. why a Blueprint stage was regressed (`Design_Data_Editing_Behavior.md`). Null for edits that don't require justification. |

### audit_field_change

One row per changed field, linked to the parent event — this is what makes the log field-level rather than row-level.

| Field | Type | Purpose |
|---|---|---|
| AuditFieldChangeKey | bigint, PK | Surrogate key |
| AuditEventKey | FK to audit_event | The edit event this change belongs to |
| FieldName | text | Which column changed |
| OldValue | text, nullable | Value before the change (null for a newly-populated field) |
| NewValue | text, nullable | Value after the change (null if the field was cleared) |

### audit_config

A single (or environment-scoped) configuration row, managed through the planned global application configuration admin page.

| Field | Type | Purpose |
|---|---|---|
| AuditConfigKey | int, PK | Surrogate key (or a fixed singleton row) |
| RetentionDays | int | How long audit records are kept before purge — admin-configurable, not hardcoded (D-12) |
| LogReadEvents | bit, default 0 | Whether read/view actions are also logged, in addition to the always-on writes/logons/approvals — off by default (D-35) |
| ModifiedBy / ModifiedDate | FK to app_user / datetime | Change tracking for this admin-editable setting (D-59) |

## Retention & Purge

The retention period is set on `audit_config.RetentionDays` via the global application configuration page.

**Resolved 2026-08-27 (D-62), Q-31:** the purge runs as a **SQL Agent job** calling a stored procedure (e.g. `usp_PurgeAuditLog`) — matching the existing documented pattern for `usp_RunFullLoad` and the history-snapshot job in `04_BlueTrack_PowerBI_Support.sql`, rather than introducing an application-level job scheduler this project hasn't used anywhere else.

The purge run itself is recorded in a new **`audit_purge_log`** table, mirroring the existing `import_log` pattern rather than writing the purge into `audit_event` itself (which would read recursively — an audit table logging its own deletions):

### audit_purge_log

| Field | Type | Purpose |
|---|---|---|
| PurgeBatchId | uniqueidentifier, PK | Same shape as `import_log.ImportBatchId` |
| CutoffDate | date | Records older than this were eligible for deletion, per `audit_config.RetentionDays` at run time |
| RowsPurged | int | How many `audit_event` rows were deleted |
| StartedAt | datetime2 | |
| CompletedAt | datetime2, nullable | |
| Status | text | e.g. `Started` / `Completed` / `Failed`, same convention as `import_log.Status` |
| ErrorMessage | text, nullable | |

## Admin UI Requirements

- A global application configuration page including, at minimum, the audit retention period (`audit_config.RetentionDays`).
- A searchable/filterable audit log view (by user, date range, event type, affected entity) — gated behind a `ViewAuditLog` (or equivalent) permission.
- Visibility into field-level changes for a given event, not just the top-level action description.

## Implementation Status (added 2026-09-01, D-73)

Real audit logging is now wired in: `AuditLogger` writes to `audit_event`/`audit_field_change` from every write action across the Risk Exceptions pages and all Admin pages, and the Audit Log Viewer (search/filter by user, event type, entity, date range, plus field-level drill-down) is built against it. This was a deliberate retrofit — the user chose to wire it in immediately rather than ship the viewer as an empty shell.

**Logon auditing: resolved 2026-09-04 (D-82).** A real session concept now exists (`UserRightsCache`, a per-identity entry in `web.distributed_cache`) — a cache miss (no live resolution has happened recently for this identity) is what logs a `Logon` event, since there's no other reliable way to distinguish a real logon from a routine call without a session. Verified: three requests in a row from the same identity produced exactly one `Logon` event, not three.

**`LogReadEvents` enforcement: resolved 2026-09-04 (D-83).** Scoped to **detail views only** — GET-by-key endpoints for governed entities (Account Progress detail, Risk Exception detail), not list/search/report endpoints, to avoid flooding the log on every page load. A new `RecordViewed` event type (`21_BlueTrack_ReadEventType.sql`) is logged via `AuditLogger.LogReadIfEnabledAsync`, checked against `audit_config.LogReadEvents` on each call. If a future page adds another detail-view endpoint, it needs to call the same method explicitly — nothing enforces that automatically across new endpoints. Verified: with the flag off, no event is logged; with it on, each detail-view GET logs exactly one `RecordViewed` event, and list endpoints log nothing.

## Open Questions

None remaining as of 2026-09-04 — Q-32 was resolved 2026-08-27 as D-63 above; `LogReadEvents` enforcement scope (the last open item from the session-layer follow-ups) was resolved 2026-09-04 as D-83.

Resolved: audit scope defaults to writes/logons/approvals with reads as an off-by-default option (Q-26/D-35); break-glass alerting resolved separately as D-24 in `Design_Authentication_Architecture.md`; app-user identity resolved as D-59 in `Design_Authentication_Architecture.md`.

---
*New document added 2026-08-27, following today's decision that auditing (logons + actions, full field-level diffs, admin-configurable retention) is a major part of this tool.*
