# Web Interface Design Document — Interface Extensibility for Account Progress Tracking

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

`fact_account_progress` has already grown twice since this project started (Source of Record, then Risk Level), and will likely keep growing as the Blueprint engagement matures. This document defines how the web interface accommodates that growth without every new field requiring changes scattered across multiple screens, DTOs, and validation rules.

## Design Principle: Governed Fields Stay Governed

New fields that need to be reported on, filtered, or trusted for compliance purposes should continue to be added as real, typed database columns via proper migration — the same practice used throughout this project so far. This document is not proposing a generic Entity-Attribute-Value (EAV) schema for `fact_account_progress` itself; a fully dynamic schema would undermine the reportability this whole system exists for.

What does need to be flexible is the application layer above the database — specifically, avoiding a design where adding one governed column means editing five different hardcoded places (an API model, a validation rule, an edit form, a display view, an export mapping) by hand, one at a time, with the risk of missing one.

## Field-Metadata-Driven Pattern

A single, central list of field definitions — one entry per `fact_account_progress` column that the interface exposes — drives both the edit form and the API contract, rather than each being hand-built separately.

| Field | Type | Purpose |
|---|---|---|
| FieldName | text | Matches the underlying column name |
| DisplayLabel | text | What the user sees on the form |
| FieldType | text, controlled list | Text / Date / Dropdown-from-reference-table / Number, etc. |
| ReferenceTable | text, nullable | For dropdown fields — which `dim_` table supplies the options (e.g., `dim_risk_level`) |
| IsRequired | bit | Drives both client-side and server-side validation from one place |
| RequiredPermission | text, references app_permission | Which specific permission is needed to edit this field — not every field needs the same permission (e.g., editing `RiskLevelKey` could require a different permission than editing `Notes`) |
| DisplayOrder | int | Controls form layout without a code change |

Adding a new governed field then looks like: run the schema migration (as already practiced), add one row to this field-definition list, and the form/API pick it up automatically — no hunting through UI markup for every place the old field list was hardcoded.

## Decision: No Ad Hoc Fields

Considered and explicitly rejected: every new field will continue to go through a proper schema migration, consistent with how `dim_account_type`, `dim_source_of_record`, and `dim_risk_level` were all added earlier in this project. Nothing further is needed here — this section exists to record that the option was considered, not to leave it as an unresolved gap. (D-08)

## Implementation Status (updated 2026-09-01)

Both sides are now built. The admin side: Field Metadata Management (CRUD against `account_progress_field_metadata`, `FieldMetadataController`) lets an admin curate the field-definition list itself. The consuming side: the Account Progress edit form (`AccountProgressDetail.vue`) reads that same list (via a separate, non-admin-gated `GET /api/account-progress/field-metadata` — any user with `EditAccountProgress` needs to read it, not just admins) and renders one input per row, ordered by `DisplayOrder`, typed by `FieldType` (`Text`/`Date`/`Dropdown`/`TextArea` — the last one a self-evident addition beyond the doc's own "etc." list, for `Notes`). `Dropdown` fields pull their options from `GET /api/account-progress/reference-data`, keyed by `ReferenceTable`.

Seeded with one row per editable `fact_account_progress` column (`16_BlueTrack_AccountProgressFieldMetadataSeed.sql`) — everything except `ExceptionKey`, which the Risk Exception workflow sets, not this form.

`RequiredPermission` stays unused (null on every seeded row) per D-20's deferral of per-field permission granularity — every field is currently gated only by the form-level `EditAccountProgress` permission.

## Open Questions

**Resolved 2026-08-27:** Per-field permission granularity is deferred, not needed from day one. Ship with a single blanket "can edit this record at all" permission initially; `RequiredPermission` stays in the field-metadata model for later use, but per-field enforcement isn't built now. Revisit if a concrete need for differentiated field-level permissions actually comes up. (D-20)

No open questions remain for this document as of 2026-08-27.
