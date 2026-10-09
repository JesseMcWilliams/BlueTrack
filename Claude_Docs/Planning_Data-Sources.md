# BlueTrack: Data Sources admin page (proposal)

> **Stage: Planning.** Not built. Requested 2026-10-08: "add a Data Sources page on Admin, so it is easy to add new sources and manage sources." **Revised 2026-10-09** to the scheduled-feeds approach below, which the user preferred over the first proposal (per-system SQL staging and import code, Run now through SQL Server Agent). Once the open questions are answered, the decisions get `D-n` rows and the build is tracked in `Planning_Backlog.md`; once built, this becomes `Design_Data-Sources.md`.

## Why

- The two CyberArk sources' settings (Privilege Cloud export folder, Self-Hosted EVD database, which sources exist) are baked into the SQL Agent job when the installer creates it (D-146, D-176). Changing them means re-running the installer or editing the job in SSMS.
- Other systems (CSV exports of groups, accounts, servers and applications) can only be loaded by hand, one upload at a time, on the Bulk Actions pages.
- Nobody can see from the app whether last night's import worked, or start one.

## Decided so far

| Question | Answer | When |
|---|---|---|
| Approach | **Scheduled feeds over the existing imports** (this document), not new SQL staging and import code per system. | 2026-10-09 |
| Other systems' format and content | **CSV files** of **groups, accounts, servers and applications.** | 2026-10-08 |
| Accounts from other systems | **Added to Account Progress as Discovered**, with the **source they came from tracked**. | 2026-10-09 |
| Sources per type | At most one per type. | 2026-10-08 |
| Run now | Yes, with a **warning and confirmation during business hours** (default 07:00 to 18:00, Monday to Friday, server local time; configurable). | 2026-10-08 |
| History retention | **15 days, configurable.** | 2026-10-08 |
| Permission | A new **`ManageDataSources`** permission, granted to the bootstrap Admin role. | 2026-10-08 |
| Account identity in another system | **Username + address**, within that system. | 2026-10-09 |
| A feed account later onboarded into CyberArk | **Linked** to the CyberArk account, and its progress **moves from Discovered to Onboarded** (to Vault). | 2026-10-09 |
| A linked pair in lists and reports | **Only the CyberArk account is shown**; the linked feed account is hidden, with a **filter** to show linked feed accounts when wanted. | 2026-10-09 |
| How strictly to match | **Exact match** (username + address) to link. Possible matches found by a looser comparison, including **DNS forward and reverse lookups** of the addresses, go to a **remediation report** for a person to act on; they are never linked automatically. | 2026-10-09 |
| Run now for the CyberArk sources | **No.** They keep running on the nightly SQL Agent job only. | 2026-10-09 |
| When feeds run | **After** the CyberArk Import+Load job. | 2026-10-09 |
| Start building? | **Not yet** (2026-10-09: "don't build anything yet"). | |

Superseded by the approach change: per-system staging tables and import procedures, `usp_Import_Configured` as the runner for other systems, and **Run now** through `SQLAgentOperatorRole`. With no Run now for the CyberArk sources (2026-10-09), no `msdb` grant is needed at all.

## What already exists to build on

- **CSV import pipelines** (Bulk Actions pages), each validating row by row and returning counts plus per-row errors: target inventory (servers), access group inventory (groups), access group → target, account → access group membership, account → target links (D-119; each can use a **D-105 mapping profile** to map a system's own column names), applications and safe → application assignments (D-180).
- **In-app scheduling:** `NotificationCheckBackgroundService` and `AdAccountDiscoveryBackgroundService` already run inside the API on a timer, each with its own DI scope per run.
- **Source tracking for accounts:** `fact_account.SourceSystemKey` → `dim_source_system` (`PRIVCLOUD`, `SELFHOSTED`, `DISCOVERY`), and Account Progress has a **Discovered** stage. The nightly CyberArk load only soft-deletes missing accounts **within its own source** (`03_BlueTrack_ETL_FactLoads.sql`), so accounts under another source are left alone.

## Proposed design

### 1. Feeds

A new table, `web.data_feed`, one row per scheduled CSV feed:

| Column | Purpose |
|---|---|
| `DataFeedKey` | Identity. |
| `DisplayName` | Admin-chosen, e.g. "Linux server inventory". |
| `FeedType` | Which existing import it runs: `TargetInventory`, `AccessGroupInventory`, `AccessGroupTargetMap`, `AccountAccessGroupMembership`, `AccountTargetMap`, `Applications`, `SafeAssignments`, plus the new `AccountInventory` (5). |
| `SourceSystemKey` | The system the data comes from (`dim_source_system`). Required for `AccountInventory`, so each account records where it came from. |
| `FolderPath`, `FileNamePattern` | Where the file is; the name may contain `{yyyy-MM-dd}` for today's date. Read by the **app pool's account** (see 7). |
| `MappingProfileKey` | Optional D-105 mapping profile, for feed types that support one. |
| `IsEnabled`, `DisplayOrder`, created/modified by and date | As on other admin tables; every change audit-logged. |

Adding a new CSV system becomes configuration: a `dim_source_system` row (added on the page), a mapping profile if its columns differ from the template, and a feed. No new SQL, staging tables or load code.

### 2. One import service per pipeline, shared by upload and feed

The import logic moves out of `RiskScoringImportController` and `ApplicationMappingImportController` into services that take a stream of CSV rows and return the existing result shape. The Bulk Actions uploads and the scheduled feeds then run **the same code** and behave identically. This refactor comes first and changes no behavior; the existing contract tests prove it.

### 3. Running feeds: nightly and Run now

- A `DataFeedBackgroundService`, following the AD Discovery pattern, runs every enabled feed once a night **after the CyberArk Import+Load job** (decided 2026-10-09), in `DisplayOrder`, each one isolated so one failure doesn't stop the rest. Its start time is a Global Application Configuration setting, defaulting to 04:00: after the 02:00 Import+Load job and the 03:00 audit-log purge. (A load that overruns past 04:00 would overlap; reading the job's state from `msdb` would avoid that, but needs a grant this design otherwise avoids, so a configurable time is the starting point.)
- **Run now** (one feed, or all) calls the same service from the page. It applies to feeds only; the CyberArk sources run only on their nightly job (decided 2026-10-09). During business hours it warns and asks for confirmation. Only one run at a time; a second request is refused while one is running.
- A missing file is that feed's error for the run, reported in its status; it doesn't touch existing data.

