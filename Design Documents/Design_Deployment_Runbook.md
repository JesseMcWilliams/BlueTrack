# Deployment Runbook

**Blueprint Progress Tracking**

## Purpose & Scope

The step-by-step procedure for standing up a brand-new BlueTrack environment (or fully rebuilding one), and the checklist of environment-specific items a full redeployment must not skip. Complements, rather than duplicates, two existing documents:

- `Database/README.md` -- the script manifest (what each numbered file does, dependency order, folded-in history).
- `Database/Import_Load_Process_Guide.docx` -- the day-to-day operational cadence for Import/Load once an environment is already running.

This document exists because those two answer "what does each piece do," not "in what order do I actually run everything, and what must I remember to change for *this* environment." Written 2026-09-05, alongside the script restructure (D-106) that made a genuinely repeatable procedure possible for the first time -- see that Decision Register entry for the full rationale.

## Step-by-Step: New or Fully Rebuilt Environment

1. **Create the database.** Either run `Database/00_BlueTrack_CreateDatabase.sql` by hand against `master` (edit the `$DatabaseName$` placeholder first -- it's never substituted, since this file never runs through DbUp), or just proceed to step 2: `App/Migrator`'s own bootstrap creates the database automatically if it doesn't exist.
2. **Run the schema/seed sequence.**
   ```
   dotnet run --project App/Migrator -- "<connection string>" "Database"
   ```
   Runs `01` through `13` in dependency order. `14_BlueTrack_ScheduleImportLoadJob.sql` is **always** excluded by `App/Migrator` itself, for every environment -- see step 5 and `Database/README.md`'s own note on why (a structural DbUp incompatibility, not a disposable-vs-real distinction).
3. **Test environments only:**
   ```
   dotnet run --project App/Migrator -- "<connection string>" "Database/Test"
   ```
   Seeds `Database/Test`'s DevFakeAuth role matrix and synthetic accounts. Never run against a real environment.
4. **Load real data** -- see "First Data Load" below.
5. **Real (non-disposable) environments only, once step 4 has been confirmed working manually at least once:**
   ```
   sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i Database/14_BlueTrack_ScheduleImportLoadJob.sql
   ```
   Creates the nightly Import+Load SQL Agent job. Must be run manually, via sqlcmd, never through `App/Migrator` -- confirmed 2026-09-05 that running it through Migrator breaks DbUp's own journal write for that script (see the script's own header and `App/Migrator/Program.cs`).

## First Data Load

Staging tables are empty immediately after step 2/3 above -- nothing populates them until you run the import procedures explicitly. `Database/Import_Load_Process_Guide.docx` is the authoritative day-to-day reference; the shape of a first run is:

```sql
EXEC usp_Import_All
    @EVDDatabaseName = 'CyberArkSH',
    @PlatformsFile = 'C:\Code\BlueTrack\Reference\PrivilegedCloud\Export_PlatformsList.csv',
    @UsersFile = 'C:\Code\BlueTrack\Reference\PrivilegedCloud\Export_UsersList.csv',
    @GroupsFile = 'C:\Code\BlueTrack\Reference\PrivilegedCloud\Export_GroupsList.csv',
    @GroupMembersFile = 'C:\Code\BlueTrack\Reference\PrivilegedCloud\Export Local Group Members <date>.csv',
    @SafesFile = 'C:\Code\BlueTrack\Reference\PrivilegedCloud\Export_SafesList.csv',
    @AccountsFile = 'C:\Code\BlueTrack\Reference\PrivilegedCloud\Export_AccountsList.csv',
    @EntitlementsFile = 'C:\Code\BlueTrack\Reference\PrivilegedCloud\Export Entitlements <date>.csv',
    @EntitlementsExportDate = '<date>';
GO
EXEC usp_RunFullLoad;
```

