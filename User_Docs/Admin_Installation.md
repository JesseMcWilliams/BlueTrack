# Admin Guide: Installation

**Blueprint Progress Tracking Web Interface**

## Who this is for

Whoever is standing up a brand-new BlueTrack environment for the first time. This is a short, linear walkthrough of the first-time setup path. For full option-by-option detail, follow the links to `Deploy/README.md`. Once installation is done, continue to [Admin_Configuration.md](Admin_Configuration.md). To upgrade an environment that's already running, see [Admin_DeploymentRunbook.md](Admin_DeploymentRunbook.md), "Upgrading an Existing Environment".

## 1. Prerequisites

You need: the .NET 10 SDK, Node.js, SQL Server reachable from this host, and (for a real environment) IIS with its Windows Authentication role service, the ASP.NET Core Hosting Bundle **10.0.13 or later** and the IIS URL Rewrite Module. `Deploy/Install-BlueTrack.ps1` checks for all of these and offers to install what's missing, except SQL Server itself (see `Deploy/README.md`, "What it deliberately does NOT do").

- **Hosting Bundle version**: some security fixes BlueTrack relies on are in .NET itself, so the server must run .NET 10.0.13 or later. The installer checks it: a Hosting Bundle whose newest ASP.NET Core runtime is older than 10.0.13 counts as missing, and the installer offers to install the current one (`-Step Prereq.HostingBundle`; see `Deploy/README.md`). You can also check with `dotnet --list-runtimes` (it should list `Microsoft.AspNetCore.App 10.0.13` or higher). Installing it restarts IIS.

- **Where the prerequisite installers come from**: `-PrerequisiteSource` is `Auto` (the default), `Offline`, `Winget` or `Download`. `Auto` uses a folder of pre-downloaded installers when you give `-InstallerSourcePath`, otherwise winget where it's available (.NET SDK and Node.js only), otherwise the vendor's own download. For a server without internet access, download the installers on another machine and copy them into one folder; the file names the installer looks for are listed in `Deploy/README.md`, "Offline prerequisites". Every installer must carry a valid signature from its vendor, or it's refused.
- **CyberArk Application Password SDK (optional)**: needed only if CyberArk Credential Provider (CP) will be the secrets backend. The installer checks for it at `C:\Program Files\CyberArk\ApplicationPasswordSdk\NetStandardPasswordSDK.dll` but never installs it. Without it, BlueTrack still builds and installs normally; only the CyberArk CP secrets backend is unavailable on that server. Windows DPAPI, the default backend, doesn't need it.

## 2. Run the installer

From an elevated PowerShell session, in the `Deploy` folder:

```powershell
.\Install-BlueTrack.ps1 `
    -Environment Test `
    -SqlServerInstance "SQLSERVER01" `
    -DatabaseName BlueTrackTest `
    -SiteName BlueTrackTest `
    -Hostname <your-BlueTrack-hostname> `
    -GenerateSelfSignedCert
```

This one script builds the API and SPA, creates and migrates the database, writes the site's settings file (`appsettings.Site.json`: the database connection, HSTS, the allowed folders and the environment name), sets the Site URL that notification emails link to, provisions the IIS site, and smoke-tests the result. `-Environment` is a label shown on **Admin > Deployment** and used in the default install folders; under IIS, BlueTrack always runs as Production, so development-only sign-in stays off whatever you choose. Anything you leave out, it asks for. Add `-WhatIf` first to preview what it will do without changing anything. See `Deploy/README.md` for the full option list and `Admin_DeploymentRunbook.md` for the manual procedure it automates.

### Using an answer file

For a repeatable or unattended install, copy `Deploy/answers.sample.json` to `answers.<env>.json` (git-ignored), edit it, and run `.\Install-BlueTrack.ps1 -ConfigFile .\answers.<env>.json`. Leave a setting out to be prompted for it. Never put a password in it: the SQL login (`SqlCredential`) is always prompted and never saved. Command-line parameters win over the file.

### Steps, resuming and re-running

The install runs as named steps (`Prereq.*`, `Build.Api`, `Build.Web`, `Db.*`, `Iis.*`, `Smoke`). Your answers and each step's result are saved under `Deploy/State/` (`answers.<SiteName>.json` and `progress.<SiteName>.json`), so you don't have to start over after a failure:

