# BlueTrack: Data Sources admin page (proposal)

> **Stage: Planning.** Not built. A proposal for review, from a request on 2026-10-08: "add a Data Sources page on Admin, so it is easy to add new sources and manage sources." Once agreed, the decisions get `D-n` rows and the build is tracked in `Planning_Backlog.md`; once built, this becomes `Design_Data-Sources.md` or moves to `Archive_`.

## Why

Today the warehouse's sources are fixed in code and in the SQL Agent job:

- `usp_Import_All` imports one CyberArk Privilege Cloud tenant (seven CSV exports from one folder) and one Self-Hosted vault (the EVD database on the same SQL Server instance).
- Where the files are, and which sources exist, is baked into the job step when `Deploy/Install-BlueTrack.ps1` creates it (D-146, D-176). Changing either means editing the job in SSMS or re-running the installer.
- Nobody can see from the app whether last night's import worked, or start one.

D-176 (2026-10-08) fixed the immediate problem, a Privilege-Cloud-only or Self-Hosted-only site, with `@ImportPrivilegeCloud`/`@ImportSelfHosted` flags set at install time. This proposal moves that choice, and the rest of each source's settings, into the app.

## Decided so far (2026-10-08, via AskUserQuestion)

| Question | Answer |
|---|---|
| What must "add new sources" cover? | **Other systems too**, not only the two CyberArk types. |
| Should the page run imports on demand? | **Yes**: show status, plus a **Run now** button. |
| Permission | A new **`ManageDataSources`** permission, granted to the bootstrap Admin role. |
| D-176 | Merge it first; the page builds on its flags and clearing procedures. |
| What do other systems export? | **CSV files.** |
| Several sources of one type? | **No**: at most one source per type. |
| Run now's `msdb` grant | **`SQLAgentOperatorRole`** for the app pool's login (option 2 below). |
| Run now during business hours? | **Allowed, with a warning** and a confirmation. |
| Import history retention | **15 days, configurable.** |
| What do other systems' CSVs contain? | **Groups, accounts, servers and applications.** |
| Business hours default | **07:00 to 18:00, Monday to Friday, server local time; configurable.** |
| Start building? | **Not yet** (2026-10-08). |

## Proposed design

### 1. Source registry

A new table, `web.data_source`, one row per configured source:

| Column | Purpose |
|---|---|
| `DataSourceKey` | Identity. |
| `SourceType` | Which built-in import adapter handles it (see 2), e.g. `CyberArkPrivilegeCloud`, `CyberArkSelfHosted`. Unique: at most one source per type. |
| `DisplayName` | Admin-chosen name, e.g. "Production Privilege Cloud". |
| `IsEnabled` | Imported by the nightly job and **Run now** only when on. |
| `Settings` | JSON, shaped per `SourceType`, edited through structured fields as in D-95 (Secrets Store). |
| `DisplayOrder`, `CreatedBy/Date`, `ModifiedBy/Date` | As on the other admin tables; changes are audit-logged like every other admin edit. |

Settings for the two existing types:

- **CyberArk Privilege Cloud:** export folder (as seen by the SQL Server service account), the seven file names, and the name pattern for the two dated files (today `Export Entitlements {yyyy-MM-dd}.csv`, `Export Local Group Members {yyyy-MM-dd}.csv`; D-146 found the real names differ from the guide's).
- **CyberArk Self-Hosted:** the EVD database name (same instance).

Seeding: a fresh install gets one row per source from the installer's answers, replacing D-176's `ImportSources`. An existing install gets rows matching what its job does today.

### 2. Source types: what "adding a new system" really takes

Adding a **source** of an existing type is configuration: a new row on the page.

Adding a **type** of source, a new system, can't be configuration alone. Each system needs:

1. its own staging tables (`stg_<type>_*`) and import procedure(s), like `07_BlueTrack_SourceImport.sql`;
2. load logic mapping its data into the warehouse's dimensions and facts (`usp_Load_*`), which is where most of the work is;
3. a settings shape and its fields on the page;
4. registration in the adapter list, so the page offers it and the runner (3) calls it.

So the page is the **registry and control panel** for sources; it cannot invent a new system's import or mapping.

Since other systems export **CSV files**, a new type follows the Privilege Cloud pattern: a folder, file names (with an optional `{yyyy-MM-dd}` date pattern), `BULK INSERT` into staging. Where a system's columns may differ between versions or tools, D-105's `import_mapping_profile` (configurable column mapping per feed type, built for risk-scoring imports) is the existing pattern for mapping file columns to staging columns, so a changed export needs a profile edit, not code. Steps 2 and 4 above (warehouse mapping, registration) are still code per type.

### 3. Running imports: nightly and Run now

- A new procedure, `usp_Import_Configured`, reads the enabled rows in `web.data_source` and runs each one's adapter, then empties the staging tables of types with no enabled source (D-176's clearing rule). The nightly job's Import step becomes just `EXEC usp_Import_Configured;`, with no paths or flags in the job.
- **Run now** starts the existing Agent job, so the import runs exactly as at night (same account, same file access) rather than inside the web request. Starting a job needs rights in `msdb`. Options, least privilege first:
  1. **A certificate-signed stored procedure** in `msdb` that can start only this one job, with `EXECUTE` granted to the app pool's login. Narrowest, but needs a DBA to create the certificate and procedure once.
  2. **`SQLAgentOperatorRole`** in `msdb` for the app pool's login. One standard role, but it can start and stop **every** job on the instance.
  3. A dedicated `msdb` role in the D-107 style. Note that `sp_start_job` checks job ownership and Agent roles, so a plain `EXECUTE` grant isn't enough on its own.

  **Decided: option 2.** A hand-run script (like 38/40/41, never through the Migrator) adds the app pool's login to `SQLAgentOperatorRole` in `msdb`, after a user for it exists there. That role can also start and stop every other job on the instance; the script's header says so, so a DBA grants it knowingly.
