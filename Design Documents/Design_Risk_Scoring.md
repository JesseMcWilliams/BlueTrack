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
- **An analyst can override an account's computed score** (confirmed 2026-09-05) — a separate override value that, when populated, takes precedence over the computed one for display/sorting/prioritization purposes everywhere the score is shown.

## Data Sources — confirmed directly, not assumed

| Relationship | Source |
|---|---|
| Target risk score (base data) | Analyst-set, via the web admin page — bulk CSV upload or single add/edit |
| Access Group base risk score | Analyst-set, via the web admin page — bulk CSV upload or single add/edit |
| Access Group → Target mapping ("this group grants access to these targets") | **Backend ETL, from a new external inventory feed** — confirmed 2026-09-05. CyberArk's own exports carry no such data (Safe/vault permissions are a different thing entirely), so this is a new source system, not an extension of the existing PC/SH ETL |
| Account → Access Group membership ("this account is a member of this group") | **Backend ETL, from a new external feed** (e.g., an AD group membership export) — confirmed 2026-09-05, same reasoning as above |
| Account → Target direct access (bypassing a group) | **A separate, different feed from Account→Group** — confirmed 2026-09-05. The user also noted some of this data may already be recoverable from accounts sitting in `_Pending`-named safes (the same safe-naming convention D-91's auto-advance logic already keys off of) — worth checking directly against real data once this feed exists, rather than assumed to fully replace it; flagged as something to verify, not designed around yet |
| Target inventory (the row set itself, not just its risk score) | **Both**: seeded/refreshed via AD discovery tooling (a new ETL feed) *and* analyst-added, single or bulk, to fill the gaps automated discovery tools always leave — confirmed 2026-09-05 |

Every ETL-sourced relationship above needs the same import-batch tracking already used everywhere else in this app's ETL layer (`ImportBatchId`/`SourceFileName`/`LoadTimestamp` per staging row, one `import_log` row per run) — this satisfies "track when and where they were imported from" using an established pattern rather than a new one. A new `dim_source_system` row (e.g. `ACCESSINVENTORY`) is proposed for this feed, following the existing `PRIVCLOUD`/`SELFHOSTED`/`DISCOVERY` pattern. Distinct from that file-level provenance, `dim_access_group` also needs to record *where a group was found* (confirmed 2026-09-05) — see `DiscoverySource`/`FoundOnTargetKey`/`GroupScope` below, since "which import run loaded this row" and "where in the managed environment this group actually lives" are two different facts.

## Proposed Schema (draft — not yet applied as a migration)

All new tables live in the `web` schema (this is operational data the web app itself manages and reports on, distinct from the `dbo` CyberArk-warehouse layer), except where noted.

**Revised 2026-09-05 (D-103):** the original design gave `dim_target` its own typed columns for Hostname/FQDN/IP/ADGuid/ADSid, but the same message that confirmed those also added Linux host signatures, LDAP directories, databases, and appliances each needing their *own* identifying scheme — a clear sign this list keeps growing as more Target types get added, and a wide table with an ever-increasing set of mostly-NULL columns is the wrong shape for that. Switched to a generic, extensible identifier table instead, matching this app's existing `dim_risk_level`-style controlled-list-with-an-order-column pattern (`RiskOrder`):

```sql
-- web.dim_target_identifier_type: the controlled, EXTENSIBLE list of ways
-- a Target can be identified -- adding a new kind of identifier later (e.g.
-- a container/pod ID) is a new seeded row here, not a schema change.
CREATE TABLE web.dim_target_identifier_type (
    IdentifierType       NVARCHAR(50) PRIMARY KEY,   -- 'ADGuid' / 'ADSid' / 'FQDN' / 'Hostname' / 'IPAddress' /
                                                       -- 'LinuxHostSignature' / 'LdapBaseDN' / 'DatabaseInstanceName' /
                                                       -- 'ApplianceSerialNumber'
    MatchPriority        INT NOT NULL,                -- lower = stronger signal, checked first during import matching
    RequiresReview        BIT NOT NULL DEFAULT 0       -- 1 = a match on this type alone routes to analyst review, not an auto-merge (D-102's confirmed choice for IP-only matches)
);
-- Seeded: ADGuid/ADSid/LdapBaseDN/DatabaseInstanceName/ApplianceSerialNumber/
-- LinuxHostSignature all priority 10 (system-assigned, authoritative
-- identifiers); FQDN priority 20; Hostname priority 30; IPAddress priority
-- 40, RequiresReview = 1 (weakest signal -- DHCP reassignment, NAT).

-- web.dim_target: any final destination (server/desktop/database/
-- application/LDAP directory/appliance/etc). Row set comes from BOTH
-- AD-discovery-style ETL tooling and analyst add/bulk-CSV (to fill the
-- gaps discovery tools always leave, confirmed 2026-09-05); the risk score
-- itself is always analyst-set regardless of how the Target row got there.
CREATE TABLE web.dim_target (
    TargetKey            INT IDENTITY(1,1) PRIMARY KEY,
    TargetType           NVARCHAR(50)     NOT NULL,  -- 'Server' / 'Desktop' / 'Database' / 'Application' / 'LdapDirectory' / 'Appliance' / 'Other'
    TargetName           NVARCHAR(300)    NOT NULL,  -- display name
    InternalGuid         UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), -- this app's own stable identity for the target, distinct from any externally-discovered identifier -- confirmed 2026-09-05
    ApplicationKey       INT              NULL REFERENCES web.dim_application(ApplicationKey), -- set only when TargetType = 'Application'
    RiskScore            INT              NOT NULL,  -- 0-1000, analyst-set
    Description          NVARCHAR(1000)   NULL,
    DiscoverySource      NVARCHAR(100)    NULL,       -- tool/method that found this target, e.g. 'AD Discovery', 'Manual'
    CreatedBy            INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedBy            INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    ImportBatchId         UNIQUEIDENTIFIER NULL,     -- set only when the row came from a bulk CSV upload or ETL feed
    SourceFileName        NVARCHAR(260)    NULL,
    CONSTRAINT UQ_dim_target_InternalGuid UNIQUE (InternalGuid)
);

-- web.target_identifier: every recognized identifier value for a target --
-- a target can (and often will) have several (e.g. a Windows server has
-- both an FQDN and an ADGuid; nothing requires exactly one row per type).
CREATE TABLE web.target_identifier (
    TargetIdentifierKey  INT IDENTITY(1,1) PRIMARY KEY,
    TargetKey            INT NOT NULL REFERENCES web.dim_target(TargetKey),
    IdentifierType       NVARCHAR(50) NOT NULL REFERENCES web.dim_target_identifier_type(IdentifierType),
    IdentifierValue      NVARCHAR(300) NOT NULL,
    CONSTRAINT UQ_target_identifier UNIQUE (IdentifierType, IdentifierValue)
);
-- This UNIQUE constraint is exactly the database-enforced dedup guarantee
-- the old wide-column design couldn't have (NULLs made a composite UNIQUE
-- across Hostname/FQDN/IP/ADGuid/ADSid meaningless) -- one real identifier
-- value can only ever point at one Target.

-- web.dim_access_group: a privileged-access group in the managed
-- environment -- NOT CyberArk's own dim_group (vault/Safe permissions).
CREATE TABLE web.dim_access_group (
    AccessGroupKey        INT IDENTITY(1,1) PRIMARY KEY,
    GroupName             NVARCHAR(300)    NOT NULL,
    GroupIdentifier       NVARCHAR(300)    NOT NULL, -- e.g. AD group SID/DN, matched by the ETL feed
    GroupScope            NVARCHAR(20)     NOT NULL DEFAULT 'Domain', -- 'Local' / 'Domain'
    FoundOnTargetKey      INT              NULL REFERENCES web.dim_target(TargetKey), -- populated when GroupScope = 'Local': which target this local group lives on
    DiscoverySource       NVARCHAR(100)    NULL,      -- tool/method that found this group, e.g. 'AD Discovery', 'Manual'
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
-- FoundOnTargetKey ties a LOCAL group directly to the server it lives on
-- (e.g. "Administrators" local group on SQLPROD01) -- a local admin group
-- found on a high-risk target is itself an obvious risk signal, so this
-- target is treated as automatically reachable by the group (folded into
-- its own reachable-target set alongside whatever access_group_target_map
-- adds), not just descriptive metadata.

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

-- web.account_risk_score: the computed, cumulative account-level score,
-- plus an analyst override that takes precedence when set (confirmed
-- 2026-09-05). A separate table rather than a column on
-- fact_account_progress, since fact_account_progress lives in dbo
-- (ETL/warehouse-refreshed) and this needs its own independent
-- staleness/recalculation lifecycle.
CREATE TABLE web.account_risk_score (
    AccountKey              BIGINT PRIMARY KEY REFERENCES dbo.fact_account(AccountKey),
    ComputedRiskScore       INT    NULL,      -- 0-1000; NULL until first calculated
    RiskScoreCalculatedDate DATETIME2 NULL,
    IsRiskScoreStale        BIT    NOT NULL DEFAULT 1,
    OverrideRiskScore       INT    NULL,      -- 0-1000; when set, takes precedence over ComputedRiskScore everywhere
    OverrideReason          NVARCHAR(1000) NULL,
    OverrideSetBy           INT    NULL REFERENCES web.app_user(UserKey),
    OverrideSetDate         DATETIME2 NULL,
    EffectiveRiskScore      AS (COALESCE(OverrideRiskScore, ComputedRiskScore)) PERSISTED
);
```

`EffectiveRiskScore` is what the Account Progress list and the Risk Score report both sort/filter/display on -- a persisted computed column so it indexes and sorts cheaply, rather than every consumer re-deriving `COALESCE(...)` itself. **Confirmed 2026-09-05: an override requires a reason** (`OverrideReason`, mirroring `web.risk_exception.Justification`), and **clearing the override (setting it back to blank/NULL) makes it ignored, reverting to the computed value** -- exactly what `EffectiveRiskScore = COALESCE(OverrideRiskScore, ComputedRiskScore)` already does by construction, so no schema change was needed to satisfy this, just confirmation the existing design already behaves this way.

## Target Matching (import-time deduplication)

Confirmed 2026-09-05, revised same day (D-103) once the identifier list grew to include Linux/LDAP/Database/Appliance identifiers alongside Windows' `ADGuid`/`ADSid`: every Target import (bulk CSV, AD-discovery ETL feed, or any future source) needs to check an incoming row's identifier values against existing `web.target_identifier` rows before deciding "new target" vs. "update this existing one" -- proposed as a dedicated `usp_MatchOrCreateTarget` procedure, driven entirely by `dim_target_identifier_type.MatchPriority`/`RequiresReview` rather than a hardcoded column list:

1. Look up every existing `target_identifier` row matching any of the incoming row's (IdentifierType, IdentifierValue) pairs, ordered by `MatchPriority` ascending (system-assigned identifiers like `ADGuid`/`ADSid`/`LdapBaseDN`/`DatabaseInstanceName`/`ApplianceSerialNumber`/`LinuxHostSignature` first, then `FQDN`, then `Hostname`, then `IPAddress` last).
2. If the best (lowest-priority-number) match found is on a type where `RequiresReview = 0` → treat as the same Target; add/update any of the incoming row's other identifier values onto that same `TargetKey` (a target can and often will carry several identifier rows at once — e.g. a Windows server has both an `FQDN` and an `ADGuid`).
3. If the *only* match found is on a `RequiresReview = 1` type (today, only `IPAddress`) → **do not auto-merge** (confirmed 2026-09-05, "1 is a good suggestion"). Insert a row into a new review queue instead:

```sql
-- web.target_match_review: a weak (IP-only, etc.) match an analyst needs
-- to confirm rather than one this app silently merges or splits.
CREATE TABLE web.target_match_review (
    TargetMatchReviewKey  INT IDENTITY(1,1) PRIMARY KEY,
    CandidateTargetKey    INT NULL REFERENCES web.dim_target(TargetKey), -- the existing target it weakly matched, if any
    IdentifierType        NVARCHAR(50) NOT NULL REFERENCES web.dim_target_identifier_type(IdentifierType),
    IdentifierValue       NVARCHAR(300) NOT NULL,
    ImportBatchId         UNIQUEIDENTIFIER NULL,
    SourceFileName        NVARCHAR(260)    NULL,
    CreatedDate           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    ResolvedBy            INT              NULL REFERENCES web.app_user(UserKey),
    ResolvedDate          DATETIME2        NULL,
    Resolution            NVARCHAR(20)     NULL  -- 'Merged' / 'NewTarget' / 'Ignored'
);
```

4. No match on anything → new Target row, plus a `target_identifier` row for every identifier value the import supplied.

The review queue needs its own small admin page (e.g. `admin/TargetMatchReview.vue`) for an analyst to resolve each pending row — not yet scoped in detail below; added to Admin & Report Pages as a placeholder.

**Staleness/recalculation, per the user's own suggestion (2026-09-05):** `IsRiskScoreStale` is set to `1` whenever a Target's `RiskScore`, an Access Group's `BaseRiskScore`, or any of the three mapping tables change (via ETL load or a manual admin edit). A new `usp_RecalculateRiskScores` procedure recomputes every stale Access Group's `ComputedRiskScore` first, then every stale Account's `ComputedRiskScore` (which depends on its groups' now-current scores) — called both from `usp_RunFullLoad` (so ETL-driven changes are picked up automatically on the normal load cycle) and from a new "Recalculate Now" action in the admin UI, for a manual edit made between load runs. Recalculation never touches `OverrideRiskScore` — an analyst override stays put until an analyst changes or clears it, regardless of how often the underlying computed value refreshes.

