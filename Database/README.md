# Database

`App/Migrator` runs the scripts at the top level of this folder through
DbUp, in file-name order, recording each one in `dbo.SchemaVersions`. It
never reads the subfolders: `Manual/` holds scripts run by hand, and `Test/`
holds test-only seeds that CI applies to `BlueTrackTest` in a second run.

Every Migrator script uses DbUp's `$DatabaseName$` token for the target
database name, never a literal (D-89): the name comes from the
`Initial Catalog` of the connection string the Migrator is given. The
manual scripts that run through `sqlcmd` use its `$(DatabaseName)` variable
instead.

## Build order

| # | File | Purpose |
|---|---|---|
| 01 | `01_BlueTrack_Baseline_CoreSchema.sql` | The `dbo` schema: every dimension, staging, fact and bridge table for the CyberArk-mirroring/ETL side, with its reference rows. |
| 02 | `02_BlueTrack_Baseline_EtlLoads.sql` | The dbo load procedures (`usp_Load_*`), the name-pattern functions (`fn_MatchesNamePattern`, `fn_RegexSupported`, D-186), account reconciliation, `dim_date`, and the reporting and Power BI views. |
| 03 | `03_BlueTrack_Baseline_SourceImport.sql` | Operational: the `usp_Import_*` procedures that fill the `stg_*` tables from real exports, and `usp_Import_All` (source selection, D-176). |
| 04 | `04_BlueTrack_Baseline_WebSchema.sql` | The `web` schema (D-64): authentication, authorization, risk exceptions, audit, settings, secrets store, session cache, preferences, credentials/LDAP, notifications, risk scoring, AD account discovery, data feeds and account deletion, with confirmed reference rows; plus the two dbo columns that point into it. |
| 05 | `05_BlueTrack_Baseline_WebSeed.sql` | Starting data: identity providers (WindowsIntegrated on; DevFakeAuth, OIDC and SAML off), the bootstrap Admin role with every permission mapped to `BUILTIN\Administrators` (S-1-5-32-544), the default roles, Account Progress field metadata, and the data feeds' system user. |
| 06 | `06_BlueTrack_Baseline_WebLogic.sql` | Risk scoring procedures and functions, the audit-log purge procedure, the decommissioning views, Targets and links from account addresses (D-123, D-193), and **`usp_RunFullLoad`**, the nightly orchestrator. |

Scripts after the baseline start at `07`. None yet.

## The baseline (D-195)

Before the first release, the numbered scripts `01`–`51` were consolidated
into the six baseline scripts above. They build exactly what the old
scripts built, schema and seed data alike, but in final form: later
columns and constraints are part of each `CREATE TABLE`, each procedure,
view and function appears once (its last version), and data fixes that did
nothing on a new database are gone. Proof: a database built from each set
compared with `Deploy/Compare-BlueTrackSchema.ps1 -IncludeData` shows no
differences. The old scripts are in git history, at the tag
`pre-sql-baseline`.

The baseline scripts run only against an empty database. DbUp records
scripts by file name only, so on a database built from the old scripts it
would treat the baseline as new and run it, wiping the data. `App/Migrator`
prevents that: if the journal shows the old scripts up to
`51_BlueTrack_TargetsFromCyberArkAddress.sql`, it records the six baseline
scripts as applied without running them. A database built from the old
scripts but not up to `51` is refused; upgrade it first from the
`pre-sql-baseline` tag, then again from the current code.

## Adding a script

1. Name it `NN_BlueTrack_<Topic>.sql`, with `NN` the next number (`07` is next).
2. Start with a header comment: the file name, `RUN THIS AFTER 01-NN.`, what
   it changes and why (with the `D-n` decision), and whether it's safe to
   re-run.
3. Make it safe to re-run, because it will run against databases holding
   real data: guard every schema change (`IF COL_LENGTH(...) IS NULL`,
   `IF OBJECT_ID(...) IS NULL`, `IF NOT EXISTS (SELECT 1 FROM sys...)`), use
   `CREATE OR ALTER` for procedures, views and functions, and guard seed
   rows with `NOT EXISTS`. Never drop and recreate a table that holds data.
