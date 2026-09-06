# Database

Every script below is run through DbUp by `App/Migrator` (see its own
top-of-file comment) **except** `00_BlueTrack_CreateDatabase.sql` and
`14_BlueTrack_ScheduleImportLoadJob.sql`, which Migrator always excludes
regardless of any skip-list argument -- both for structural reasons, not
convenience (00 must `USE master`, DbUp cannot; 14 must `USE msdb`, and
DbUp's own post-script journal write then fails against the wrong
database -- see each script's own header and `App/Migrator/Program.cs`).
Every DbUp-managed script uses DbUp's `$DatabaseName$` substitution token
for the target database name (never a hardcoded literal, per D-89) -- the
name comes from whatever `Initial Catalog` the caller's connection string
specifies. `14` is the one exception: since it never runs through DbUp,
it uses sqlcmd's own `$(DatabaseName)` scripting-variable syntax instead
(same idea, different tool, see its header for the exact command).

This layout is the result of a 2026-09-05 restructure: the original
`01`-`25` sequence had accumulated a long tail of small, hand-written
gap-fill scripts (each one only safe to write because D-58 forbids
editing an already-applied schema file directly once real data exists).
The user explicitly authorized abandoning that caution for this pass --
the database was still initial development and fully rebuildable -- so
every gap-fill script that only ever existed because of D-58 has been
folded back into its natural home below. **D-58 itself still applies
going forward**: once an environment holds real tracked data again,
further schema changes go back to being small guarded scripts appended
after `14`, never edits to an existing file in this list.

## Build order

| # | File | Purpose |
|---|---|---|
| 00 | `00_BlueTrack_CreateDatabase.sql` | Standalone, run once by hand, before pointing Migrator at a brand-new environment. Creates the target database if missing. Never runs through DbUp -- see its own header for why (DbUp's single-connection, single-database journal model is structurally incompatible with a script that switches to `master`). Migrator's own C# bootstrap already does the equivalent create-if-missing check before every run, so this script exists for visibility/manual use, not because the tooling needs it. |
| 01 | `01_BlueTrack_CoreSchema.sql` | The `dbo` schema: every dimension, staging, fact, and bridge table for the CyberArk-mirroring/ETL side of the project. Drops and recreates every table it defines -- safe only because it always starts from an empty database. |
| 02 | `02_BlueTrack_ETL_DimensionLoads.sql` | Dimension-table load procedures (`usp_Load_Dim*`) plus `usp_Load_GroupMembership` and the `ufn_YNToBit` helper. Split out of the original `02_BlueTrack_ETL_LoadProcedures.sql`. |
| 03 | `03_BlueTrack_ETL_FactLoads.sql` | Fact-table load procedures (`usp_Load_FactAccount`, `usp_Load_FactAccountProgress`, `usp_Load_AccountProgressAutoAdvance`, `usp_Load_FactSafeEntitlement`). Split out of the same original file. |
| 04 | `04_BlueTrack_ETL_ReportingViews.sql` | Reporting views (`vw_effective_safe_access`, `vw_export_account_progress`, `vw_review_platform_sor_accounttype`). Split out of the same original file. |
| 05 | `05_BlueTrack_AccountReconciliation.sql` | Cross-source (Self-Hosted <-> Privilege Cloud) account reconciliation: `usp_Load_AccountReconciliation` and its review views. |
| 06 | `06_BlueTrack_PowerBI_Support.sql` | `dim_date` population, Power BI-facing views, `usp_Load_FactAccountProgressHistory`, and **`usp_RunFullLoad`** -- the orchestrator that calls every load procedure above in dependency order. Defined here because this is the first point in the sequence where every procedure it calls already exists. |
| 07 | `07_BlueTrack_SourceImport.sql` | OPERATIONAL, not one-time: the `usp_Import_PC_*`/`usp_Import_SelfHosted_EVD` procedures that populate the `stg_*` staging tables from real exports, plus `usp_Import_All`. Re-run every time a fresh set of source exports needs loading, followed by `EXEC usp_RunFullLoad;`. |
| 08 | `08_BlueTrack_WebSchema.sql` | The `web` schema: every table backing the web interface (authentication, authorization, risk exceptions, audit logging, application structure, interface extensibility, secrets store, session cache, user preferences) plus `web.vw_account_application_exception`. Re-runnable in Dev via its own drop-then-recreate cleanup pass -- see its header. |
| 09 | `09_BlueTrack_WebSeed.sql` | Minimum-viable seed: one enabled WindowsIntegrated identity provider, a bootstrap Admin role bundling every confirmed permission, and a mapping from `BUILTIN\Administrators` (SID `S-1-5-32-544`) to that role. |
| 10 | `10_BlueTrack_DefaultRoleSeed.sql` | Confirmed default roles: Viewer, Analyst, Approver, Auditor, with their permission bundles. |
| 11 | `11_BlueTrack_DevFakeAuthSeed.sql` | DevFakeAuth identity provider (disabled by default) for exercising every authorization path against a local, non-domain Windows account in Development. Requires a manual edit (`@DevFakeAuthUsername`) to actually map a user. |
| 12 | `12_BlueTrack_OidcSamlProviderSeed.sql` | Disabled OIDC and SAML placeholder identity provider rows, documenting the expected `ConfigurationValues` shape ahead of real IdP metadata. |
| 13 | `13_BlueTrack_AccountProgressFieldMetadataSeed.sql` | Seeds `web.account_progress_field_metadata` with one row per editable `fact_account_progress` column, so the Account Progress edit form has field definitions to render. |
| 14 | `14_BlueTrack_ScheduleImportLoadJob.sql` | Creates the nightly SQL Server Agent job (Import then Load, 2:00 AM) once Import and Load have both been confirmed working manually. Runs against `msdb`, not the target database. **Never run through `App/Migrator`, for any environment** -- always excluded (see above); run it manually via `sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i 14_BlueTrack_ScheduleImportLoadJob.sql`. Both the job name and schedule name embed the substituted database name so `BlueTrack` and `BlueTrackTest` (if ever scheduled on the same SQL Server instance) get distinctly-named jobs rather than colliding. |

`Test/` holds test-only fixtures (`01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql`,
`02_BlueTrack_Test_SyntheticAccountData.sql`) -- never run against a real
environment, only against a disposable `BlueTrackTest`. Migrator is invoked
a second time, separately, against the `Database/Test` folder (DbUp tracks
one folder per run).

## Building a brand-new environment from scratch

1. Run `00_BlueTrack_CreateDatabase.sql` by hand against `master` (or just
   let `App/Migrator` create the database for you -- it does the same
   check-then-create automatically before every run).
2. `dotnet run --project App/Migrator -- "<connection string>" "Database"`
   -- runs `01` through `13` in order (`14` is always excluded -- see above).
3. For a test database only: `dotnet run --project App/Migrator -- "<connection string>" "Database/Test"`.
4. Load real data: run `07_BlueTrack_SourceImport.sql`'s procedures (or
   `usp_Import_All`), then `EXEC usp_RunFullLoad;`.
5. For a real (non-disposable) environment only, once Import and Load have
   both been confirmed working manually at least once: run `14` by hand
   via sqlcmd (see its row above).

## Folded-in history

These scripts existed only because D-58 forbade editing an
already-applied schema file once real data existed. Each is now folded
directly into the file listed -- consult git history for the original
standalone version if you need the exact incremental diff.

| Retired script | Folded into |
|---|---|
| `08_BlueTrack_FixMovePermissionAlias.sql` | `01_BlueTrack_CoreSchema.sql` |
| `17_BlueTrack_AccountTypeSeed.sql` | `01_BlueTrack_CoreSchema.sql` |
| `11_BlueTrack_FixWindowsGroupSidFormat.sql` | `09_BlueTrack_WebSeed.sql` (the SID fix was already applied there directly) |
| `12_BlueTrack_ExceptionIdNumbering.sql` | `08_BlueTrack_WebSchema.sql` |
| `13_BlueTrack_AuditEventTypes.sql` | `08_BlueTrack_WebSchema.sql` |
| `14_BlueTrack_SecretsStoreSchema.sql` | `08_BlueTrack_WebSchema.sql` |
| `15_BlueTrack_LockTimeoutConfig.sql` | `08_BlueTrack_WebSchema.sql` |
| `19_BlueTrack_ApplicationExceptionView.sql` | `08_BlueTrack_WebSchema.sql` |
| `20_BlueTrack_SessionCacheSchema.sql` | `08_BlueTrack_WebSchema.sql` |
| `21_BlueTrack_ReadEventType.sql` | `08_BlueTrack_WebSchema.sql` |
| `23_BlueTrack_UserPreferenceSchema.sql` | `08_BlueTrack_WebSchema.sql` |
| `10_BlueTrack_SeedDbUpJournal.sql` | Retired outright, not folded -- it backfilled DbUp's journal for an environment where scripts had already been applied by hand outside DbUp tracking. Not relevant once every environment is rebuilt fresh through this sequence. |
| `25_BlueTrack_DeploymentInfoPermissionSeed.sql` | Retired outright, not folded -- fully redundant for a fresh install: `ViewDeploymentInfo` is already in `08`'s seed and already covered by `09`'s blanket "every existing permission" Admin bootstrap grant. |

Renumbered without any content change beyond the header: `01` (was
`01_BlueTrack_CreateDatabase_Schema.sql`), `05` (was
`03_BlueTrack_AccountReconciliation.sql`), `06` (was
`04_BlueTrack_PowerBI_Support.sql`), `07` (was `05_BlueTrack_SourceImport.sql`),
`08` (was `06_BlueTrack_WebInterface_Schema.sql`, content also
consolidated -- see table above), `09` (was `07_BlueTrack_WebInterface_Seed.sql`),
`10` (was `24_BlueTrack_DefaultRoleSeed.sql`), `11` (was
`18_BlueTrack_DevFakeAuthSeed.sql`), `12` (was `22_BlueTrack_OidcSamlProviderSeed.sql`),
`13` (was `16_BlueTrack_AccountProgressFieldMetadataSeed.sql`), `14` (was
`09_BlueTrack_ScheduleImportLoadJob.sql`; also fixed two hardcoded-name
bugs found while renumbering it -- see its own header).

## See also

- `Import_Load_Process_Guide.docx` -- operational runbook for the Import/Load
  cadence and the nightly Agent job.
- `Design Documents/Design_Decision_Register.md` -- the full record of every
  numbered decision (`D-nn`) referenced throughout these scripts' comments.
- `Lessons_Learned.md` -- real incidents/gotchas found while building this,
  several of which are directly encoded in these scripts (the BULK INSERT
  `ROWTERMINATOR` fix in `07`, the `$DatabaseName$`/`$(DatabaseName)`-only
  rule everywhere, CRLF line endings expected by SSMS).