## Algorithm — open, needs a decision

The user flagged this directly as "the difficult part." Both candidates below start from the same first step: resolve an entity's (an Account's, or an Access Group's) full set of reachable Targets, **deduplicated** — this matters because if an account reaches the same high-risk domain controller through two different group memberships, that target's risk must count once, not twice.

**Candidate A — "dominant risk plus a shrinking tail."** Sort reachable targets' risk scores descending: r₁ ≥ r₂ ≥ ... ≥ rₙ. Score = min(1000, r₁ + Σᵢ₌₂ⁿ rᵢ × decayⁱ⁻¹) for a tunable decay constant (e.g. 0.3–0.5). Plain-language story: "your score is mostly driven by the single riskiest thing you can reach, plus a shrinking bonus for each additional thing" — easy to explain to an analyst, but needs a decay constant tuned/calibrated against real data, and needs an explicit cap at 1000.

**Candidate B — "combined exposure probability."** Treating each target's risk/1000 as a probability of being the source of a real exposure, and combining them as independent risks: Score = 1000 × (1 − Πᵢ(1 − rᵢ/1000)). Self-bounding by construction (never exceeds 1000, no cap needed) and needs no arbitrary decay constant — but the underlying story ("probability at least one access point is exploited," an OR-combination of independent risks) is a less immediately intuitive pitch than Candidate A, even though it's a standard pattern in risk-scoring/fraud-scoring systems.

