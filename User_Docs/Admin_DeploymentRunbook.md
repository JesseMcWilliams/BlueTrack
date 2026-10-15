# Deployment Runbook

**Blueprint Progress Tracking**

## Purpose & Scope

The step-by-step procedure for standing up a brand-new BlueTrack environment (or fully rebuilding one), upgrading an existing one, and the checklist of environment-specific items a full redeployment must not skip. Complements, rather than duplicates, three other documents:

- `Admin_Installation.md`: the installer walkthrough (`Deploy/Install-BlueTrack.ps1`), which automates most of the steps below.
- `Database/README.md`: the script manifest (what each numbered file does, and the order they run in).
- `Database/Import_Load_Process_Guide.docx`: the day-to-day operational cadence for Import/Load once an environment is already running.

Those answer "what does each piece do"; this one answers "in what order do I run everything, and what must I remember to change for *this* environment."

## Step-by-Step: New or Fully Rebuilt Environment

The installer runs steps 1-3 for you (`Db.Migrate`, and `Db.SeedTestData` when you ask for test data) and, if you choose them, the nightly job and App Pool grants from step 5 (`Db.NightlyJob`, `Db.AppPoolAccess`). Step 4 and the other manual scripts are always yours to run. The manual equivalents, if you ever need to run one by hand:

1. **Create the database.** Either run `Database/Manual/01_BlueTrack_CreateDatabase.sql` by hand against `master` (replace the `$DatabaseName$` placeholder first; it's never substituted, since this file never runs through the Migrator), or just proceed to step 2: `App/Migrator` creates the database automatically if it doesn't exist.
2. **Run the schema/seed sequence.**
   ```
   dotnet run --project App/Migrator -- "<connection string>" "Database"
   ```
   Runs the scripts at the top level of `Database/` in file-name order: the six baseline scripts (`01`-`06`), then each later numbered script (`07`, `08`, ...). Each script runs once; the database records which have run. The scripts in `Database/Manual/` never run this way (see step 5).
3. **Test environments only:**
   ```
   dotnet run --project App/Migrator -- "<connection string>" "Database/Test"
   ```
   Seeds test roles and synthetic accounts. Never run against a real environment.
4. **Load real data**: see "First Data Load" below.
5. **Run the manual scripts that apply**, from `Database/Manual/`, numbered in the order they're run (`Database/README.md`, "Manual scripts", has the full list):
   - `02_BlueTrack_GrantAppServiceAccountAccess.sql`: the App Pool account's database permissions (the installer's `Db.AppPoolAccess` step; `Admin_Installation.md`, section 3).
   - `03_BlueTrack_GrantBackupStatusReaderRole.sql`: optional, lets the Deployment page read SQL Server's backup history.
   - `04_BlueTrack_ScheduleImportLoadJob.sql`: real (non-disposable) environments only, once step 4 has worked by hand at least once. Creates the nightly 2:00 AM Import+Load SQL Agent job, one step that runs `usp_RunNightlyImportLoad` (the Import, the Load if the Import succeeded, then the daily snapshot for Reports > Trends whatever happened):
     ```
     sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql
     ```
     Run it on a copy in which you've replaced the four tokens: `__IMPORT_PC__` and `__IMPORT_SH__` (`1` or `0` for Privilege Cloud and Self-Hosted; at least one must be `1`), `__EXPORT_FOLDER__` (the Privilege Cloud export folder) and `__EVD_DB__` (the Self-Hosted EVD database). The job reads its source settings from **Admin > Data Sources** ("CyberArk sources") each night; the script saves your values there only if nothing has been saved yet, and otherwise keeps the saved settings and prints a WARNING if they differ. The installer's `Db.NightlyJob` step fills the tokens for you from `-ImportSources`, `-ExportFolderPath` and `-EvdDatabaseName`, on a temporary copy, without changing the file.
   - `05_BlueTrack_ScheduleAuditLogPurgeJob.sql`: real environments, once a retention period is set on Global Application Configuration. Same `sqlcmd` form.

   These scripts are run by hand (or by the installer), never through `App/Migrator`. The SQL Agent job and backup-status scripts switch to `msdb`, which would break the Migrator's own record-keeping.

## First Data Load

