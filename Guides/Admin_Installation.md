# Admin Guide: Installation

**Blueprint Progress Tracking Web Interface**

## Who this is for

Whoever is standing up a brand-new BlueTrack environment for the first time. This is a short, linear walkthrough of the first-time setup path — for full field-by-field detail on any step, follow the links out to the reference documents in `Design Documents/`. Once installation is done, continue to [Admin_Configuration.md](Admin_Configuration.md).

## 1. Prerequisites

You need: the .NET 10 SDK, Node.js, SQL Server reachable from this host, and (for a real environment) IIS with the ASP.NET Core Hosting Bundle and the IIS URL Rewrite Module. `Deploy/Install-BlueTrack.ps1` checks for all of these and offers to install what's missing, except SQL Server itself — see `Deploy/README.md`'s "What it deliberately does NOT do" section.

## 2. Run the installer

From an elevated PowerShell session:

```powershell
.\Install-BlueTrack.ps1 `
    -Environment Test `
    -SqlServerInstance "SQLSERVER01" `
    -DatabaseName BlueTrackTest `
    -SiteName BlueTrackTest `
    -Hostname bluetrack-test.company.com `
    -GenerateSelfSignedCert
```

This one script builds the API and SPA, creates and migrates the database, provisions the IIS site, and smoke-tests the result. Add `-WhatIf` first if you want to preview what it will do without changing anything. See `Deploy/README.md` for the full option list and `Design Documents/Design_Deployment_Runbook.md` for the manual step-by-step this automates, if you ever need to run a step by hand.

## 3. First sign-in

A fresh install ships with `BUILTIN\Administrators` bootstrapped as the Admin role — sign in as a member of that local group using Windows Integrated authentication (no extra setup needed for this first sign-in). This is a deliberate "usable immediately" default, not something to leave in place for a real production environment.

## 4. Replace the bootstrap admin group

Once you're in, go to **Admin > Group / Role Mapping** and map a real AD/Entra group to the Admin role. Plan to remove the `BUILTIN\Administrators` bootstrap mapping once your real admin group is confirmed working — see `Design Documents/Design_User_Guide.md`'s Group / Role Mapping section for the exact steps.

## 5. Load real data

A fresh install's staging tables are empty. Follow `Design Documents/Design_Deployment_Runbook.md`'s "First Data Load" section to run `usp_Import_All` / `usp_RunFullLoad` once your CyberArk export files are in place, then (for a real, non-disposable environment) schedule the nightly Import+Load job as that runbook describes.

## 6. What's next

Installation gets you a running, reachable BlueTrack with Windows Integrated authentication and the default Windows DPAPI secrets backend. Before opening it up to regular users, continue to [Admin_Configuration.md](Admin_Configuration.md) to review identity providers, the secrets store backend, roles, notifications, and the other admin-wide settings that a real environment normally needs to change from their installed defaults.

## See also

- `Deploy/README.md` — the installer's full option reference.
- `Design Documents/Design_Deployment_Runbook.md` — the manual step-by-step procedure, and the environment-specific items a redeployment must not skip.
- `Design Documents/Design_Deployment_Methodology.md` — the reasoning behind the deployment approach.