```powershell
.\Install-BlueTrack.ps1 -ListSteps                  # list the steps and where the last run got to
.\Install-BlueTrack.ps1 -Resume                     # carry on from the step that failed
.\Install-BlueTrack.ps1 -Step Iis.Site              # re-run one step (wildcards allowed, e.g. Prereq.*)
.\Install-BlueTrack.ps1 -StartAt Iis.AppPool        # re-run that step and every step after it
```

`-Resume`, `-Step` and `-StartAt` reuse the saved answers, so they don't prompt again. If `Deploy/State/` holds more than one site, add `-SiteName`. A plain run (none of the three) starts a fresh progress record.

### Choices worth making up front

| Option | What it decides | Default |
|---|---|---|
| `-ApiInstallPath` / `-WebInstallPath` | Where the API and the site's pages are installed. The site is served from its own folder, so a later build in the source folder never changes the live site. Re-running `Build.Web`, `Iis.WebConfig` and `Iis.Site` moves an existing site to `-WebInstallPath`. | `C:\inetpub\BlueTrack\<Environment>\api` and `...\web` |
| `-InstallNightlyJob` / `-ImportSources` | Whether to create the nightly Import+Load SQL Agent job, and which CyberArk sources you have: `Both`, `PrivilegeCloud` or `SelfHosted`. Then `-ExportFolderPath` (Privilege Cloud export folder) when Privilege Cloud is included. Self-Hosted is read from PVWA (Self-Hosted Direct): set its PVWA address and credential on **Admin > Data Sources** after installing; until then the nightly import fails, saying so. These answers set the CyberArk sources on first install; after that, change them on **Admin > Data Sources**. | prompted |
| `-GrantAppPoolSqlAccess` | Whether the `Db.AppPoolAccess` step gives the App Pool's account its SQL login and permissions (section 3). `-AppPoolSqlLogin` names a different account. | prompted |
| `-ResetIis` | Whether the `Iis.Reset` step runs `iisreset` after the IIS steps. This briefly stops **every** site on the server. The installer always restarts just the BlueTrack site afterwards, and redeploying over a running API doesn't need a manual `iisreset`. | prompted |
| `-AllowedFileRoots` | The only folders data feeds may read and the Deployment page's backups may be written to. Unset: any local folder, and no network shares. List a share here to use one, e.g. `-AllowedFileRoots 'D:\Feeds','\\<fileserver>\bluetrack'`. | none |
| `-TrustServerCertificate` | Whether to skip validating SQL Server's TLS certificate. Leave it unset: the installer then trusts the certificate only when SQL Server is on this machine. For a remote SQL Server, its certificate must be trusted by this server. | worked out |
| `-BackupFolder` | Where the pre-deployment backup goes when the database already exists (an upgrade). See "Backup folder access" below. | asked only if needed |

### Certificates

The site needs an HTTPS certificate for its hostname:

- **Real environments**: put the certificate in the computer's personal store (`Cert:\LocalMachine\My`) and pass `-CertificateThumbprint <thumbprint>`.
- **Dev/Test only**: `-GenerateSelfSignedCert` creates a self-signed certificate for `-Hostname`. Browsers on other machines won't trust it until it's added to their trusted roots.

The certificate needs a **Subject Alternative Name** (SAN) for the hostname. Chrome and Edge reject a certificate that has the name only in its Subject. To check a certificate before you use it:

```powershell
(Get-Item Cert:\LocalMachine\My\<thumbprint>).Extensions |
    Where-Object { $_.Oid.Value -eq '2.5.29.17' } |   # Subject Alternative Name
    ForEach-Object { $_.Format($false) }
```

No output means it has no SAN; request a new one that includes the hostname as a DNS name.

**Replacing the certificate later** (for example, at renewal): import the new certificate into `Cert:\LocalMachine\My`, then switch the site's HTTPS binding to it:

```powershell
.\Install-BlueTrack.ps1 -Step Iis.Site -CertificateThumbprint <thumbprint>
```

The step reports the thumbprint the site now uses and saves it to the site's saved answers, so later installer runs keep it. Remove the old certificate from the store once the site is confirmed working with the new one.

