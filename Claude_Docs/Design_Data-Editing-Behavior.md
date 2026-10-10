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
| LockedByUserKey | FK to app_user | **Resolved 2026-08-27 (D-59):** see `Design_Authentication-Architecture.md`. Who holds the lock |
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
2. `fact_account_progress.CurrentStageKey` cannot move to a **lower `StageOrder`** than its current value (a regression, e.g. Onboarded to Vault → Assessed/Prioritized) without a reason. The reason is captured as a new structured `Reason` field on `audit_event` (see `Design_Audit-Logging.md`) — populated only when an edit requires justification, not free text buried in `Notes`.

Additional rules can be added the same way (a new row here plus a Decision Register entry) as they come up — these two aren't meant to be exhaustive.

## Bulk Edit (Q-19)

**Built 2026-10-09 (D-182), superseding the deferral below.** Account Progress has a selection mode and a Bulk Edit page. Each selected account is locked, saved through `AccountProgressSaveService` (the same D-51 and Risk Exception rules as the edit page, shared code), audited per account, and unlocked; an account another user has locked, or one failing a rule, is skipped and reported, without stopping the rest. One `BulkEdit` audit event summarizes each run. The size limit is `app_config.BulkEditMaxAccounts` (default 500). Risk Exception links stay per account.

Original 2026-08-27 entry:

**Resolved 2026-08-27 (D-52):** deferred for the initial build. Single-record editing ships first, consistent with the same "simple first, refine later" pattern as D-20 (Interface Extensibility's per-field permissions). Bulk edit inside the app is distinct from the Excel intake template (which handles bulk *loading* of new source data via ETL, not editing existing progress records) — revisit if single-record editing proves too slow in practice. Bulk edit would need to interact with per-row locking (D-50), per-row validation results rather than all-or-nothing (D-51), and per-row field-level audit events (D-10) — real scope, not free, which is part of why it's deferred rather than built now.

## Deleting and Undeleting Accounts (D-185)

**Built 2026-10-09.** `fact_account.IsDeleted` still means "deleted" everywhere, but it is now derived: `IsDeletedInSource` (written by the nightly load from the CyberArk export, D-184) **or** a BlueTrack delete (`web.account_deletion`). `usp_Load_FactAccount` recomputes it at the end of every load, so a BlueTrack delete survives. `AccountDeletionService` deletes/undeletes one or many accounts (Account Progress page and list selection), always with a reason, skipping any account another user has locked; each change is an `AccountDeleted`/`AccountUndeleted` audit event plus a `web.account_deletion_history` row. Undelete removes only the BlueTrack delete and sets `IsDeleted` back to `IsDeletedInSource`. Guarded by `DeleteAccounts`.

## UI Edits vs. the Nightly Import (Q-20)

**Resolved 2026-08-27 (D-53):** already handled by existing ETL design — confirmed by reading `02_BlueTrack_Baseline_EtlLoads.sql` (`usp_Load_FactAccountProgress`, split out of the original `02_BlueTrack_ETL_LoadProcedures.sql` in the 2026-09-05 restructure) and `02_BlueTrack_Baseline_EtlLoads.sql` directly rather than assuming.

`usp_Load_FactAccountProgress` (called by `usp_RunFullLoad`) only **inserts** a `fact_account_progress` row for an account that doesn't have one yet (`NOT EXISTS` check) — it never updates an existing row. An account already being tracked keeps whatever an analyst has since set, no matter how many times the load runs. `02_BlueTrack_Baseline_EtlLoads.sql` doesn't touch `fact_account_progress` or exceptions at all. No schema or process change was needed — the risk this question worried about doesn't exist in the current design.

## Implementation Status (added 2026-09-01)

The Account Progress edit form is built end to end against everything decided above:

- **Locking (D-50):** `AccountProgressLockRepository`/`AccountProgressController` implement the full mechanics — acquire-on-open, heartbeat, release-on-save-or-cancel, the 5-minute (admin-configurable via `app_config.LockTimeoutMinutes`, added by `04_BlueTrack_Baseline_WebSchema.sql` — no column existed for it before) abandoned-lock timeout, and admin force-break. Force-break reuses the `EditAccountProgress` permission rather than a new one (D-74). **D-174 (2026-10-07):** the lock-status query treats a lock past the timeout as no lock, so the edit page acquires it (the acquire call deletes the stale row) instead of showing "Currently being edited by …" indefinitely, even to the lock's own holder. Saving checks ownership separately (`IsHeldByAsync`, which ignores the timeout), so a holder who paused can still save unless someone else has taken the lock over. **D-177 (2026-10-08):** the edit page also takes over a lock **the current user** already holds (another tab, or an earlier visit that left without saving); `AcquireLock` already granted that, but the page only asked when no lock existed, so the user saw "Currently being edited by" themselves until the timeout. A lock held by anyone else still shows as read-only.
- **Validation (D-51):** both rules are enforced server-side in `AccountProgressController.Update` and verified against live data — Complete-without-`ActualCompletionDate` and stage-regression-without-`Reason` both correctly reject with 400, and a regression with a `Reason` succeeds and is captured on `audit_event.Reason`.
- **Field-level audit (D-10, via D-73):** every changed field is diffed and logged to `audit_event`/`audit_field_change` on save.
- The form itself is genuinely field-metadata-driven, per `Design_Interface-Extensibility.md` — see that document's own Implementation Status note.

**Not built:** anything about the UI-vs-nightly-import interaction (D-53 needed no code change, confirmed by reading the ETL procedures directly).

## Open Questions

None remaining as of 2026-08-27 — Q-16 (D-50), Q-18 (D-51), Q-19 (D-52), and Q-20 (D-53) are all resolved above.
