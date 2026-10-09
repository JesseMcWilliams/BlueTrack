# Admin Guide: Installation

**Blueprint Progress Tracking Web Interface**

## Who this is for

Whoever is standing up a brand-new BlueTrack environment for the first time. This is a short, linear walkthrough of the first-time setup path — for full field-by-field detail on any step, follow the links out to the reference documents in `Claude_Docs/`. Once installation is done, continue to [Admin_Configuration.md](Admin_Configuration.md).

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

This one script builds the API and SPA, creates and migrates the database, provisions the IIS site, and smoke-tests the result. Add `-WhatIf` first if you want to preview what it will do without changing anything. See `Deploy/README.md` for the full option list and `Admin_DeploymentRunbook.md` for the manual step-by-step this automates, if you ever need to run a step by hand.

## 3. Set up a dedicated service account for the App Pool identity (recommended)

`Install-BlueTrack.ps1` creates the App Pool using IIS's default `ApplicationPoolIdentity` virtual account, with no parameter yet to specify a different identity — switching it is a manual follow-up step. Do this before real users start relying on the environment, since it affects how the app authenticates to SQL Server.

**What `ApplicationPoolIdentity` means for SQL Server (D-164, D-171)**: with SQL Server on the same machine (the normal single-server layout), the App Pool reaches it as its own virtual account, `IIS APPPOOL\<pool>` (for example `IIS APPPOOL\BlueTrack-AppPool`); with SQL Server on another machine, as the server's computer account, `DOMAIN\HOSTNAME$`. The installer's `Db.AppPoolAccess` step works out which, and grants that account its SQL login and permissions (see `Deploy/README.md`, "App pool SQL access"). Watch for one trap: with SQL Server on the same machine, the `Login failed for user '...'` message names the *computer account*, although the login that's needed is `IIS APPPOOL\<pool>`. That misleading name is what D-164 hit: it granted the computer account, the error continued, and it concluded `ApplicationPoolIdentity` was unreliable. It works once `IIS APPPOOL\<pool>` has the login.

**Why a dedicated account is still recommended**: least privilege and clarity. A gMSA's database access belongs to BlueTrack alone, it is the same account whether SQL Server is local or remote, and SQL Server's error messages name it correctly. (A computer-account grant, needed for a remote SQL Server with `ApplicationPoolIdentity`, also covers everything else on the server running as `NETWORK SERVICE`, `SYSTEM` or another App Pool.) A dedicated domain service account, or a **gMSA** (Group Managed Service Account — the standard modern replacement for a manually-managed service account password), is the recommended setup.

### Setting up a gMSA