Staging tables are empty after a fresh install; nothing populates them until you run the import procedures. `Database/Import_Load_Process_Guide.docx` is the authoritative day-to-day reference; the shape of a first run is:

```sql
EXEC usp_Import_All
    @EVDDatabaseName = '<EVD database>',
    @PlatformsFile = '<export folder>\Export_PlatformsList.csv',
    @UsersFile = '<export folder>\Export_UsersList.csv',
    @GroupsFile = '<export folder>\Export_GroupsList.csv',
    @GroupMembersFile = '<export folder>\Export Local Group Members <date>.csv',
    @SafesFile = '<export folder>\Export_SafesList.csv',
    @AccountsFile = '<export folder>\Export_AccountsList.csv',
    @EntitlementsFile = '<export folder>\Export Entitlements <date>.csv',
    @EntitlementsExportDate = '<date>';
GO
EXEC usp_RunFullLoad;
```

- **One source only**: `usp_Import_All` imports both Privilege Cloud and Self-Hosted by default. For a Privilege-Cloud-only implementation add `@ImportSelfHosted = 0` (and leave out `@EVDDatabaseName`); for a Self-Hosted-only one add `@ImportPrivilegeCloud = 0` (and leave out the file parameters). The nightly job follows the **Import Privilege Cloud** and **Import Self-Hosted** choices on **Admin > Data Sources** ("CyberArk sources"); the installer's `-ImportSources` answer sets them on first install.
- **Date-stamped files**: use the actual date stamp on the two date-stamped export files on disk (`Export Entitlements <date>.csv`, `Export Local Group Members <date>.csv`), not today's date. Only the nightly job fills in the current date, from the `{yyyy-MM-dd}`-style date tokens in the file names set on Admin > Data Sources.
- **Self-Hosted**: the EVD database must be on the *same* SQL Server instance (a cross-database query, not a linked server). Confirm it exists and is `ONLINE` first.
- **Privilege Cloud**: the files are read with BULK INSERT, so the SQL Server *service account*, not your own login, needs read access to the export folder.

## Upgrading an Existing Environment

`Deploy/Update-BlueTrack.ps1` downloads a new version from GitHub and offers to redeploy this site from it with the site's saved installer answers. It needs only PowerShell and HTTPS access to `github.com`; git isn't needed on the server. Full detail: `Deploy/README.md`, "Updating to a new version".

1. **Run it from an elevated (Run as Administrator) PowerShell session**, in the current copy's `Deploy` folder. It refuses to run otherwise, since the scripts it downloads are later run elevated. It doesn't change the download folder's permissions, but warns if someone other than Administrators, SYSTEM or TrustedInstaller can change it; add `-LockDownFolder` to limit it to Administrators and SYSTEM (use a folder of its own, not the backup folder: this resets the permissions of everything inside).
2. **Choose what to install**:
   ```powershell
   .\Update-BlueTrack.ps1                                   # the latest release
   .\Update-BlueTrack.ps1 -Release v1.2.0                   # a specific release
   .\Update-BlueTrack.ps1 -Branch main -AllowUnsignedBranch # a branch, for testing only
   .\Update-BlueTrack.ps1 -ListBranches -AllowUnsignedBranch
   ```
   **Only signed releases are installed**: a release's tag must be an annotated tag whose signature GitHub verifies, or the script refuses it. A branch is never a signed release, so it needs `-AllowUnsignedBranch`. Use that for testing only, not for production.
3. **Download folder**: the first time, pass `-DownloadFolder <folder>` or answer the prompt; it's saved with the site's answers (`UpdateDownloadFolder`). Each version goes into its own folder there, e.g. `BlueTrack-v1.2.0`, and earlier versions stay for rollback; delete old ones yourself.
4. **Redeploy**: the script copies the site's saved answers into the new copy and asks whether to run its installer (`-RunInstaller` skips the question). That's a full install run, including the pre-deployment backup, the database migration and the smoke test.
5. **Afterwards**, use the new copy's `Deploy` folder for every later run (`-Step`, `-Resume`, the next update): it now holds the site's answers and progress.

**Before upgrading**, check the server's ASP.NET Core Hosting Bundle is **10.0.13 or later** (`dotnet --list-runtimes` lists `Microsoft.AspNetCore.App 10.0.13` or higher), and install the current one from Microsoft's .NET download page if not. The installer's prerequisite check only confirms a Hosting Bundle is installed, not its version.