### 4. Status and history

`web.data_feed_run`: one row per feed per run (start, end, triggered by "schedule" or a user, outcome, the import's counts and row errors as JSON). The page shows each feed's last run and a short history. `DataFeedRunRetentionDays` (Global Application Configuration, default 15) is applied at the start of each run.

### 5. Accounts from other systems (new `AccountInventory` feed type)

- Each row becomes a `fact_account` row under the feed's `SourceSystemKey`, with a `fact_account_progress` row at the **Discovered** stage, so it appears in Account Progress with its source visible.
- **Identity: username + address** within the feed's source (decided 2026-10-09). A row matching an existing account of the same source updates it; no match creates one. Accounts of that source missing from the file are marked deleted, like the CyberArk load does within its own source. The CSV needs at least `UserName` and `Address`; a row missing either is a row error.
- **When the same account appears in CyberArk** (decided 2026-10-09): the feed account is **linked** to the CyberArk account and its progress **moves from Discovered to Onboarded to Vault**. Proposed mechanics:
  - Matching is by username + address against CyberArk accounts (Privilege Cloud and Self-Hosted), run after each feed import and after each nightly CyberArk load, so an account onboarded later is caught.
  - The link is recorded in the existing `account_reconciliation` table (`LegacyAccountKey` = the feed account, `CurrentAccountKey` = the CyberArk account, a new `MatchMethod` such as `FeedUserNameAddress`), the same table `usp_Load_AccountReconciliation` uses for Self-Hosted ↔ Privilege Cloud.
  - The feed account's `fact_account_progress` stage moves to **Onboarded to Vault**, recorded in progress history like any stage change.
  - Only an **exact** match links (username and address equal, ignoring case and surrounding spaces).
  - **Display** (decided 2026-10-09): once linked, the feed account is hidden from Account Progress and the reports, so only the CyberArk account shows and the account is counted once. A filter ("Show linked feed accounts") brings the hidden rows back into view.
- **Possible-match remediation report** (decided 2026-10-09): a new report lists feed accounts that may be the same as a CyberArk account but weren't linked, because the match wasn't exact. Candidates come from a looser comparison: the same username with addresses that agree once normalized (short name versus FQDN, case), or that resolve to the same host by **DNS forward lookup** (name to IP) or **reverse lookup** (IP to name). Each row shows both accounts and why they were paired. Acting on a row, to link the pair or dismiss it, is recorded through `account_reconciliation`'s `IsConfirmed`/`RejectedFlag`. DNS lookups run on the app server as the app pool's account, during each feed run, with results cached for the run so one address isn't resolved repeatedly.
- Must stay excluded from the CyberArk auto-advance rules, as `DISCOVERY` accounts are (`36_BlueTrack_FixAutoAdvanceForDiscoveredAccounts.sql`); the link above is the only thing that advances them.

### 6. The CyberArk sources

Stay on the existing SQL import and nightly Agent job (D-176), which already works. The change: their settings (enabled, Privilege Cloud folder and file names, EVD database) move into a table edited on the same page, and the job's Import step becomes `EXEC usp_Import_Configured;`, which reads it, so no setting is baked into the job. The installer writes these rows from its answers and recreates the job.

### 7. The page, permissions and file access

- **Admin > Data Sources**: CyberArk sources (settings, enabled; no Run now) and feeds (list, add, edit, enable/disable, **Test**, **Run now**, history), following the list/grid, breadcrumb and delete-confirm conventions.
- **Test** checks, as the app pool's account, that the folder and today's file exist and that the header row has the expected (or mapped) columns. For CyberArk, it checks the EVD database exists (`DB_ID`).
- `ManageDataSources` guards the page and its API.
- **File access:** feeds are read by the **app pool's account**: `IIS APPPOOL\<pool>` for a local folder, the computer account `DOMAIN\HOSTNAME$` for a network share, or the gMSA if the pool uses one. That account needs read access to each feed folder. (The CyberArk Privilege Cloud folder is still read by SQL Server's service account.)

## Phasing

1. **Shared import services** (2), no behavior change. **Built 2026-10-09:** `App/Api/Imports/RiskScoringImportService.cs` and `ApplicationMappingImportService.cs`; every existing test passed unchanged (xUnit 456, Vitest 137, Playwright 58). For phase 2: `ImportTargetInventoryAsync` records the user who ran it (`TargetMatchingService.MatchOrCreateAsync`), so a scheduled feed needs a user to attribute its changes to, for example a dedicated system account.
2. **Feeds** (1, 3, 4, 7) for the existing pipelines: registry, background runner, Run now, status and history, page.
3. **CyberArk settings on the page** (6): settings table, `usp_Import_Configured`, job and installer changes.
4. **Account inventory feed** (5): accounts as Discovered with their source, exact-match linking to CyberArk accounts, hiding linked feed accounts (with the filter), and the possible-match remediation report.

## Open questions

None outstanding as of 2026-10-09. Details to settle when each phase is built, with real sample files: the account CSV's full column list, and which permission guards acting on the remediation report.