1. **Confirm the domain supports it**: `(Get-ADDomain).DomainMode` needs to be at least a Windows Server 2012-level domain (gMSA didn't exist before that).
2. **Create the KDS Root Key, once per domain** (needs Domain Admin rights; check first with `Get-KdsRootKey` — skip this step if one already exists):
   ```powershell
   Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))
   ```
   The `-10` hours backdating makes it usable immediately, for a lab/test domain. Microsoft's own default (no backdating) waits ~10 hours before the key is usable, to give AD replication time to converge safely across every DC in a real multi-DC domain — don't skip that safety margin on a real production domain.
3. **Create the gMSA** (Domain Admin, or a delegated OU):
   ```powershell
   New-ADServiceAccount -Name BlueTrackSvc -DNSHostName bluetrack.company.com `
       -PrincipalsAllowedToRetrieveManagedPassword "HOSTNAME$"
   ```
   `-PrincipalsAllowedToRetrieveManagedPassword` needs the computer's **SamAccountName** (the NetBIOS-style name with a trailing `$`, e.g. `WIN-K5POLANERI5$`) — not a DNS/FQDN-style hostname (`WIN-K5POLANERI5.company.com`), which fails to resolve with `Cannot find an object with identity`. Confirmed directly: `Get-ADComputer -Identity 'WIN-K5POLANERI5$'` resolves; the FQDN form does not. For more than one server, use a security group containing every server's computer account here instead of listing them individually.
4. **Install and test it on the IIS server**:
   ```powershell
   Install-ADServiceAccount -Identity BlueTrackSvc
   Test-ADServiceAccount -Identity BlueTrackSvc
   ```
5. **Point the App Pool at it**: in IIS Manager, the App Pool's **Advanced Settings > Identity > Custom account**, enter `DOMAIN\BlueTrackSvc$` (trailing `$`) with no password — Windows manages the password automatically for a gMSA.
6. **Grant it SQL Server access**: `.\Install-BlueTrack.ps1 -Step Db.AppPoolAccess -GrantAppPoolSqlAccess $true` reads the App Pool's new identity and does both parts. By hand: `CREATE LOGIN [DOMAIN\BlueTrackSvc$] FROM WINDOWS;`, then run `Database/41_BlueTrack_GrantAppServiceAccountAccess.sql` against the target database with `DOMAIN\BlueTrackSvc$` substituted in — see `Admin_OperationsGuide.md`'s "Granting the app's own SQL Server access" section for the full detail.
7. **Check Windows sign-in still works (Kerberos)**: with a custom App Pool identity, IIS by default still decrypts Kerberos tickets with the *computer* account (kernel-mode authentication on, `useAppPoolCredentials` off). That works as long as the site's `HTTP/<hostname>` SPN stays on the computer account (its built-in `HOST/` SPNs cover it). If you register `HTTP/<hostname>` on the gMSA instead, also set `useAppPoolCredentials` to `true` on the `/BlueTrack` application, or sign-in fails with `401.1`. Never register the same SPN on two accounts. This is standard IIS behavior, not yet tested on a BlueTrack server.

**A separate, important note if this environment is small enough that the app server and a Domain Controller are the same machine**: confirmed directly this session that gMSA creation still works from a DC (the identity-format fix above was the actual issue, not DC-ness). That said, running IIS/SQL Server/the application stack directly on a Domain Controller is strongly discouraged in general — a DC holds the entire domain's credential database, so anything that could compromise the app-hosting side has a much larger blast radius than on a regular member server. Worth raising with whoever owns this environment's architecture if that's the current setup, independent of the gMSA question.

## 4. First sign-in

A fresh install ships with `BUILTIN\Administrators` bootstrapped as the Admin role — sign in as a member of that local group using Windows Integrated authentication (no extra setup needed for this first sign-in). This is a deliberate "usable immediately" default, not something to leave in place for a real production environment.

## 5. Replace the bootstrap admin group

Once you're in, go to **Admin > Group / Role Mapping** and map a real AD/Entra group to the Admin role. Plan to remove the `BUILTIN\Administrators` bootstrap mapping once your real admin group is confirmed working — see `User_Guide.md`'s Group / Role Mapping section for the exact steps.

## 6. Load real data

A fresh install's staging tables are empty. Follow `Admin_DeploymentRunbook.md`'s "First Data Load" section to run `usp_Import_All` / `usp_RunFullLoad` once your CyberArk export files are in place, then (for a real, non-disposable environment) schedule the nightly Import+Load job as that runbook describes.

### Data feed folders (Admin > Data Sources)

If you'll use scheduled CSV data feeds (**Admin > Data Sources**, D-181), grant the **App Pool's account** read access to each feed folder; the installer doesn't. Unlike the CyberArk export folder, which SQL Server's service account reads, feeds are read by the API itself. The account is the same one that needs the SQL login (section 3):

- `ApplicationPoolIdentity` and a folder on this server: `IIS APPPOOL\<pool>`, e.g. `IIS APPPOOL\BlueTrack-AppPool`.
- `ApplicationPoolIdentity` and a share on another machine: the server's computer account, `DOMAIN\HOSTNAME$`.
- A gMSA or service account: that account.

For example, for a local folder: `icacls D:\Feeds\BlueTrack /grant "IIS APPPOOL\BlueTrack-AppPool:(OI)(CI)RX"`. A share also needs share-level read permission for the account. Afterwards, the feed's **Test** button confirms the access, since it runs as that account.

## 7. What's next

Installation gets you a running, reachable BlueTrack with Windows Integrated authentication and the default Windows DPAPI secrets backend. Before opening it up to regular users, continue to [Admin_Configuration.md](Admin_Configuration.md) to review identity providers, the secrets store backend, roles, notifications, and the other admin-wide settings that a real environment normally needs to change from their installed defaults.

## See also

- `Deploy/README.md` — the installer's full option reference.
- `Admin_DeploymentRunbook.md` — the manual step-by-step procedure, and the environment-specific items a redeployment must not skip.
- `Claude_Docs/Design_Deployment-Methodology.md` — the reasoning behind the deployment approach.