An Access Group's score = the same formula applied to its own reachable-target set, with its `BaseRiskScore` folded in as one more value in that same set before sorting/combining (so a group's manually-flagged sensitivity and its actual target access compete/combine the same way a target's own risk would).

**Recommendation:** prototype both against a small set of realistic example accounts/groups once some real Target/Access Group data exists, and compare which one's ranking "feels right" to the analyst who'll actually use it — this is exactly the kind of judgment call that's hard to get right blind, so treat this section as a starting point for that comparison, not a locked decision.

**Implementation shape, confirmed 2026-09-05: a stored procedure, with a top-level dispatcher over swappable candidate implementations** -- so both candidates can be built and compared live against real data rather than one being picked and hard-coded in application code:

```sql
-- Top-level entry point -- callers (usp_RecalculateRiskScores, an admin
-- "Recalculate Now" action) never call a candidate directly.
CREATE PROCEDURE usp_CalculateRiskScore
    @TargetSetKey NVARCHAR(20),   -- 'Account' or 'AccessGroup'
    @EntityKey BIGINT
AS
BEGIN
    DECLARE @Algorithm NVARCHAR(50) = (SELECT ActiveRiskAlgorithm FROM web.app_config);
    IF @Algorithm = 'DominantPlusTail'
        EXEC usp_CalculateRiskScore_DominantPlusTail @TargetSetKey, @EntityKey;   -- Candidate A
    ELSE IF @Algorithm = 'CombinedExposure'
        EXEC usp_CalculateRiskScore_CombinedExposure @TargetSetKey, @EntityKey;   -- Candidate B
END
```

