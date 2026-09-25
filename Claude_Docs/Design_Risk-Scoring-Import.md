# Web Interface Design Document — Risk Scoring: Import Mapping

**Blueprint Progress Tracking Web Interface**

## Scope

Split out of `Design_Risk-Scoring.md` on 2026-09-21, once that document had grown to 375 lines — more than 2x any other document in this folder, a gap `Testing_Audit-Findings.md` had flagged since 2026-09-09. This document covers the import-mapping/ETL side of the Risk Scoring feature (D-119) specifically: how the four ETL-fed imports (and the analyst-facing bulk CSV uploads that share the same code path) turn a source file's own column headers into this app's internal fields.

See `Design_Risk-Scoring.md` for the data model itself (Targets, Access Groups, the mapping tables, target matching/dedup, the scoring algorithm) and its Implementation Status section for the full build history, including this layer's own build (Phase B).

## Import Mechanics

**New for this app:** a CSV bulk-upload-with-generated-template pattern for the analyst-managed entities (Targets, Access Groups, and account risk-score overrides) — confirmed no precedent exists anywhere in this app today (every existing CSV import is ETL-only, not a web admin upload). Both **bulk import and single add are needed** (confirmed 2026-09-05), matching this app's existing single-add-or-bulk pattern used elsewhere (Identity Providers/Secrets Store already support single add/edit; this adds the bulk path on top):
- `GET /api/admin/targets/import-template` → downloads a CSV with the correct headers. Given identifiers now live in their own extensible table rather than fixed columns, the template has one identifier column per currently-seeded `IdentifierType` (`TargetType,TargetName,RiskScore,Description,ADGuid,ADSid,FQDN,Hostname,IPAddress,LinuxHostSignature,LdapBaseDN,DatabaseInstanceName,ApplianceSerialNumber`, generated from `dim_target_identifier_type` rather than hardcoded, so a newly-added identifier type automatically appears in future template downloads) — a row only needs to fill in whichever identifier columns actually apply to that Target.
- `POST /api/admin/targets/import` (multipart file upload) → parses, validates every row (reports errors per row rather than failing the whole batch on one bad row), and runs each row through the same `usp_MatchOrCreateTarget` dedup logic used by the ETL feed (so a manual CSV upload and an automated discovery feed can't create duplicate Target rows for the same real machine) — same shape for Access Groups (`GroupName,GroupIdentifier,GroupScope,BaseRiskScore,Description`).
- Both entities also get a single add/edit form.
- The `ManualImport` mechanism for direct Account→Target links (see `Design_Risk-Scoring.md`'s "Direct Account→Target Access") gets the same single-add-or-bulk-CSV shape — an analyst picks an Account and a Target and links them directly, or uploads a CSV of `AccountKey`/`SourceAccountId`-plus-`TargetIdentifier` pairs.

**ETL side:** a new `usp_Load_TargetInventory` (AD-discovery-style target seeding, using the same `usp_MatchOrCreateTarget` matching), `usp_Load_AccessGroupTargetMap`, `usp_Load_AccountAccessGroupMembership`, and `usp_Load_AccountTargetMap` (the `ETL`-sourced one of direct-target-access's three mechanisms — see `Design_Risk-Scoring.md`'s "Direct Account→Target Access") — naming to match the existing `usp_Load_*` convention, fed by new `stg_*` staging tables under the same `ImportBatchId`/`SourceFileName`/`LoadTimestamp` convention as every other staging table. Exact file formats are TBD until each actual external feed is specified. The `PendingSafeDerived` mechanism needs no file at all — `usp_DeriveAccountTargetMap_FromPendingSafes` runs entirely off data already in `fact_account`/`dim_safe`, called from `usp_RunFullLoad` alongside the other Load steps.

## Import Field Mapping — configurable, not hardcoded (confirmed 2026-09-05)

Every external feed's file format is still undefined (per Open Questions below) because it's controlled by whatever discovery/inventory tool produces it, not by this app — and that tool, or its column naming, could change later, or a second tool with a differently-shaped export could get added for the same feed. Rather than hardcoding a fixed set of expected column names per feed (which breaks the moment a source format shifts, and needs a code change to fix), **each of the four ETL-fed imports gets a configurable field-mapping layer**: an admin defines which *source* column name corresponds to which *internal* field, once per distinct file shape, and the load logic reads through that mapping rather than assuming fixed headers.

```sql
-- web.import_mapping_profile: one named mapping per distinct source file
-- shape (e.g. "AD Discovery Tool v2", "Legacy CMDB export") -- more than
-- one profile can exist per FeedType, since more than one tool/version can
-- produce differently-shaped files for the same underlying import.
CREATE TABLE web.import_mapping_profile (
    ImportMappingProfileKey INT IDENTITY(1,1) PRIMARY KEY,
    FeedType              NVARCHAR(50)     NOT NULL, -- 'TargetInventory' / 'AccessGroupTargetMap' / 'AccountAccessGroupMembership' / 'AccountTargetMap'
    ProfileName           NVARCHAR(200)    NOT NULL,
    IsActive              BIT              NOT NULL DEFAULT 1,
    Description           NVARCHAR(1000)   NULL,
    CreatedBy             INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedBy            INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_import_mapping_profile UNIQUE (FeedType, ProfileName)
);

-- web.import_mapping_field: per profile, which source column feeds which
-- internal field. TargetFieldName values are a fixed, documented list per
-- FeedType (e.g. TargetInventory's internal fields are TargetType,
-- TargetName, DiscoverySource, plus one per dim_target_identifier_type row
-- -- Hostname, FQDN, ADGuid, etc.) -- the flexibility is in what SOURCE
-- column maps to each one, not in inventing new internal fields.
CREATE TABLE web.import_mapping_field (
    ImportMappingFieldKey   INT IDENTITY(1,1) PRIMARY KEY,
    ImportMappingProfileKey INT              NOT NULL REFERENCES web.import_mapping_profile(ImportMappingProfileKey),
    SourceColumnName        NVARCHAR(200)    NOT NULL, -- the literal header found in the actual file
    TargetFieldName         NVARCHAR(100)    NOT NULL, -- the internal field this maps to
    IsRequired              BIT              NOT NULL DEFAULT 0,
    DefaultValue            NVARCHAR(300)    NULL,      -- used when the source column is blank/missing and not required
    CONSTRAINT UQ_import_mapping_field UNIQUE (ImportMappingProfileKey, TargetFieldName)
);
```

At import time (ETL or an admin's own bulk CSV upload alike), the loading logic reads the file's header row, looks up the selected (or active) mapping profile for that `FeedType`, and for every internal `TargetFieldName` it needs, pulls the value from whichever `SourceColumnName` the profile says corresponds to it — falling back to `DefaultValue`, or raising a per-row validation error if `IsRequired` and genuinely missing. Swapping to a differently-shaped export from the same or a different tool becomes "edit a mapping profile," not a code change. This reconciles with the CSV-template-download feature above: uploading the app's own generated template needs no mapping at all (its headers already match the internal field names 1:1), while uploading an existing external export as-is just needs a one-time profile defined for that shape.

`TargetFieldName`'s fixed list per `FeedType` currently excludes `RiskScore`/`BaseRiskScore` — see the future-use-case note below on why that exclusion is a current omission, not a structural limit.

## Open Questions

- **The `ETL`-sourced direct Account→Target feed's exact file shape** — confirmed as one of three mechanisms (see `Design_Risk-Scoring.md`'s "Direct Account→Target Access"); the actual file format is still undefined until that external source is specified, though the field-mapping layer above means this matters less than it used to (a differently-shaped file just needs its own mapping profile, not a code change).
- **Interactive mapping UX** — v1 as designed is a plain form (an admin already knows the source column names and types them in). A "upload a sample file, auto-detect its headers, map interactively" UX would be nicer but is a real chunk of extra work — worth deciding whether that's in scope for a later refinement.
- **Mapping a risk score directly on import (future use case, flagged 2026-09-21, not designed or built now).** `RiskScore` (on `dim_target`) and `BaseRiskScore` (on `dim_access_group`) are currently always analyst-set — deliberately excluded from every `TargetFieldName` list a mapping profile can target, per `Design_Risk-Scoring.md`'s own schema comments. A plausible future extension: if a source discovery/inventory tool already computes its own risk rating for a target or group, let a mapping profile map that column onto `RiskScore`/`BaseRiskScore` directly, instead of requiring an analyst to re-enter a value the source system already produced. Not designed now — this is a note to keep in mind so the mapping mechanism (already generic over `TargetFieldName`) doesn't get built or extended in a way that structurally forecloses it later (e.g., hardcoding "the fields a profile can target" to assume they're always analyst-only, rather than just not offering `RiskScore`/`BaseRiskScore` as options today). Whoever picks this up later will also need to decide the harder question this doc doesn't attempt: whether an imported score should simply set `RiskScore`/`BaseRiskScore` outright, or interact with `EffectiveRiskScore`'s existing override mechanism somehow — genuinely undecided, not just unbuilt.

## See also

- `Design_Risk-Scoring.md` — the data model, target matching/dedup, the scoring algorithm, the admin/report pages, and the full Implementation Status build history for this entire feature (D-119), including this import layer's own Phase B.
