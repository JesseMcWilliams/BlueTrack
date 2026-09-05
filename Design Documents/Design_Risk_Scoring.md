# Web Interface Design Document — Risk Scoring

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

A new, computed risk score for prioritizing work — requested by the user 2026-09-05. This is deliberately a **different concept** from the existing `dim_risk_level` (Low/Medium/High/Critical, manually assigned by an analyst during Stage 2 of account onboarding, `fact_account_progress.RiskLevelKey`) — that stays as-is. This document's score is a computed, numeric value meant to answer "how much does this specific account's access actually expose us," not a coarse onboarding-priority judgment call.

**Core idea, confirmed directly with the user:**
- A **Target** is any final destination an account's access leads to — a server/desktop (an OS endpoint), a database, or an application. Targets carry an analyst-set base risk score (e.g., a domain controller scores very high; a low-value member server scores low).
- An **Access Group** is a privileged-access group in the *managed environment* (e.g., an AD security group like "Server Admins" that grants its members admin rights on a set of servers) — confirmed structurally distinct from this app's existing `dim_group`/`bridge_group_membership`/`fact_safe_entitlement`, which model CyberArk's own *vault-internal* groups used only for Safe permissions, not real-world privileged access on managed targets. Deliberately named **Access Group**, not "Group," to avoid colliding with that existing concept (or with `web.identity_group_role_map`, which is BlueTrack's own login/authorization group mapping — a third, unrelated "group").
- An Access Group carries **both** a manually-set base risk score (an analyst can flag a group as inherently sensitive regardless of its current access — confirmed 2026-09-05) **and** a computed component derived from every Target it can reach (a group with access to a high-risk server inherits that risk; access to multiple servers increases it further).
- An **Account's** risk score is cumulative, based on: every Access Group it belongs to, any Target it can reach directly (bypassing a group — confirmed this happens, though less commonly), and the total breadth of distinct systems it can reach. Most of an account's risk is expected to come through group membership; direct grants are the exception, not the rule.
- Scale: **0–1000, integers only** (no decimals in the display) — confirmed 2026-09-05, chosen over 0–100 for more resolution when combining multiple weighted factors without needing fractional intermediate values.

## Data Sources — confirmed directly, not assumed

| Relationship | Source |
|---|---|
| Target risk score (base data) | Analyst-set, via the web admin page — bulk CSV upload or single add/edit |
| Access Group base risk score | Analyst-set, via the web admin page — bulk CSV upload or single add/edit |
| Access Group → Target mapping ("this group grants access to these targets") | **Backend ETL, from a new external inventory feed** — confirmed 2026-09-05. CyberArk's own exports carry no such data (Safe/vault permissions are a different thing entirely), so this is a new source system, not an extension of the existing PC/SH ETL |
| Account → Access Group membership ("this account is a member of this group") | **Backend ETL, from a new external feed** (e.g., an AD group membership export) — confirmed 2026-09-05, same reasoning as above |
| Account → Target direct access (bypassing a group) | Assumed to come from the same external feed as a distinct row shape, alongside Account→Group rows — **not yet confirmed, see Open Questions** |

Both ETL-sourced relationships need the same import-batch tracking already used everywhere else in this app's ETL layer (`ImportBatchId`/`SourceFileName`/`LoadTimestamp` per staging row, one `import_log` row per run) — this satisfies the "track the source and date of import" requirement using an established pattern rather than a new one. A new `dim_source_system` row (e.g. `ACCESSINVENTORY`) is proposed for this feed, following the existing `PRIVCLOUD`/`SELFHOSTED`/`DISCOVERY` pattern.

## Proposed Schema (draft — not yet applied as a migration)

All new tables live in the `web` schema (this is operational data the web app itself manages and reports on, distinct from the `dbo` CyberArk-warehouse layer), except where noted.

```sql
-- web.dim_target: any final destination (server/desktop/database/application).
-- Analyst-managed (single add/edit + bulk CSV) for the risk score itself;
-- the row set could also be seeded/refreshed via ETL (open question below).
CREATE TABLE web.dim_target (
    TargetKey            INT IDENTITY(1,1) PRIMARY KEY,
    TargetType           NVARCHAR(50)     NOT NULL,  -- 'Server' / 'Desktop' / 'Database' / 'Application' / 'Other'
    TargetName           NVARCHAR(300)    NOT NULL,
    TargetIdentifier     NVARCHAR(300)    NOT NULL,  -- normalized hostname/instance/app-code, matched against fact_account.Address
    ApplicationKey       INT              NULL REFERENCES web.dim_application(ApplicationKey), -- set only when TargetType = 'Application'
    RiskScore            INT              NOT NULL,  -- 0-1000, analyst-set
    Description          NVARCHAR(1000)   NULL,
    CreatedBy            INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedBy            INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    ImportBatchId         UNIQUEIDENTIFIER NULL,     -- set only when the row came from a bulk CSV upload
    SourceFileName        NVARCHAR(260)    NULL,
    CONSTRAINT UQ_dim_target UNIQUE (TargetIdentifier)
);

-- web.dim_access_group: a privileged-access group in the managed
-- environment -- NOT CyberArk's own dim_group (vault/Safe permissions).
CREATE TABLE web.dim_access_group (
    AccessGroupKey        INT IDENTITY(1,1) PRIMARY KEY,
    GroupName             NVARCHAR(300)    NOT NULL,
    GroupIdentifier       NVARCHAR(300)    NOT NULL, -- e.g. AD group SID/DN, matched by the ETL feed
    BaseRiskScore         INT              NOT NULL, -- 0-1000, analyst-set floor
    ComputedRiskScore     INT              NULL,     -- 0-1000, derived from reachable targets + BaseRiskScore
    RiskScoreCalculatedDate DATETIME2      NULL,
    IsRiskScoreStale      BIT              NOT NULL DEFAULT 1,
    Description           NVARCHAR(1000)   NULL,
    CreatedBy             INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedBy            INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    ImportBatchId         UNIQUEIDENTIFIER NULL,
    SourceFileName        NVARCHAR(260)    NULL,
    CONSTRAINT UQ_dim_access_group UNIQUE (GroupIdentifier)
);

-- web.access_group_target_map: which targets a group grants access to.
-- ETL-sourced (confirmed) -- an external inventory feed, not CyberArk.
CREATE TABLE web.access_group_target_map (
    AccessGroupKey        INT NOT NULL REFERENCES web.dim_access_group(AccessGroupKey),
    TargetKey             INT NOT NULL REFERENCES web.dim_target(TargetKey),
    ImportBatchId         UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (AccessGroupKey, TargetKey)
);

-- web.account_access_group_map: which accounts belong to which access
-- groups. ETL-sourced (confirmed) -- e.g. an AD group membership export.
CREATE TABLE web.account_access_group_map (
    AccountKey            BIGINT NOT NULL REFERENCES dbo.fact_account(AccountKey),
    AccessGroupKey        INT    NOT NULL REFERENCES web.dim_access_group(AccessGroupKey),
    ImportBatchId         UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (AccountKey, AccessGroupKey)
);

-- web.account_target_map: DIRECT account-to-target access, bypassing any
-- group. Expected to be the exception, not the rule.
CREATE TABLE web.account_target_map (
    AccountKey            BIGINT NOT NULL REFERENCES dbo.fact_account(AccountKey),
    TargetKey             INT    NOT NULL REFERENCES web.dim_target(TargetKey),
    ImportBatchId         UNIQUEIDENTIFIER NULL,
    SourceFileName        NVARCHAR(260)    NULL,
    LoadTimestamp          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (AccountKey, TargetKey)
);

-- web.account_risk_score: the computed, cumulative account-level score.
-- A separate table rather than a column on fact_account_progress, since
-- fact_account_progress lives in dbo (ETL/warehouse-refreshed) and this
-- needs its own independent staleness/recalculation lifecycle.
CREATE TABLE web.account_risk_score (
    AccountKey            BIGINT PRIMARY KEY REFERENCES dbo.fact_account(AccountKey),
    RiskScore             INT    NULL,      -- 0-1000; NULL until first calculated
    RiskScoreCalculatedDate DATETIME2 NULL,
    IsRiskScoreStale      BIT    NOT NULL DEFAULT 1
);
```

**Staleness/recalculation, per the user's own suggestion (2026-09-05):** `IsRiskScoreStale` is set to `1` whenever a Target's `RiskScore`, an Access Group's `BaseRiskScore`, or any of the three mapping tables change (via ETL load or a manual admin edit). A new `usp_RecalculateRiskScores` procedure recomputes every stale Access Group's `ComputedRiskScore` first, then every stale Account's `RiskScore` (which depends on its groups' now-current scores) — called both from `usp_RunFullLoad` (so ETL-driven changes are picked up automatically on the normal load cycle) and from a new "Recalculate Now" action in the admin UI, for a manual edit made between load runs.