**After upgrading:**

- **Database changes apply themselves.** The installer's `Db.Migrate` step runs any new numbered scripts in `Database/` (for example, the edit-lock timeout default and the default sign-in method setting). An existing install keeps its current lock timeout and signs people in exactly as before; nothing extra is needed.
- **A site built before the six-script baseline** upgrades the same way: the Migrator recognizes the older scripts and records the baseline as already applied, without running it or touching your data. It refuses a database that isn't up to date with the older scripts (through `51_BlueTrack_TargetsFromCyberArkAddress.sql`); upgrade that one from the `pre-sql-baseline` tag first, then again from the current version.
- **Manual scripts were renumbered** into `Database/Manual/`, `01`-`06`, in the order they're run (for example, the nightly job is now `Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql`). Update any notes or scheduled commands of your own that name the old files. Jobs already created in SQL Agent keep working.
- **Re-run `Db.NightlyJob` once** after the upgrade that adds the CyberArk sources settings (`Database/12_BlueTrack_CyberArkSourceConfig.sql`) and the one-step nightly job (`Database/21_BlueTrack_SnapshotAndSiteUrl.sql`): `.\Install-BlueTrack.ps1 -Step Db.NightlyJob`. The old job keeps working with the values written into it, but changes on Admin > Data Sources have no effect until the job is recreated, and it takes the Trends snapshot only on nights the Import and Load succeed. The step uses your saved answers to save the settings and recreates the job as one step.
- **The settings file is now `appsettings.Site.json`.** The installer's `Db.AppSettings` step writes it and removes the old `appsettings.<Environment>.json`, which BlueTrack read only on a Production install; until this upgrade, a Test or Staging site ignored the installer's connection string, HSTS and allowed folders. If an old `appsettings.Production.json` holds settings the installer didn't write, it's kept with a warning naming them: move them into `appsettings.Site.json`, then delete the old file. Afterwards **Admin > Deployment** shows the environment you chose in the installer, and a site with a trusted certificate starts sending HSTS. Details: `Admin_SettingsReference.md`, "Settings file".
- **Site URL:** the installer's `Db.SiteUrl` step fills in the Site URL on Global Application Configuration from `-Hostname` and `-HttpsPort` if it's blank, so notification emails link to pages. Check it there.
- **Check the version** on **Admin > Deployment**: a release shows its plain number.
- **Re-run `Db.AppPoolAccess`** to pick up changed database permissions for the app's account: `.\Install-BlueTrack.ps1 -Step Db.AppPoolAccess -GrantAppPoolSqlAccess $true` (`Admin_Installation.md`, section 3).
- **Reload open browser tabs** (Ctrl+F5). A page left open from before the upgrade may still call the API at its old address and fail.

### Making a signed release