Use the actual date stamp on the two date-stamped export files on disk (`Export Entitlements <date>.csv`, `Export Local Group Members <date>.csv`), not today's date -- those two filenames are only built from the current date automatically inside the scheduled job (`14`)'s own Import step, not by `usp_Import_All` itself. `usp_Import_SelfHosted_EVD` requires `@EVDDatabaseName`'s database to be reachable on the *same* SQL Server instance (a same-instance cross-database query, not a linked server) -- confirm it exists and is `ONLINE` before running this. BULK INSERT (used by every `usp_Import_PC_*` procedure) requires the SQL Server *service account*, not your own login, to have read access to the file path given.

## Environment-Specific Items a Redeployment Must Not Skip

None of these are inferred from the connection string the way `$DatabaseName$`/`$(DatabaseName)` is -- a human has to set each one deliberately for a *new* environment:

- **`11_BlueTrack_DevFakeAuthSeed.sql`'s `@DevFakeAuthUsername` placeholder** (Development only) -- ships as `'REPLACE_WITH_YOUR_WINDOWS_USERNAME'` and is a no-op until a developer edits it to their own Windows username and re-runs the file.
- **`14`'s hardcoded EVD database name (`CyberArkSH`) and export folder (`C:\Code\BlueTrack\Reference\PrivilegedCloud`)** -- real, confirmed values for *this* host, not placeholders in the committed file. A redeploy to a different server needs both edited in `14` before it's run.
- **`09_BlueTrack_WebSeed.sql`'s bootstrap admin group** (`BUILTIN\Administrators`, SID `S-1-5-32-544`) -- a deliberate bootstrap default so a fresh install is usable immediately. Map a real AD/Entra group to the Admin role via the Group/Role Mapping admin screen once one exists; don't leave every local admin as a permanent BlueTrack Admin in a real production environment.
- **OIDC/SAML identity providers** (`12`) -- seeded disabled, with placeholder `ConfigurationValues` documenting the expected shape only. Need real IdP tenant/metadata entered via the Identity Providers admin page, then enabling, before either is usable.
- **Secrets Store backend** (`08`'s `web.secrets_store` seed) -- `WindowsDpapi` is active by default (D-36's first-built backend). A production cutover to CyberArk CP/CCP/Conjur, Azure Key Vault, or AWS Secrets Manager happens through the Secrets Store Configuration admin page, not by editing the seed script.
- **D-58 resumes immediately once real data exists.** This restructure's "fold everything back into its parent file" approach (D-106) was a one-time reset explicitly authorized because the database had nothing worth protecting yet. The moment a real environment holds real tracked data again, further schema changes go back to being small, guarded, numbered scripts appended after `14` -- never edits to `01`-`14` themselves.

## Verification

After any full rebuild:
- `dotnet test App/Api.Tests` -- unit/integration/contract, against `BlueTrackTest` only.
- `npm run test` in `App/Web` -- Vitest.
- `npm test` in `App/E2E` -- Playwright, against `BlueTrackTest`. A full run has a known, pre-existing worker-contention flake on this dev host (see `Lessons_Learned.md`'s 2026-09-04 E2E entry) -- a handful of tests can fail together on a full run yet pass individually; that pattern is a resource-contention signature to double-check against, not automatically a regression.
- Direct SQL sanity checks after a real-environment rebuild: table count (`SELECT COUNT(*) FROM sys.tables`), a known seed table's row count (e.g. `dim_account_type`), and (once step 5 has run) `SELECT name FROM msdb.dbo.sysjobs WHERE name LIKE 'BlueTrack%'` to confirm exactly one correctly-named job exists with no stale leftover from a prior naming scheme.

## See Also

- `Database/README.md` -- full script manifest and folded-in history.
- `Database/Import_Load_Process_Guide.docx` -- ongoing operational cadence once an environment is running.
- `Design Documents/Design_Decision_Register.md`, D-106 -- the restructure this runbook documents, and the two follow-on fixes (hardcoded job/schedule names, script 14's DbUp incompatibility) found while first exercising it against a real environment.
- `Lessons_Learned.md` -- the BULK INSERT `ROWTERMINATOR` fix, the D-89 hardcoded-database-name incident, and the E2E worker-contention pattern referenced above.