- Only one run at a time: **Run now** is refused while the job is running.
- **During business hours**, **Run now** warns that the load rebuilds the warehouse in one transaction and may block reports until it finishes, and asks for confirmation. Business hours come from new Global Application Configuration settings: start, end and working days, server local time, defaulting to 07:00 to 18:00, Monday to Friday.
- Import and Load stay separate steps. **Run now** runs the whole job (Import then Load), as at night.

### 4. Import status

New tables `dbo.import_run` (one row per run: start, end, triggered by "schedule" or a user, outcome, error) and `dbo.import_run_source` (one row per source per run: rows loaded per staging table, outcome, error). `usp_Import_Configured` writes them. The page shows each source's last run and a short history, and the Deployment page's health checks can show "last import failed" too.

**Retention: 15 days, configurable.** A new Global Application Configuration setting, `ImportHistoryRetentionDays` (default 15). `usp_Import_Configured` deletes runs older than that at the start of each run, so no separate purge job is needed.

### 5. The page

`Admin > Data Sources`, following the list/grid, breadcrumb and delete-confirm conventions (`Design_Application-Structure.md`, "Cross-Cutting UI Conventions"):

- **List:** name, type, enabled, last run (time, outcome), and **Run now** for the whole job.
- **Add / Edit:** type (fixed after creation), name, enabled, the type's structured settings, and a **Test** action that checks the settings without importing (the folder and files exist for Privilege Cloud, the EVD database exists for Self-Hosted).
- **History:** recent runs with per-source row counts and errors.

### 6. Permissions and SQL grants

- `ManageDataSources` (new, granted to the bootstrap Admin role), on the page and its API.
- The app pool's login gets `SELECT/INSERT/UPDATE/DELETE` on `web.data_source` through its existing `db_datareader`/`db_datawriter` (script 41); no new database grant.
- **Run now**'s `msdb` grant (`SQLAgentOperatorRole`, see 3) is the only new SQL permission, and it is optional: without it, the page shows status and settings, and **Run now** explains what to grant.

## Phasing

1. **Registry and runner:** `web.data_source`, seeding, `usp_Import_Configured`, the job step change, the page (list, add, edit, enable/disable, Test) for the two CyberArk types. Replaces D-176's install-time choice.
2. **Status:** `import_run` tables, page history, Deployment health check.
3. **Run now:** the `msdb` script, the button, and refusing a second run.
4. **Each new system type:** its own change set (staging, import, load mapping, settings fields), one type at a time, once its export format is known.

### What other systems' CSVs feed

Other systems export **groups, accounts, servers and applications**. Each maps onto a part of the warehouse that already exists, which keeps phase 4 to new staging tables, imports and mappings rather than new warehouse structure:

| CSV content | Likely destination (to confirm per system) |
|---|---|
| Groups | `web.dim_access_group` (Risk Scoring, D-119), as the Access Groups bulk import already does. |
| Accounts | `fact_account` and `fact_account_progress`, matched against CyberArk's accounts the way `usp_Load_AccountReconciliation` (`05_BlueTrack_AccountReconciliation.sql`) already matches Self-Hosted and Privilege Cloud accounts. |
| Servers | `web.dim_target` and `web.target_identifier` (D-119), as the Targets bulk import already does. |
| Applications | `web.dim_application`, with the Application ↔ Safe mapping. |

## Open questions

1. **Per system, before its phase-4 work:** the actual file layout (sample files), and how its accounts should match CyberArk's (by name, address, both?). Not needed for phases 1 to 3.