## Algorithm — open, needs a decision

The user flagged this directly as "the difficult part." Both candidates below start from the same first step: resolve an entity's (an Account's, or an Access Group's) full set of reachable Targets, **deduplicated** — this matters because if an account reaches the same high-risk domain controller through two different group memberships, that target's risk must count once, not twice.

**Candidate A — "dominant risk plus a shrinking tail."** Sort reachable targets' risk scores descending: r₁ ≥ r₂ ≥ ... ≥ rₙ. Score = min(1000, r₁ + Σᵢ₌₂ⁿ rᵢ × decayⁱ⁻¹) for a tunable decay constant (e.g. 0.3–0.5). Plain-language story: "your score is mostly driven by the single riskiest thing you can reach, plus a shrinking bonus for each additional thing" — easy to explain to an analyst, but needs a decay constant tuned/calibrated against real data, and needs an explicit cap at 1000.

**Candidate B — "combined exposure probability."** Treating each target's risk/1000 as a probability of being the source of a real exposure, and combining them as independent risks: Score = 1000 × (1 − Πᵢ(1 − rᵢ/1000)). Self-bounding by construction (never exceeds 1000, no cap needed) and needs no arbitrary decay constant — but the underlying story ("probability at least one access point is exploited," an OR-combination of independent risks) is a less immediately intuitive pitch than Candidate A, even though it's a standard pattern in risk-scoring/fraud-scoring systems.