For whoever publishes BlueTrack releases, not the server administrator. Register a signing key on GitHub (your account's SSH and GPG keys settings; for an SSH key choose the *Signing Key* type), and tell git to use it: `git config --global gpg.format ssh` and `git config --global user.signingkey <path to the .pub>` for SSH, or `git config --global user.signingkey <GPG key id>` for GPG.

Then make the release with `Tools/New-BlueTrackRelease.ps1`, in your clone of the repository (not on the server). It needs git, Python 3 with `python-docx` and, for `-Push`, the GitHub CLI signed in. It takes two runs, because `main` changes only through a pull request:

1. **Prepare**, on an up-to-date, clean `main`:
   ```powershell
   .\Tools\New-BlueTrackRelease.ps1 -Version 1.2.0 -Push
   ```
   It checks the version is above the latest release and that the Unreleased section of `User_Docs/Release_Notes.md` lists something. On a new branch it sets `<Version>` in `App/Api/BlueTrack.Api.csproj`, turns Unreleased into that version's section, rebuilds `Published_Docs/`, and commits. `-Push` pushes the branch and opens the pull request; without it, the script prints the commands. Add `-WhatIf` to see what it would do.
2. **Merge** the pull request.
3. **Tag**:
   ```powershell
   .\Tools\New-BlueTrackRelease.ps1 -Version 1.2.0 -Tag -Push
   ```
   It signs the tag `v1.2.0` on the merged commit, pushes it, creates the GitHub release with that version's notes, and checks that GitHub verified the signature, which `Update-BlueTrack.ps1` requires.

Don't change `<Version>` by hand. Between releases, the build adds where it came from to the version (for example `1.2.0-dev.12+abc1234`), so About and the Deployment page never show a test build as a release. Full detail: `Tools/README.md`, "Making a release". Confirm the GitHub settings wording when you first set up the signing key.

## Environment-Specific Items a Redeployment Must Not Skip

None of these are worked out from the connection string the way the database name is. Someone has to set each one deliberately for a *new* environment:

- **The CyberArk sources** (which sources to import, the Privilege Cloud export folder and file names, the Self-Hosted EVD database): set on **Admin > Data Sources** ("CyberArk sources"). On a new environment the installer's `-ImportSources`, `-ExportFolderPath` and `-EvdDatabaseName` answers seed them when it creates the nightly job; if you create the job by hand, replace the four tokens in a copy of `Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql` first. Check them on the page with **Test**.
- **The bootstrap admin group** (`BUILTIN\Administrators`, seeded by `05_BlueTrack_Baseline_WebSeed.sql`): a deliberate default so a fresh install is usable immediately. Map a real AD/Entra group to the Admin role on **Admin > Group / Role Mapping**, then remove the bootstrap mapping; don't leave every local administrator as a permanent BlueTrack Admin in production.
- **OIDC/SAML identity providers** (`05_BlueTrack_Baseline_WebSeed.sql`): seeded disabled, with placeholder settings only. Enter the real IdP tenant/metadata on the Identity Providers admin page, then enable them, before either is usable.
- **Secrets Store backend** (seeded by `04_BlueTrack_Baseline_WebSchema.sql`): Windows DPAPI is active by default. A switch to CyberArk CP/CCP/Conjur, Azure Key Vault or AWS Secrets Manager happens on the Secrets Store Configuration admin page, not by editing the script. CyberArk CP also needs the CyberArk Application Password SDK on the server (`Admin_Installation.md`, section 1).
- **Certificate, backup folder and data feed folders**: see `Admin_Installation.md` ("Certificates", "Backup folder access", "Data feed folders").
- **Schema changes on a real environment** go in new, guarded, numbered scripts after the last one in `Database/`, never as edits to a script that has already run (`Database/README.md`, "Adding a script").

## Verification

After any full rebuild or upgrade:

- **Smoke test**: the installer's last step checks the API's health check (`/BlueTrack/api/admin/deployment`) and that the site root serves the page. Re-run it any time with `.\Install-BlueTrack.ps1 -Step Smoke`; `Deploy/README.md`, "Smoke test failures", explains each failure.
- **Deployment page**: in BlueTrack, the Deployment page's health checks should all be healthy.
- **Direct SQL checks**: table count (`SELECT COUNT(*) FROM sys.tables`), a known seed table's row count (e.g. `dim_account_type`), and (once the nightly job exists) `SELECT name FROM msdb.dbo.sysjobs WHERE name LIKE 'BlueTrack%'` to confirm exactly one correctly-named job (`BlueTrack (<DatabaseName>) - Import and Load`) with no stale leftover from an earlier naming scheme.
- **Schema drift check (optional)**: to confirm the live database matches the scripts, build a reference database from them on the same SQL Server and compare, from the `Deploy` folder:
  ```powershell
  dotnet run --project ..\App\Migrator -- "Server=<server>;Database=BlueTrackReference;Integrated Security=true;TrustServerCertificate=true" ..\Database
  .\Compare-BlueTrackSchema.ps1 -SqlServerInstance <server> -ReferenceDatabase BlueTrackReference -DifferenceDatabase BlueTrack -IgnoreComments
  ```
  It lists every schema difference (tables, columns, constraints, indexes, procedures, views, roles and their permissions; not users or logins) and exits with code 1 when anything differs. Drop `BlueTrackReference` afterwards.

## See Also

- `Admin_Installation.md`: the installer walkthrough.
- `Deploy/README.md`: the installer's and update script's full option reference.
- `Database/README.md`: full script manifest, manual scripts and the drift check.
- `Database/Import_Load_Process_Guide.docx`: ongoing operational cadence once an environment is running.
