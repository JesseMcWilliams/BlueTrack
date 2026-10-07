# BlueTrack Installer

A PowerShell script (`Install-BlueTrack.ps1`) that performs a full deployment of BlueTrack from scratch: verifies/installs prerequisites, builds the API and SPA from source, creates and migrates the database, provisions the IIS site, and smoke-tests the result.

This exists because, before it, there was no automated deployment path at all — see `Claude_Docs/Design_Deployment-Methodology.md` and `User_Docs/Admin_DeploymentRunbook.md` for the manual procedure this automates (and extends: the IIS site/`web.config` this script creates didn't exist anywhere in the repo before it).

## What it does

The install runs as named steps, grouped into the five phases below. `-ListSteps` prints them with each one's saved status; see "Steps, resume and re-run" for running a subset.

1. **Prerequisites** — checks for the .NET 10 SDK, Node.js, the IIS role, the IIS Windows Authentication role service, the ASP.NET Core Hosting Bundle, and the IIS URL Rewrite Module; offers to install any that are missing (see "Prerequisite installers"). Each is its own step (`Prereq.<Name>`).
2. **Build** — `dotnet publish` (API) and `npm run build` (SPA) from source. The API publishes to `C:\inetpub\BlueTrack\<Environment>\api` by default (override with `-ApiInstallPath`) — deliberately not a temp folder, since IIS's Application Pool identity needs durable read access to whatever directory it's pointed at, and `%TEMP%` is both ephemeral and scoped to the account that ran this script.
3. **Database** — confirms SQL Server is reachable; if the target database already exists with a schema (an upgrade, not a fresh install), takes a pre-deployment backup first (see "Rollback" below); runs `App/Migrator` against `Database` (and optionally `Database/Test`); writes an environment-specific `appsettings.{Environment}.json` next to the published API; and optionally installs the nightly Import+Load SQL Agent job.
4. **IIS** — creates the Application Pool, the site (physical root = the built SPA), the nested `/BlueTrack` Application (physical root = the published API), the HTTPS binding/certificate, and the site-root `web.config` (the SPA client-side-routing fallback rule). The nested Application is named `BlueTrack`, not `api` — the controllers' own routes already start with a literal `api/` segment, and naming the Application `/api` too would double it up externally (D-163); the real API root ends up at `/BlueTrack/api/...`. Also enables IIS's own Windows Authentication (alongside Anonymous) on that Application — BlueTrack.Api defers Windows Integrated Auth to IIS's native handshake when IIS-hosted rather than running its own Negotiate handler, which cannot coexist with IIS/ANCM at all (D-162).
   Then, with your consent (`-GrantAppPoolSqlAccess`), the `Db.AppPoolAccess` step gives the App Pool's Windows account SQL Server access: `CREATE LOGIN` if it's missing, then `Database/41_BlueTrack_GrantAppServiceAccountAccess.sql` (read/write/execute, not `db_owner`). See "App pool SQL access" below.
5. **Smoke test** — calls the Deployment Info health-check endpoint and reports the result. A failure is explained by HTTP status; see "Smoke test failures" below.

## Rollback

`Claude_Docs/Design_Deployment-Methodology.md`'s rollback mechanism (Option B): formalize backup/restore rather than per-script "down" migrations.

- **`Backup-BlueTrack.ps1`** — backs up BlueTrack (and, optionally with `-IncludeMsdb`, `msdb`) to a timestamped `.bak`, verifies it with `RESTORE VERIFYONLY`, and writes a manifest (database name, timestamp, git commit) recording what it preceded. `Install-BlueTrack.ps1` calls this automatically before migrating an existing (non-fresh) database — pass `-SkipPreDeployBackup` to opt out.
- **`Restore-BlueTrack.ps1`** — restores BlueTrack (and, optionally with `-RestoreMsdbFilePath`, `msdb`) from a specific backup file. Destructive (`RESTORE DATABASE ... WITH REPLACE`) — requires typing `YES` to confirm, or `-Confirm:$false` for unattended use. Only ever handles the database side: also redeploy the application build matching the backup's manifest `GitCommit`, if one was recorded.
- **`msdb` is opt-in, not automatic**, in both directions: SQL Agent job definitions (the nightly Import+Load job) live in `msdb`, which is shared by every database on the SQL Server instance. Restoring it later is instance-wide, not scoped to just BlueTrack — only do it if a job definition was actually lost.
- **What this does not solve**: there are still no per-script "down" migrations for the numbered `Database/*.sql` files — full-database restore remains the only rollback path, which is what Option B always meant.

Every phase is idempotent where it makes sense: re-running the script detects what's already there and skips it, rather than failing or duplicating (pass `-Force` to recreate the IIS pieces instead). Every step also supports `-WhatIf` to preview what would happen without doing it. Before D-168, `-WhatIf` did not reach the `BlueTrack.*.psm1` functions when the script was run from a prompt, so a "preview" really built, migrated and created IIS objects; see `Claude_Docs/Reference_Lessons-Learned.md`.

## What it deliberately does NOT do

- **Does not install SQL Server.** Verifying SQL Server connectivity is in scope; standing up the Database Engine itself is an edition/licensing decision this script won't make unattended. Install SQL Server first, then run this.
- **Does not configure a real identity provider** (SAML/OIDC) — Windows Integrated authentication works out of the box; a real IdP needs real tenant metadata entered afterward via the Identity Providers admin page.
- **Does not cut over the Secrets Store backend** — Windows DPAPI is active by default after install; switching to CyberArk CP/CCP/Conjur, Azure Key Vault, or AWS Secrets Manager happens afterward via the Secrets Store Configuration admin page.
- **Does not load real CyberArk export data** — a fresh install has empty staging tables; see `User_Docs/Admin_DeploymentRunbook.md`'s "First Data Load" section to run `usp_Import_All`/`usp_RunFullLoad` once real export files are in place.
- **Does not add per-script "down" migrations.** The rollback mechanism (see above) is full-database backup/restore, not an "undo this specific script" path — that's a deliberate scope boundary, not an oversight.
- **Is not a general upgrade tool for an existing production install** — it's built for a fresh (or fresh-ish) environment. Re-running it is safe, but it isn't a patching/upgrade pipeline.

## Usage

Run from an elevated PowerShell session, from the `Deploy` folder (the script lives there, not at the repo root):

```powershell
.\Install-BlueTrack.ps1 `
    -Environment Test `
    -SqlServerInstance "SQLSERVER01" `
    -DatabaseName BlueTrackTest `
    -SiteName BlueTrackTest `
    -Hostname bluetrack-test.company.com `
    -GenerateSelfSignedCert
```

Or unattended, via a config file. Start from `answers.sample.json`: copy it to `answers.<env>.json` (git-ignored) and edit it. Leave a setting out to be prompted for it; `SqlCredential` is always prompted and never saved.

```powershell
.\Install-BlueTrack.ps1 -ConfigFile .\answers.prod.json
```

Preview without changing anything:

```powershell
.\Install-BlueTrack.ps1 -ConfigFile .\answers.prod.json -WhatIf
```

### Parameters

| Parameter | Purpose | Default |
|---|---|---|
| `-ConfigFile` | JSON file supplying any of the parameters below, for unattended runs. Explicit command-line parameters win over the file. | none |
| `-Environment` | `Development` / `Test` / `Staging` / `Production` | prompted |
| `-SqlServerInstance` | SQL Server instance to connect to | prompted |
| `-DatabaseName` | Target database name | `BlueTrack` |
| `-UseWindowsAuth` | Connect via Windows Integrated Security | `$true` |
| `-SqlCredential` | SQL login (only used when `-UseWindowsAuth $false`) | prompted via `Get-Credential` |
| `-SeedTestData` | Also run `Database/Test` (DevFakeAuth/synthetic accounts) — never for Production | prompted |
| `-InstallNightlyJob` | Install the nightly Import+Load SQL Agent job | prompted |
| `-GrantAppPoolSqlAccess` | Give the App Pool's account a SQL login and the script 41 grants (`Db.AppPoolAccess` step). Only asked with Windows Integrated Security | prompted |
| `-AppPoolSqlLogin` | Account to grant, overriding the one worked out from the App Pool (e.g. on a server that isn't domain-joined) | worked out |
| `-ExportFolderPath` / `-EvdDatabaseName` | Required if `-InstallNightlyJob` — see below | prompted |
| `-SiteName` | IIS site name | `BlueTrack` |
| `-Hostname` | Site host header / binding hostname | prompted |
| `-HttpPort` / `-HttpsPort` | IIS bindings | `80` / `443` |
| `-CertificateThumbprint` | Existing certificate to bind (`Cert:\LocalMachine\My`) | prompted; generates a self-signed cert if left blank |
| `-GenerateSelfSignedCert` | Generate a self-signed certificate instead of using an existing one — **Dev/Test only** | off |
| `-Force` | Recreate IIS Application Pool/Site/Application even if they already exist | off |
| `-SkipPrerequisiteInstall` | Report missing prerequisites but don't offer to install them | off |
| `-SkipSmokeTest` | Skip the post-install health-check call | off |
| `-BackupFolder` | Where the automatic pre-deployment backup is written, when the target database already has an existing schema | prompted only if needed |
| `-SkipPreDeployBackup` | Skip the automatic pre-deployment backup entirely | off |
| `-WhatIf` | Preview every action without performing it (standard PowerShell `SupportsShouldProcess`) | off |
| `-ListSteps` | List the steps and their saved status, then exit | off |
| `-Step` | Run only these steps (wildcards allowed, e.g. `Prereq.*`), whatever their saved status | all |
| `-StartAt` | Run this step and every step after it | none |
| `-Resume` | Run every step not yet `Completed`/`NotApplicable`, with the saved answers | off |
| `-PrerequisiteSource` | `Auto` / `Offline` / `Winget` / `Download` — see "Prerequisite installers" | `Auto` |
| `-InstallerSourcePath` | Folder of pre-downloaded installers, for servers without internet access | none |

### Steps, resume and re-run

Answers and step outcomes are saved under `Deploy/State/` (git-ignored), one pair of files per `-SiteName`:

- `answers.<SiteName>.json` — every answer given so far, written before the first step runs and again whenever a step adds one (the backup folder, a generated certificate's thumbprint). It has the same shape as `answers.sample.json`, so it also works as a `-ConfigFile`. It never holds a credential.
- `progress.<SiteName>.json` — each step's last status: `Completed`, `Failed` (with the error), `Declined` (a prerequisite you chose not to install) or `NotApplicable` (its setting was off, e.g. `InstallNightlyJob`).

```powershell
.\Install-BlueTrack.ps1 -ListSteps                       # where did it get to?
.\Install-BlueTrack.ps1 -Resume                          # carry on from the failed step
.\Install-BlueTrack.ps1 -Step Prereq.HostingBundle       # (re)install one prerequisite, no y/N prompt
.\Install-BlueTrack.ps1 -StartAt Iis.AppPool             # redo the IIS phase onward
```

`-Resume`, `-Step` and `-StartAt` load the saved answers, so they don't prompt again. If `Deploy/State/` holds only one site, it's picked automatically; otherwise pass `-SiteName`. Precedence: command line, then `-ConfigFile`, then saved answers. A plain run (none of the three) starts a fresh progress record. The script only prompts for what the selected steps need: `-Step Prereq.*` asks for nothing.

### Prerequisite installers

`-PrerequisiteSource Auto` (the default) uses the first that applies:

1. **Offline** — when `-InstallerSourcePath` is given. For servers without internet access: download the installers on another machine and copy them into one folder.
2. **winget** — when it's on `PATH` (the .NET SDK and Node.js only).
3. **Download** — from the vendor's own release metadata: Microsoft's `release-metadata/10.0/releases.json` for the .NET SDK and Hosting Bundle, `nodejs.org/dist/index.json` for Node.js LTS, and Microsoft's fixed URL Rewrite 2.1 link. The file is checked against the SHA-512/SHA-256 hash the vendor publishes (URL Rewrite has none).

Every installer, however it was obtained, must carry a valid Authenticode signature before it runs, and its exit code is checked (3010 = success, reboot needed). After an install, the script re-runs the check and fails the step if it still doesn't pass.

#### Offline prerequisites

File names the offline lookup matches in `-InstallerSourcePath` (the newest version wins if there are several):

| Prerequisite | File name pattern | Where to get it |
|---|---|---|
| .NET 10 SDK | `dotnet-sdk-10.*-win-x64.exe` | https://dotnet.microsoft.com/download/dotnet/10.0 |
| ASP.NET Core Hosting Bundle | `dotnet-hosting-10.*-win.exe` | same page, "Hosting Bundle" |
| Node.js LTS | `node-v*-x64.msi` | https://nodejs.org/dist/ |
| IIS URL Rewrite 2.1 | `rewrite_amd64*.msi` | https://www.iis.net/downloads/microsoft/url-rewrite |

To have the hash checked as well, put a sidecar file next to the installer, `<installer>.sha512` or `<installer>.sha256`, holding the hash (a bare hash or `sha256sum`-style `<hash>  <file>` line). The IIS role and its Windows Authentication role service are Windows features (`Install-WindowsFeature`) and need no installer file.

### App pool SQL access

With Windows Integrated Security, the API connects to SQL Server as the App Pool's Windows account, and nothing works until that account has a login (D-164, D-170). The account is:

- **`ApplicationPoolIdentity`** (the installer's default), `NetworkService` or `LocalSystem`: the **computer account**, `DOMAIN\HOSTNAME$`. This holds even when SQL Server is on the same machine, confirmed live (`Login failed for user 'SAIA\DCACYBSQL01$'`); it is not `IIS APPPOOL\<pool>`.
- **A custom identity** (a gMSA or service account): that account.

`Db.AppPoolAccess` works this out, creates the login if it's missing and runs script 41 for it. The installing user needs `securityadmin` (or `sysadmin`). Granting the computer account also gives that access to anything else on the server running as `NETWORK SERVICE`, `SYSTEM` or another App Pool; a dedicated gMSA avoids that (`User_Docs/Admin_Installation.md`, section 3).

### Smoke test failures

| Result | Meaning | What to do |
|---|---|---|
| `401` | IIS rejected the Windows login; BlueTrack wasn't reached. Seen live as `401.1` / `0x8009030e`. | Run `klist purge`, then `-Step Smoke` (that cleared it live; a stale Kerberos ticket is the likely, unconfirmed cause). If it persists with a custom App Pool identity, check SPNs and `useAppPoolCredentials` (see the install guide's gMSA section). |
| `403` | Signed in, but without `ViewDeploymentInfo`. | Run the installer elevated as a local administrator (the bootstrap Admin role is mapped to `BUILTIN\Administrators`), or map your group to Admin. |
| `500` | BlueTrack.Api failed; the exception is in the Application event log (`.NET Runtime`, event 1000). | The usual cause is a missing SQL login for the App Pool's account. The smoke test prints SQL Server's matching `Login failed` event if there is one; run `-Step Db.AppPoolAccess -GrantAppPoolSqlAccess $true`. |
| No response | DNS, binding or certificate. | Check the hostname resolves here, the HTTPS binding exists, and this machine trusts the certificate. |

### A note on the nightly Import+Load job

`Database/14_BlueTrack_ScheduleImportLoadJob.sql` has its export-folder path and Self-Hosted EVD database name hardcoded as literal T-SQL — they aren't `sqlcmd` variables the way the target database name is. `Install-BlueTrackNightlyJob` (in `Modules/BlueTrack.Database.psm1`) generates a **temporary copy** of that script with your `-ExportFolderPath`/`-EvdDatabaseName` substituted in before running it via `sqlcmd` — the tracked file in `Database/` is never modified.

## Structure

```
Deploy/
  Install-BlueTrack.ps1           # entry point
  answers.sample.json             # starting point for a -ConfigFile answer file
  Modules/
    BlueTrack.Prereqs.psm1        # prerequisite verification/auto-install (offline/winget/download)
    BlueTrack.State.psm1          # saved answers + step progress (Deploy/State/)
    BlueTrack.Build.psm1          # dotnet publish + npm run build
    BlueTrack.Database.psm1       # SQL connectivity, App/Migrator, appsettings, nightly job
    BlueTrack.Rollback.psm1       # backup/restore rollback mechanism
    BlueTrack.Iis.psm1            # app pool, site, /BlueTrack Application, web.config, bindings
    BlueTrack.Smoke.psm1          # post-install health-check call
  Backup-BlueTrack.ps1            # standalone backup, outside a full install run
  Restore-BlueTrack.ps1           # standalone emergency restore
  Templates/
    site-web.config.template      # SPA URL Rewrite fallback rule
  Logs/                           # transcript logs from each run (git-ignored)
  State/                          # answers.<SiteName>.json + progress.<SiteName>.json (git-ignored)
```

## Known limitation

This has been reviewed and syntax-checked, and dry-run (`-WhatIf`) logic has been traced against an already-running BlueTrack instance to confirm the idempotency checks behave correctly — but it has **not yet been run end-to-end against a genuinely blank server**. Do that once, on a disposable VM, before trusting it for a real environment.