An Access Group's score = the same formula applied to its own reachable-target set, with its `BaseRiskScore` folded in as one more value in that same set before sorting/combining (so a group's manually-flagged sensitivity and its actual target access compete/combine the same way a target's own risk would).

**Recommendation:** prototype both against a small set of realistic example accounts/groups once some real Target/Access Group data exists, and compare which one's ranking "feels right" to the analyst who'll actually use it — this is exactly the kind of judgment call that's hard to get right blind, so treat this section as a starting point for that comparison, not a locked decision.

## Import Mechanics

**New for this app:** a CSV bulk-upload-with-generated-template pattern for the two analyst-managed entities (Targets, Access Groups) — confirmed no precedent exists anywhere in this app today (every existing CSV import is ETL-only, not a web admin upload). Proposed shape, matching this app's existing single-add-or-bulk pattern used elsewhere (e.g. Identity Providers/Secrets Store already support single add/edit; this adds the bulk path on top):
- `GET /api/admin/targets/import-template` → downloads a CSV with the correct headers (`TargetType,TargetName,TargetIdentifier,RiskScore,Description`) and one example row.
- `POST /api/admin/targets/import` (multipart file upload) → parses, validates every row (reports errors per row rather than failing the whole batch on one bad row), and upserts by `TargetIdentifier` (so re-uploading the same file updates existing rows instead of duplicating) — same shape for Access Groups (`GroupName,GroupIdentifier,BaseRiskScore,Description`).
- Both entities also get a single add/edit form, per the user's explicit "bulk import and single add are needed."

**ETL side:** a new `usp_Load_AccessGroupTargetMap`/`usp_Load_AccountAccessGroupMembership` pair (naming to match the existing `usp_Load_*` convention), fed by new `stg_*` staging tables under the same `ImportBatchId`/`SourceFileName`/`LoadTimestamp` convention as every other staging table, sourced from the new external inventory feed (exact file format TBD once that feed is defined).

## Admin & Report Pages (new)

- **Targets** admin page (`admin/Targets.vue`) — list/create/edit/delete, plus bulk CSV upload + template download. New permission: `ManageTargets`.
- **Access Groups** admin page (`admin/AccessGroups.vue`) — same shape, for Access Groups and their base risk score. New permission: `ManageAccessGroups`.
- **Risk Score report** — accounts sorted/filtered by computed risk score, with drill-down into which Access Groups/Targets are driving a given account's score. New permission: `ViewRiskReport`. Given the score's whole purpose is prioritizing work, it should also appear as a sortable column on the existing Account Progress list (**open question below** — confirm before building).

## Open Questions

- **Algorithm choice** — Candidate A vs. B above, or a third option; needs either a decision or a small prototype-and-compare pass against sample data before this is locked in.
- **Direct Account→Target grants' data source** — assumed to come from the same external ETL feed as Account→Group rows (a distinct row shape with no group), not confirmed directly yet.
- **Should the computed Risk Score also appear on the existing Account Progress list/detail pages** (alongside the existing manual `RiskLevel`), not just its own dedicated report? Given "this score will help with prioritizing the work," this seems likely, but wasn't asked directly.
- **Does `dim_target`'s row set itself ever get seeded/refreshed via ETL** (e.g., auto-discovering candidate targets from `fact_account.Address` values), or is the Target list *entirely* analyst-authored (only the risk score is analyst-set, but someone still has to first create each Target row via the admin page or CSV)? The user's answer implied Targets are risk-scored by the analyst but didn't confirm whether the underlying Target inventory itself needs an auto-discovery/seeding aid.
- **`TargetIdentifier` matching against `fact_account.Address`** — `Address` is free-text and can vary for the same physical target (FQDN vs. short hostname vs. IP). Worth deciding now whether v1 requires exact-string matching (simplest, but will under-match in practice) or needs an aliasing mechanism (a Target can have multiple recognized identifiers) — recommend starting with an aliasing table (`web.target_identifier_alias`, TargetKey + Alias) rather than a single `TargetIdentifier` column, to avoid a painful migration later.
- **Server/Desktop/Database Target subtypes** — does v1 need type-specific extra fields (e.g. environment tier, OS), or is TargetType + TargetName + TargetIdentifier + RiskScore sufficient to start?

---
*New document added 2026-09-05, following the user's request for a computed risk-scoring system to help prioritize work. Schema/algorithm above are a first-draft proposal for review, not yet applied as a migration or implemented — see Open Questions before treating any of this as final.*
