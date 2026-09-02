# Web Interface Design Document — Risk Exception Tracking

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Formalizes how an account's Risk Accepted / Excluded status is backed by a real, trackable exception record — replacing what is currently just a free-text Notes field on `fact_account_progress` with a proper identifier, approval trail, and review cadence.

## Why This Needs More Than a Notes Field

A Risk Accepted / Excluded status represents a deliberate decision not to bring an account into full management — it should be auditable on its own terms: who approved it, why, whether it's still valid, and whether it needs to be revisited. A sentence in a Notes column doesn't support any of that, and doesn't give the account a stable identifier that can be referenced from an external ticket, an audit finding, or a later re-review.

## Data Model

### dim_application

**New 2026-08-27, resolving Q-25.** A curated business grouping — not present in any CyberArk export — sitting above `dim_safe`. **Resolved relationship:** a Safe belongs to exactly one Application; an Application can own many Safes. Populated and maintained the same way as `platform_account_type_map` (a curated, manually-reviewed mapping), not inferred automatically from Safe names.

| Field | Type | Purpose |
|---|---|---|
| ApplicationKey | int, PK | Surrogate key |
| ApplicationGUID | uniqueidentifier, unique | **Resolved 2026-08-27** (D-44): a stable external identifier for referencing an application outside this database (e.g., from another system or a future API) |
| ApplicationCode | text, unique | **Resolved 2026-08-27** (D-44): a short, human-readable identifier — distinct from the full `ApplicationName` — for compact display (e.g., in a grid column or a breadcrumb) |
| ApplicationName | text, unique | The business application/system name |
| Description | text, nullable | What this application is, for the admin/curation screen |
| OwnerName | text, nullable | **Resolved 2026-08-27** (D-46): the business owner's name |
| OwnerEmail | text, nullable | The business owner's email |
| TechnicalName | text, nullable | The technical contact's name |
| TechnicalEmail | text, nullable | The technical contact's email |
| Notes | text, nullable | Free-form notes, distinct from `Description` (e.g. curation history, caveats) |

### dim_safe.ApplicationKey

**New 2026-08-27.** Nullable FK to `dim_application`, added to the existing `dim_safe` table. Nullable because not every Safe maps to a business application — system/built-in Safes (e.g. `VaultInternal`, `Notification Engine`, seen in the actual Privilege Cloud export sample) have no application owner. Populate through the same curated-review process as other business mappings in this project — don't infer it from `SafeName` text.

### dim_exception_status

| Field | Type | Purpose |
|---|---|---|
| ExceptionStatusKey | int, PK | Surrogate key |
| StatusName | text, unique | Active / Expired / Revoked |

### risk_exception

| Field | Type | Purpose |
|---|---|---|
| ExceptionKey | int, PK | Surrogate key |
| ExceptionID | text, unique, human-readable | e.g. EXC-2026-0001 — the identifier referenced in tickets/audits. **Resolved 2026-08-27:** the numbering scheme must be flexible/configurable rather than a single fixed global sequence, since it differs by organization (D-17). **Implemented 2026-09-01 (D-71):** a configurable pattern string in `web.app_config.ExceptionIdPattern` (default `EXC-{yyyy}-{seq:0000}`), backed by an atomically-incremented, year-scoped sequence counter. See `App/Api/Data/ExceptionIdGenerator.cs`. |
| AccountKey | FK to fact_account, **nullable** | Which account this exception applies to, when scoped to a single account — stored directly here, not only inferred through `fact_account_progress`, so history survives even if the account's current status later changes. |
| ApplicationKey | FK to dim_application, **nullable** | Which application this exception applies to, when scoped to an entire application rather than one account (D-18). **Resolved 2026-08-27** (Q-25): exactly one of `AccountKey` / `ApplicationKey` must be set per exception — enforced at the application layer, consistent with how this project avoids database triggers for business rules (see the workflow step below). |
| Justification | text | Why the exception was granted |
| ApprovedBy | FK to app_user | **Resolved 2026-08-27 (D-59):** was plain text, now a FK — see `Design_Authentication_Architecture.md`. Who approved it |
| ApprovalDate | date | When it was approved |
| ReviewDate | date | When it needs to be revisited — exceptions are treated as time-bound, not permanent, by design |
| ExceptionStatusKey | FK to dim_exception_status | Active / Expired / Revoked |
| ExternalTicketReference | text, nullable | An optional link to a ticket in an external system (ServiceNow, Jira, etc.) if your org tracks exceptions there too |

### fact_account_progress.ExceptionKey

New nullable FK column pointing to the currently-active `risk_exception` row for that account. Populated when `CurrentStatusKey` is set to Risk Accepted / Excluded. For an application-scoped exception, this still resolves per-account through `dim_safe.ApplicationKey` → the accounts in that application's Safes, rather than requiring every affected account's `fact_account_progress` row to be touched individually — the exact mechanics of that resolution (a view vs. a batch update) are an implementation detail for later, not decided here.

## Why AccountKey and ApplicationKey Both Live Directly on risk_exception

If an exception later expires and a new one is granted for the same account or application, keeping the scoping key on `risk_exception` itself (rather than only reachable through `fact_account_progress`'s current pointer) means the full history of exceptions stays queryable — the same reasoning already applied to `account_reconciliation` elsewhere in this project: don't let a "current state" pointer erase history.

## Proposed Workflow

**Decision:** who can add or approve an exception is controlled by a permission (`ApproveExceptions`, per the Authorization Model's permission-based design), not hardcoded to a single named role — that permission can be granted through one role or several, so this stays consistent even if the role structure changes later.

1. A user holding the `ApproveExceptions` permission sets an account's status to Risk Accepted / Excluded (or, once designed, applies an exception at the application level).
2. The interface requires either linking an existing Active exception or creating a new one — the status cannot be set without one, enforced at the application layer (not a database trigger, consistent with how this project has avoided triggers elsewhere).
3. A new exception is assigned the next `ExceptionID` per the org's configured numbering scheme, captures `Justification`/`ApprovedBy`/`ApprovalDate`/`ReviewDate`, and is marked Active.
4. When `ReviewDate` passes, the exception surfaces on a worklist for re-approval or revocation. **Resolved 2026-08-27:** active notification (email, Teams, etc.) will be offered as an **optional/configurable** capability, not a mandatory feature for every exception (D-19).

## Implementation Status (added 2026-09-01)

Built end to end: the exception List, Approval Worklist, Overdue-Review Worklist, and create/extend-review/revoke actions (`RiskExceptionsController`/`RiskExceptionRepository`), each write action logged to `audit_event`/`audit_field_change` (D-73). The Approval Worklist's exact scope — every currently-Active exception — was ambiguous in this document and resolved directly by the user during implementation (**D-70**), since the schema has no separate "pending approval" state: an exception is created Active in one step by whoever holds `ApproveExceptions`.

**Known gap:** creating an exception doesn't yet update the scoped account's `fact_account_progress.CurrentStatusKey`/`ExceptionKey`, even though workflow step 1 above describes the action starting from an Account Progress status change to Risk Accepted / Excluded. That requires the Account Progress edit form to exist first — it's still a placeholder as of this writing.

## Open Questions

None remaining as of 2026-08-27. Q-25 (how an Application relates to `dim_safe`/`fact_account`) was resolved this session — see `dim_application` above.

Resolved this session: ExceptionID numbering flexibility (D-17), exception scope covering account-or-application (D-18), application entity design (Q-25/D-31), and optional review notifications (D-19). Resolved during implementation, 2026-09-01: the ExceptionID numbering mechanism (D-71) and the Approval Worklist's scope (D-70).