4. Begin with `USE $DatabaseName$;` and `GO`.
5. Never edit a script that has already run anywhere, including the
   baseline: change things with a new script.
6. A new permission needs its own grant to the Admin role, since 05's
   "every permission" grant ran only once.
7. To apply it by hand instead of through the Migrator, use `sqlcmd -I`
   (QUOTED_IDENTIFIER on); see `Claude_Docs/Reference_Lessons-Learned.md`.

## Building a new environment

1. Optional: run `Manual/01_BlueTrack_CreateDatabase.sql` against `master`. The
   Migrator also creates the database if it's missing.
2. `dotnet run --project App/Migrator -- "<connection string>" "Database"`.
3. Test database only: `dotnet run --project App/Migrator -- "<connection string>" "Database/Test"`.
4. Load real data: `EXEC usp_Import_All ...;` then `EXEC usp_RunFullLoad;`.
5. Run the manual scripts that apply (below). `Deploy/Install-BlueTrack.ps1`
   runs step 2 and can also install the nightly job and the App Pool's grants.

## Manual scripts (`Manual/`)

Numbered in the order they're run; run only the ones that apply.

| File | When and how |
|---|---|
| `01_BlueTrack_CreateDatabase.sql` | Optional, before the first Migrator run. Connects to `master`, which DbUp can't. Replace `$DatabaseName$` by hand first. |
| `02_BlueTrack_GrantAppServiceAccountAccess.sql` | Gives the App Pool's Windows account its database permissions (D-30, D-171). The installer's `Db.AppPoolAccess` step runs a filled-in copy; by hand, replace `__TARGET_ACCOUNT__` first. |
| `03_BlueTrack_GrantBackupStatusReaderRole.sql` | When the Deployment page's backup-status check should work: lets the app's account read `msdb` backup history (D-107). Replace `__TARGET_ACCOUNT__`, or download a filled-in copy from the Group / Role Mapping page. |
| `04_BlueTrack_ScheduleImportLoadJob.sql` | Real environments, once Import and Load have each been run by hand and work: creates the nightly 2:00 AM Import + Load SQL Agent job in `msdb`. `sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql`. The installer's nightly-job option runs a filled-in copy. |
| `05_BlueTrack_ScheduleAuditLogPurgeJob.sql` | Real environments, once `RetentionDays` is set on Global Application Configuration: the 3:00 AM job calling `usp_PurgeAuditLog`. Same `sqlcmd` form. |
| `06_BlueTrack_DevFakeAuthUserMapping.sql` | Development only: maps your Windows username to Admin through DevFakeAuth. Replace `@DevFakeAuthUsername` first. |

The SQL Agent and backup-status scripts can't run through DbUp: they
`USE msdb;` and never switch back, so DbUp's journal write after them
would fail against `msdb`.

## Drift check

To find what a live database has that the scripts don't (or the reverse),
build a reference database from this folder on the same SQL Server and
compare:

```powershell
dotnet run --project App/Migrator -- "Server=<server>;Database=BlueTrackReference;Integrated Security=true;TrustServerCertificate=true" "Database"
.\Deploy\Compare-BlueTrackSchema.ps1 -SqlServerInstance <server> -ReferenceDatabase BlueTrackReference -DifferenceDatabase BlueTrack -IgnoreComments
```

Drop `BlueTrackReference` afterwards. `-IgnoreComments` hides comment-only
differences in procedures and views (a script's comment edited after the
database was built). Without `-IncludeData`, data is not compared.

## See also

- `User_Docs/Admin_DeploymentRunbook.md`: standing up or rebuilding an environment.
- `Import_Load_Process_Guide.docx`: the Import/Load cadence and the nightly job.
- `Claude_Docs/Design_Decision-Register.md`: every `D-n` referenced in the scripts.
- `Claude_Docs/Reference_Lessons-Learned.md`: incidents encoded in these
  scripts (the BULK INSERT `ROWTERMINATOR` fix, the `$DatabaseName$` rule,
  `sqlcmd -I`).