`ActiveRiskAlgorithm NVARCHAR(50) NOT NULL DEFAULT 'DominantPlusTail'` is proposed as a new column on the existing `web.app_config` singleton (surfaced on the existing Global Application Configuration admin page — no new settings page needed for just this one switch), so comparing the two candidates is an admin-configurable choice, not a deployment.

## Import Mechanics

**New for this app:** a CSV bulk-upload-with-generated-template pattern for the analyst-managed entities (Targets, Access Groups, and account risk-score overrides) — confirmed no precedent exists anywhere in this app today (every existing CSV import is ETL-only, not a web admin upload). Both **bulk import and single add are needed** (confirmed 2026-09-05), matching this app's existing single-add-or-bulk pattern used elsewhere (Identity Providers/Secrets Store already support single add/edit; this adds the bulk path on top):
- `GET /api/admin/targets/import-template` → downloads a CSV with the correct headers. Given identifiers now live in their own extensible table rather than fixed columns, the template has one identifier column per currently-seeded `IdentifierType` (`TargetType,TargetName,RiskScore,Description,ADGuid,ADSid,FQDN,Hostname,IPAddress,LinuxHostSignature,LdapBaseDN,DatabaseInstanceName,ApplianceSerialNumber`, generated from `dim_target_identifier_type` rather than hardcoded, so a newly-added identifier type automatically appears in future template downloads) — a row only needs to fill in whichever identifier columns actually apply to that Target.
- `POST /api/admin/targets/import` (multipart file upload) → parses, validates every row (reports errors per row rather than failing the whole batch on one bad row), and runs each row through the same `usp_MatchOrCreateTarget` dedup logic used by the ETL feed (so a manual CSV upload and an automated discovery feed can't create duplicate Target rows for the same real machine) — same shape for Access Groups (`GroupName,GroupIdentifier,GroupScope,BaseRiskScore,Description`).
- Both entities also get a single add/edit form.

**ETL side:** a new `usp_Load_TargetInventory` (AD-discovery-style target seeding, using the same `usp_MatchOrCreateTarget` matching), `usp_Load_AccessGroupTargetMap`, and `usp_Load_AccountAccessGroupMembership` (naming to match the existing `usp_Load_*` convention), fed by new `stg_*` staging tables under the same `ImportBatchId`/`SourceFileName`/`LoadTimestamp` convention as every other staging table. Direct Account→Target access is confirmed to come from yet another, separate feed (`usp_Load_AccountTargetMap`) — exact file formats for all of these are TBD until the actual external feeds are defined.

## Admin & Report Pages (new)

- **Targets** admin page (`admin/Targets.vue`) — list/create/edit/delete, plus bulk CSV upload + template download. Editing a target's identifiers manages its `target_identifier` rows underneath. New permission: `ManageTargets`.
- **Access Groups** admin page (`admin/AccessGroups.vue`) — same shape, for Access Groups and their base risk score. New permission: `ManageAccessGroups`.
- **Target Match Review** admin page (`admin/TargetMatchReview.vue`) — a queue of pending `target_match_review` rows (weak/IP-only matches) for an analyst to resolve as Merged/NewTarget/Ignored. Likely gated by the same `ManageTargets` permission rather than a new one, unless a narrower gate is wanted.
- **Risk Score report** — accounts sorted/filtered by computed risk score, with drill-down into which Access Groups/Targets are driving a given account's score. New permission: `ViewRiskReport`.
- **Account Progress list** gets two new columns (confirmed 2026-09-05): `EffectiveRiskScore` (sortable, the computed value or the override if one is set) and an editable override column/action — populating it takes precedence over the computed score everywhere. Editing the override likely needs `EditAccountProgress` (the existing permission already gating edits on this page) rather than a new one, unless a narrower override-specific permission is wanted.

## Open Questions

- **Algorithm choice** — Candidate A vs. B above, or a third; needs either a decision or a small prototype-and-compare pass against sample data before this is locked in. The dispatcher design means both can be built and compared live rather than picked blind.
- **Direct Account→Target grants**, confirmed to come from a separate feed from Account→Group — exact file/matching shape not yet defined (depends on what that feed actually looks like), and the `_Pending` safes angle the user raised is worth checking against real data before assuming it's a full substitute for that feed.
- **`fact_account.Address` reconciliation** — `Address` is a raw, unnormalized string already on `fact_account`; once Targets have real identifier values, is there a need to backfill/link existing accounts' `Address` values to a matching Target (for accounts imported before this feature existed), or does that happen naturally once the Account→Target/Account→Group ETL feeds start running?
- **Server/Desktop/Database Target subtypes** — does v1 need type-specific extra fields (e.g. environment tier, OS), or is the current generic shape sufficient to start?
- **`web.target_match_review`'s resolution actions** — sketched as Merged/NewTarget/Ignored, but the exact admin workflow for "Merged" (pick which existing Target to merge into, if `CandidateTargetKey` is wrong) isn't designed yet.

---
*New document added 2026-09-05, following the user's request for a computed risk-scoring system to help prioritize work. Schema/algorithm above are a first-draft proposal for review, not yet applied as a migration or implemented — see Open Questions before treating any of this as final.*
