# Web Interface Design Document — Data & Editing Behavior

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Defines how the web interface handles concurrent edits, validation, bulk operations, and the interaction between manual UI edits and the nightly ETL import — resolving Q-16, Q-18, Q-19, and Q-20 from the Decision Register.

## Concurrency Control (Q-16)

**Resolved 2026-08-27:** pessimistic locking, not optimistic concurrency or silent last-write-wins. (D-50)

### Data Model

#### account_progress_lock

A separate table from `fact_account_progress` — lock state is transient session data, not business data.

| Field | Type | Purpose |
|---|---|---|
| AccountKey | FK to fact_account, PK | The account progress row being edited |
| LockedByUserKey | FK to app_user | **Resolved 2026-08-27 (D-59):** see `Design_Authentication_Architecture.md`. Who holds the lock |
| LockedAt | datetime2 | When the lock was acquired |
| LastHeartbeatAt | datetime2 | Updated periodically while the edit form stays open |

### Mechanics

1. Opening the Account Progress edit form acquires the lock (insert a row, or reject if already locked by someone else).
2. Other users viewing that account see it as **read-only**, with "Currently being edited by \<user\> since \<time\>".
3. While the edit form is open, the client sends a periodic heartbeat to refresh `LastHeartbeatAt`.
4. **Resolved 2026-08-27 (D-50):** if no heartbeat arrives within **5 minutes**, the lock is considered abandoned and auto-releases — covering a crashed browser or closed tab without a clean release. This timeout is **admin-configurable** via the Global Application Configuration page (D-12/D-28's home), not hardcoded.
5. Saving or explicitly canceling releases the lock immediately.
6. An admin can force-break a stuck lock — consistent with how Reload Rights already lets an admin act on another user's session (D-04).

## Business Validation Rules (Q-18)

**Resolved 2026-08-27 (D-51):** two rules for now, enforced at the application layer (consistent with how this project avoids database triggers for business logic):

1. `fact_account_progress.CurrentStatusKey` cannot be set to **Complete** unless `ActualCompletionDate` is populated in the same save.
2. `fact_account_progress.CurrentStageKey` cannot move to a **lower `StageOrder`** than its current value (a regression, e.g. Onboarded to Vault → Assessed/Prioritized) without a reason. The reason is captured as a new structured `Reason` field on `audit_event` (see `Design_Audit_Logging.md`) — populated only when an edit requires justification, not free text buried in `Notes`.

Additional rules can be added the same way (a new row here plus a Decision Register entry) as they come up — these two aren't meant to be exhaustive.

## Bulk Edit (Q-19)

**Resolved 2026-08-27 (D-52):** deferred for the initial build. Single-record editing ships first, consistent with the same "simple first, refine later" pattern as D-20 (Interface Extensibility's per-field permissions). Bulk edit inside the app is distinct from the Excel intake template (which handles bulk *loading* of new source data via ETL, not editing existing progress records) — revisit if single-record editing proves too slow in practice. Bulk edit would need to interact with per-row locking (D-50), per-row validation results rather than all-or-nothing (D-51), and per-row field-level audit events (D-10) — real scope, not free, which is part of why it's deferred rather than built now.

## UI Edits vs. the Nightly Import (Q-20)

**Resolved 2026-08-27 (D-53):** already handled by existing ETL design — confirmed by reading `02_BlueTrack_ETL_LoadProcedures.sql` and `03_BlueTrack_AccountReconciliation.sql` directly rather than assuming.

`usp_Load_FactAccountProgress` (called by `usp_RunFullLoad`) only **inserts** a `fact_account_progress` row for an account that doesn't have one yet (`NOT EXISTS` check) — it never updates an existing row. An account already being tracked keeps whatever an analyst has since set, no matter how many times the load runs. `03_BlueTrack_AccountReconciliation.sql` doesn't touch `fact_account_progress` or exceptions at all. No schema or process change was needed — the risk this question worried about doesn't exist in the current design.

## Implementation Status (added 2026-09-01)

The Account Progress edit form is built end to end against everything decided above:

- **Locking (D-50):** `AccountProgressLockRepository`/`AccountProgressController` implement the full mechanics — acquire-on-open, heartbeat, release-on-save-or-cancel, the 5-minute (admin-configurable via `app_config.LockTimeoutMinutes`, added by `15_BlueTrack_LockTimeoutConfig.sql` — no column existed for it before) abandoned-lock timeout, and admin force-break. Force-break reuses the `EditAccountProgress` permission rather than a new one (D-74).
- **Validation (D-51):** both rules are enforced server-side in `AccountProgressController.Update` and verified against live data — Complete-without-`ActualCompletionDate` and stage-regression-without-`Reason` both correctly reject with 400, and a regression with a `Reason` succeeds and is captured on `audit_event.Reason`.
- **Field-level audit (D-10, via D-73):** every changed field is diffed and logged to `audit_event`/`audit_field_change` on save.
- The form itself is genuinely field-metadata-driven, per `Design_Interface_Extensibility.md` — see that document's own Implementation Status note.

**Not built:** bulk edit (D-52, deferred by design) and anything about the UI-vs-nightly-import interaction (D-53 needed no code change, confirmed by reading the ETL procedures directly).

## Open Questions

None remaining as of 2026-08-27 — Q-16 (D-50), Q-18 (D-51), Q-19 (D-52), and Q-20 (D-53) are all resolved above.