**SAML signing certificate** (only if you'll use SAML sign-in): this is a separate certificate, not the HTTPS one. The App Pool's account must be able to read its private key. For a certificate in `LocalMachine\My`: open `certlm.msc`, right-click the certificate, choose **All Tasks > Manage Private Keys**, and give the App Pool's account **Read**. If it can't read the key, SAML sign-in returns "SAML sign-in is not available" and the Identity Providers health check reports it. Choosing the certificate: `Admin_Configuration.md`, "SAML".

**Optional: a SAML certificate only the App Pool's account holds.** BlueTrack looks for the SP certificate in the App Pool account's own store (`CurrentUser\My`) before `LocalMachine\My`, so the account can hold a certificate whose key nobody else can read. This works for a gMSA or a domain account, not for `ApplicationPoolIdentity`. There's no installer step for it; do it by hand:

1. **On your CA:** make a certificate template for SAML signing (for example, a copy of the *Web Server* template), give the App Pool's account (for a gMSA, `DOMAIN\BlueTrackSvc$`) **Read** and **Enroll** on it, and publish the template on the CA.
2. **In IIS:** set the App Pool's **Load User Profile** (Advanced Settings) to **True**, so the account has its own certificate store.
3. **Enroll as the App Pool's account.** A gMSA can't sign in interactively, so run the request as a one-time scheduled task under that account. For example, in an elevated PowerShell:

   ```powershell
   $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -Command Get-Certificate -Template '<template name>' -CertStoreLocation Cert:\CurrentUser\My"
   $principal = New-ScheduledTaskPrincipal -UserId 'DOMAIN\BlueTrackSvc$' -LogonType Password
   Register-ScheduledTask -TaskName 'BlueTrack SAML certificate' -Action $action -Principal $principal
   Start-ScheduledTask -TaskName 'BlueTrack SAML certificate'
   ```

   When it has finished, remove the task (`Unregister-ScheduledTask -TaskName 'BlueTrack SAML certificate'`). The template must let the subject name be built from Active Directory, or supply one in the request.
4. **Find the thumbprint** (the CA's issued-certificates list, or a second task that lists `Cert:\CurrentUser\My`), enter it as **SP Certificate Thumbprint** on Identity Providers, and check the **Identity Providers** health check on the Deployment page. Renew the same way before the certificate expires.

These steps haven't been run on a real install yet; confirm the template settings and the task with your PKI administrator the first time.

### Backup folder access

A backup is a full copy of the database, including stored credentials, so the backup folder is limited to specific accounts. When the installer takes the pre-deployment backup, and whenever `Deploy/Backup-BlueTrack.ps1` runs, the folder (and the backups already in it) has inheritance turned off and broad groups removed: Everyone, Users, Authenticated Users, Guests, Domain Users and Domain Guests. Other existing entries are kept.

- **The installer** gives Modify on the folder to the SQL Server service account (which writes the backups) and to the App Pool's account.
- **`Backup-BlueTrack.ps1`** gives Modify to the SQL Server service account only.
- **Scheduled backups** (set on Global Application Configuration; see [Admin_OperationsGuide.md](Admin_OperationsGuide.md)) need the App Pool's account (section 3) to have **Modify** on the folder, to delete old scheduled backups. If the folder was set up only by `Backup-BlueTrack.ps1`, grant it, for example `icacls <backup folder> /grant "IIS APPPOOL\<SiteName>-AppPool:(OI)(CI)M"`. Without it the backup still succeeds, but its clean-up fails.
- The folder must be on the local server, or inside a folder listed in `-AllowedFileRoots`.

If a broad group can still read the folder, BlueTrack removes that access before its own backups or, if it can't, refuses the backup and says why on the Deployment page.

### The smoke test

The last step, `Smoke`, makes two checks as the user running the installer: the API's health check at `https://<your-BlueTrack-hostname>/BlueTrack/api/admin/deployment`, which should return JSON, and the site root, which should serve the BlueTrack page. If either fails, the output explains the HTTP status and ends with both addresses, so you can try them in a browser on the server. `Deploy/README.md`, "Smoke test failures", lists each status and what to do. Re-run just this check with `-Step Smoke`.

## 3. Set up a dedicated service account for the App Pool identity (recommended)

`Install-BlueTrack.ps1` creates the App Pool (named `<SiteName>-AppPool`) using IIS's default `ApplicationPoolIdentity` virtual account, with no parameter to specify a different identity; switching it is a manual follow-up step. Do this before real users start relying on the environment, since it affects how the app authenticates to SQL Server.

**What `ApplicationPoolIdentity` means for SQL Server**: with SQL Server on the same machine (the normal single-server layout), the App Pool reaches it as its own virtual account, `IIS APPPOOL\<pool>` (for example `IIS APPPOOL\BlueTrack-AppPool`); with SQL Server on another machine, as the server's computer account, `DOMAIN\HOSTNAME$`. The installer's `Db.AppPoolAccess` step works out which, and grants that account its SQL login and permissions (see `Deploy/README.md`, "App pool SQL access"). Watch for one trap: with SQL Server on the same machine, the `Login failed for user '...'` message names the *computer account*, although the login that's needed is `IIS APPPOOL\<pool>`. Granting the computer account doesn't help; it works once `IIS APPPOOL\<pool>` has the login.

**Re-run `Db.AppPoolAccess` on an existing install after upgrading**: the grants it applies change from time to time. The current version makes the audit log append-only for the app's account (it can add audit entries but not change or delete them) and adds `db_backupoperator` for the Deployment page's backups. To apply them:

```powershell
.\Install-BlueTrack.ps1 -Step Db.AppPoolAccess -GrantAppPoolSqlAccess $true
```

The user running it needs `securityadmin` (or `sysadmin`) on SQL Server.

**Why a dedicated account is still recommended**: least privilege and clarity. A gMSA's database access belongs to BlueTrack alone, it is the same account whether SQL Server is local or remote, and SQL Server's error messages name it correctly. (A computer-account grant, needed for a remote SQL Server with `ApplicationPoolIdentity`, also covers everything else on the server running as `NETWORK SERVICE`, `SYSTEM` or another App Pool.) A dedicated domain service account, or a **gMSA** (Group Managed Service Account, the standard modern replacement for a manually-managed service account password), is the recommended setup.

### Setting up a gMSA

1. **Confirm the domain supports it**: `(Get-ADDomain).DomainMode` needs to be at least a Windows Server 2012-level domain (gMSA didn't exist before that).
2. **Create the KDS Root Key, once per domain** (needs Domain Admin rights; check first with `Get-KdsRootKey`, and skip this step if one already exists):
   ```powershell
   Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))
   ```
   The `-10` hours backdating makes it usable immediately, for a lab/test domain. Microsoft's own default (no backdating) waits ~10 hours before the key is usable, to give AD replication time to converge safely across every DC in a real multi-DC domain. Don't skip that safety margin on a real production domain.
3. **Create the gMSA** (Domain Admin, or a delegated OU):
   ```powershell
   New-ADServiceAccount -Name BlueTrackSvc -DNSHostName <your-BlueTrack-hostname> `
       -PrincipalsAllowedToRetrieveManagedPassword "HOSTNAME$"
   ```
   `-PrincipalsAllowedToRetrieveManagedPassword` needs the computer's **SamAccountName** (the NetBIOS-style name with a trailing `$`, e.g. `APPSERVER01$`), not a DNS/FQDN-style hostname, which fails to resolve with `Cannot find an object with identity`. `Get-ADComputer -Identity 'APPSERVER01$'` confirms the name resolves. For more than one server, use a security group containing every server's computer account here instead of listing them individually.
4. **Install and test it on the IIS server**:
   ```powershell
   Install-ADServiceAccount -Identity BlueTrackSvc
   Test-ADServiceAccount -Identity BlueTrackSvc
   ```
5. **Point the App Pool at it**: in IIS Manager, the App Pool's **Advanced Settings > Identity > Custom account**, enter `DOMAIN\BlueTrackSvc$` (trailing `$`) with no password. Windows manages the password automatically for a gMSA.
6. **Grant it SQL Server access**: `.\Install-BlueTrack.ps1 -Step Db.AppPoolAccess -GrantAppPoolSqlAccess $true` reads the App Pool's new identity and does both parts. By hand: `CREATE LOGIN [DOMAIN\BlueTrackSvc$] FROM WINDOWS;`, then run `Database/Manual/02_BlueTrack_GrantAppServiceAccountAccess.sql` against the target database with `DOMAIN\BlueTrackSvc$` substituted for `__TARGET_ACCOUNT__`. See `Admin_OperationsGuide.md`'s "Granting the app's own SQL Server access" section for the full detail.
7. **Give it the folder access the old account had**: the backup folder (Modify) and any data feed folders (read); see "Backup folder access" above and "Data feed folders" below.
8. **Check Windows sign-in still works (Kerberos)**: with a custom App Pool identity, IIS by default still decrypts Kerberos tickets with the *computer* account (kernel-mode authentication on, `useAppPoolCredentials` off). That works as long as the site's `HTTP/<hostname>` SPN stays on the computer account (its built-in `HOST/` SPNs cover it). If you register `HTTP/<hostname>` on the gMSA instead, also set `useAppPoolCredentials` to `true` on the `/BlueTrack` application, or sign-in fails with `401.1`. Never register the same SPN on two accounts. This is standard IIS behavior, not yet tested on a BlueTrack server.

**If the app server and a Domain Controller are the same machine**: gMSA creation still works from a DC. That said, running IIS, SQL Server and the application stack directly on a Domain Controller is strongly discouraged in general: a DC holds the entire domain's credential database, so anything that could compromise the app-hosting side has a much larger blast radius than on a regular member server. Raise it with whoever owns this environment's architecture if that's the current setup.

## 4. First sign-in

A fresh install ships with `BUILTIN\Administrators` bootstrapped as the Admin role, and Windows Integrated authentication as the only enabled sign-in method (and so the default). Browse to `https://<your-BlueTrack-hostname>/` as a member of that local group; the installer has already enabled Windows Authentication in IIS, so no extra setup is needed. You're signed in and taken to the dashboard (or the page you asked for). If you stay on the sign-in page instead, select **Continue** to retry; it shows a message if sign-in still fails.

The bootstrap mapping is a deliberate "usable immediately" default, not something to leave in place for a real production environment.

If sign-in fails or keeps prompting, run the smoke test (`-Step Smoke`) and check its output against `Deploy/README.md`, "Smoke test failures". A browser tab left open from before an upgrade may still be running the old page: reload it (Ctrl+F5).

## 5. Replace the bootstrap admin group

Once you're in, go to **Admin > Group / Role Mapping** and map a real AD/Entra group to the Admin role. Windows groups are shown by name (`DOMAIN\Group`), with the stored SID beneath; the bootstrap mapping appears as `BUILTIN\Administrators`. Plan to remove that bootstrap mapping once your real admin group is confirmed working; see `User_Guide.md`'s Group / Role Mapping section for the exact steps.

## 6. Load real data

A fresh install's staging tables are empty. Follow `Admin_DeploymentRunbook.md`'s "First Data Load" section to run `usp_Import_All` / `usp_RunFullLoad` once your CyberArk export files are in place, then (for a real, non-disposable environment) schedule the nightly Import+Load job: re-run the installer with `-Step Db.NightlyJob -InstallNightlyJob $true`, with `-ImportSources` set to the sources you have, or create it by hand as that runbook describes. For a Privilege-Cloud-only or Self-Hosted-only implementation, the job imports just that source.

### Data feed folders (Admin > Data Sources)

If you'll use scheduled CSV data feeds (**Admin > Data Sources**), grant the **App Pool's account** read access to each feed folder; the installer doesn't. Unlike the CyberArk export folder, which SQL Server's service account reads, feeds are read by the API itself. The account is the same one that needs the SQL login (section 3):

- `ApplicationPoolIdentity` and a folder on this server: `IIS APPPOOL\<pool>`, e.g. `IIS APPPOOL\BlueTrack-AppPool`.
- `ApplicationPoolIdentity` and a share on another machine: the server's computer account, `DOMAIN\HOSTNAME$`.
- A gMSA or service account: that account.

For example, for a local folder: `icacls D:\Feeds\BlueTrack /grant "IIS APPPOOL\BlueTrack-AppPool:(OI)(CI)RX"`. A share also needs share-level read permission for the account, and must be inside a folder listed in the installer's `-AllowedFileRoots` (network shares are refused otherwise). Afterwards, the feed's **Test** button confirms the access, since it runs as that account.

## 7. What's next

Installation gets you a running, reachable BlueTrack with Windows Integrated authentication and the default Windows DPAPI secrets backend. Before opening it up to regular users, continue to [Admin_Configuration.md](Admin_Configuration.md) to review identity providers, the secrets store backend, roles, notifications, and the other admin-wide settings that a real environment normally needs to change from their installed defaults.

To install a newer version later, use `Deploy/Update-BlueTrack.ps1`; see `Admin_DeploymentRunbook.md`, "Upgrading an Existing Environment".

## See also

- `Deploy/README.md`: the installer's full option reference.
- `Admin_DeploymentRunbook.md`: the manual step-by-step procedure, upgrading, and the environment-specific items a redeployment must not skip.
- `Admin_OperationsGuide.md`: day-to-day operation, including backups.
