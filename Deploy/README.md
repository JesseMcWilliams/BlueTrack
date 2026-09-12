# BlueTrack Installer

A PowerShell script (`Install-BlueTrack.ps1`) that performs a full deployment of BlueTrack from scratch: verifies/installs prerequisites, builds the API and SPA from source, creates and migrates the database, provisions the IIS site, and smoke-tests the result.

This exists because, before it, there was no automated deployment path at all — see `Design Documents/Design_Deployment_Methodology.md` and `Design Documents/Design_Deployment_Runbook.md` for the manual procedure this automates (and extends: the IIS site/`web.config` this script creates didn't exist anywhere in the repo before it).

## What it does

1. **Prerequisites** — checks for the .NET 10 SDK, Node.js, the IIS role, the ASP.NET Core Hosting Bundle, and the IIS URL Rewrite Module; offers to install any that are missing.
2. **Build** — `dotnet publish` (API) and `npm run build` (SPA) from source.
3. **Database** — confirms SQL Server is reachable, runs `App/Migrator` against `Database` (and optionally `Database/Test`), writes an environment-specific `appsettings.{Environment}.json` next to the published API, and optionally installs the nightly Import+Load SQL Agent job.
4. **IIS** — creates the Application Pool, the site (physical root = the built SPA), the nested `/api` Application (physical root = the published API), the HTTPS binding/certificate, and the site-root `web.config` (the SPA client-side-routing fallback rule).
5. **Smoke test** — calls the Deployment Info health-check endpoint and reports the result.

Every phase is idempotent where it makes sense: re-running the script detects what's already there and skips it, rather than failing or duplicating (pass `-Force` to recreate the IIS pieces instead). Every step also supports `-WhatIf` to preview what would happen without doing it.

## What it deliberately does NOT do

- **Does not install SQL Server.** Verifying SQL Server connectivity is in scope; standing up the Database Engine itself is an edition/licensing decision this script won't make unattended. Install SQL Server first, then run this.
- **Does not configure a real identity provider** (SAML/OIDC) — Windows Integrated authentication works out of the box; a real IdP needs real tenant metadata entered afterward via the Identity Providers admin page.
- **Does not cut over the Secrets Store backend** — Windows DPAPI is active by default after install; switching to CyberArk CP/CCP/Conjur, Azure Key Vault, or AWS Secrets Manager happens afterward via the Secrets Store Configuration admin page.
- **Does not load real CyberArk export data** — a fresh install has empty staging tables; see `Design Documents/Design_Deployment_Runbook.md`'s "First Data Load" section to run `usp_Import_All`/`usp_RunFullLoad` once real export files are in place.
- **Does not add a rollback mechanism.** There's no automated "undo this deploy" path here or anywhere else in the project yet (an open question in `Design_Deployment_Methodology.md`) — back up first.
- **Is not a general upgrade tool for an existing production install** — it's built for a fresh (or fresh-ish) environment. Re-running it is safe, but it isn't a patching/upgrade pipeline.

## Usage

Run from an elevated PowerShell session:

```powershell
.\Install-BlueTrack.ps1 `
    -Environment Test `
    -SqlServerInstance "SQLSERVER01" `
    -DatabaseName BlueTrackTest `
    -SiteName BlueTrackTest `
    -Hostname bluetrack-test.company.com `
    -GenerateSelfSignedCert
```

Or unattended, via a config file:

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
| `-ExportFolderPath` / `-EvdDatabaseName` | Required if `-InstallNightlyJob` — see below | prompted |
| `-SiteName` | IIS site name | `BlueTrack` |
| `-Hostname` | Site host header / binding hostname | prompted |
| `-HttpPort` / `-HttpsPort` | IIS bindings | `80` / `443` |
| `-CertificateThumbprint` | Existing certificate to bind (`Cert:\LocalMachine\My`) | prompted; generates a self-signed cert if left blank |
| `-GenerateSelfSignedCert` | Generate a self-signed certificate instead of using an existing one — **Dev/Test only** | off |
| `-Force` | Recreate IIS Application Pool/Site/Application even if they already exist | off |
| `-SkipPrerequisiteInstall` | Report missing prerequisites but don't offer to install them | off |
| `-SkipSmokeTest` | Skip the post-install health-check call | off |
| `-WhatIf` | Preview every action without performing it (standard PowerShell `SupportsShouldProcess`) | off |

### A note on the nightly Import+Load job

`Database/14_BlueTrack_ScheduleImportLoadJob.sql` has its export-folder path and Self-Hosted EVD database name hardcoded as literal T-SQL — they aren't `sqlcmd` variables the way the target database name is. `Install-BlueTrackNightlyJob` (in `Modules/BlueTrack.Database.psm1`) generates a **temporary copy** of that script with your `-ExportFolderPath`/`-EvdDatabaseName` substituted in before running it via `sqlcmd` — the tracked file in `Database/` is never modified.

## Structure

```
Deploy/
  Install-BlueTrack.ps1           # entry point
  Modules/
    BlueTrack.Prereqs.psm1        # prerequisite verification/auto-install
    BlueTrack.Build.psm1          # dotnet publish + npm run build
    BlueTrack.Database.psm1       # SQL connectivity, App/Migrator, appsettings, nightly job
    BlueTrack.Iis.psm1            # app pool, site, /api Application, web.config, bindings
    BlueTrack.Smoke.psm1          # post-install health-check call
  Templates/
    site-web.config.template      # SPA URL Rewrite fallback rule
  Logs/                           # transcript logs from each run (git-ignored)
```

## Known limitation

This has been reviewed and syntax-checked, and dry-run (`-WhatIf`) logic has been traced against an already-running BlueTrack instance to confirm the idempotency checks behave correctly — but it has **not yet been run end-to-end against a genuinely blank server**. Do that once, on a disposable VM, before trusting it for a real environment.
