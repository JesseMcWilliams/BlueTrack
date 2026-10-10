<#
.SYNOPSIS
    Full from-scratch deployment of BlueTrack: verifies/installs prerequisites,
    builds the API and SPA from source, creates and migrates the database,
    provisions the IIS site, and smoke-tests the result.

.DESCRIPTION
    Deliberate scope boundaries (see Deploy/README.md for the full list):
      - Never installs SQL Server itself -- only verifies connectivity.
      - Never configures a real identity provider, real secrets backend, or
        loads real CyberArk export data -- these need real per-environment
        values that don't exist at install time.
      - Not a general "upgrade an existing production install" tool -- built
        for a fresh (or fresh-ish) environment, but safe to re-run: each step
        checks current state before acting rather than failing or duplicating.

    Every value below can be supplied as a parameter, via -ConfigFile (a JSON
    file with the same property names; see answers.sample.json), or left
    blank to be prompted for interactively. Nothing credential-like is ever
    prompted as plain text or saved.

    The install runs as a list of named steps (-ListSteps shows them). Answers
    and each step's outcome are saved under Deploy/State/ (D-168), so a failed
    run can be continued with -Resume, and any step re-run with -Step.

.PARAMETER ConfigFile
    Path to a JSON answer file supplying any of this script's settings, for
    unattended/repeatable runs. Explicit command-line parameters take
    precedence over the config file, which takes precedence over answers
    saved by an earlier run.

.PARAMETER Step
    Run only these steps (wildcards allowed, e.g. Prereq.*), in install order,
    whatever their saved status. Naming a prerequisite step here installs it
    without the y/N prompt.

.PARAMETER StartAt
    Run this step and every step after it.

.PARAMETER Resume
    Run, in order, every step that hasn't completed yet (per
    Deploy/State/progress.<SiteName>.json), reusing the saved answers.

.PARAMETER ListSteps
    List the steps and their saved status, then exit.

.PARAMETER PrerequisiteSource
    Where prerequisite installers come from: Auto (default: offline folder if
    -InstallerSourcePath is given, else winget where available, else vendor
    download), Offline, Winget or Download.

.PARAMETER InstallerSourcePath
    Folder of pre-downloaded installers, for servers without internet access.
    See Deploy/README.md, "Offline prerequisites".

.EXAMPLE
    .\Install-BlueTrack.ps1 -Environment Test -SqlServerInstance localhost `
        -SiteName BlueTrackTest -Hostname bluetrack-test.company.com -GenerateSelfSignedCert

.EXAMPLE
    .\Install-BlueTrack.ps1 -ConfigFile .\answers.prod.json -WhatIf

.EXAMPLE
    .\Install-BlueTrack.ps1 -Resume

.EXAMPLE
    .\Install-BlueTrack.ps1 -Step Prereq.HostingBundle -PrerequisiteSource Download
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigFile,

    [ValidateSet('Development', 'Test', 'Staging', 'Production')]
    [string]$Environment,

    # --- Database ---
    [string]$SqlServerInstance,
    [string]$DatabaseName = 'BlueTrack',
    [bool]$UseWindowsAuth = $true,
    [pscredential]$SqlCredential,
    [bool]$SeedTestData,
    [bool]$InstallNightlyJob,
    [string]$ExportFolderPath,
    [string]$EvdDatabaseName,
    [ValidateSet('Both', 'PrivilegeCloud', 'SelfHosted')]
    [string]$ImportSources,
    [bool]$GrantAppPoolSqlAccess,
    [string]$AppPoolSqlLogin,
    [bool]$ResetIis,

    # --- IIS ---
    [string]$SiteName = 'BlueTrack',
    [string]$ApiInstallPath,
    [string]$WebInstallPath,
    [string]$Hostname,
    [int]$HttpPort = 80,
    [int]$HttpsPort = 443,
    [string]$CertificateThumbprint,
    [switch]$GenerateSelfSignedCert,

    # --- Prerequisite installers ---
    [ValidateSet('Auto', 'Offline', 'Winget', 'Download')]
    [string]$PrerequisiteSource = 'Auto',
    [string]$InstallerSourcePath,

    # --- Step selection ---
    [string[]]$Step,
    [string]$StartAt,
    [switch]$Resume,
    [switch]$ListSteps,

    # --- Behavior ---
    [switch]$Force,
    [switch]$SkipPrerequisiteInstall,
    [switch]$SkipSmokeTest,
    [switch]$SkipPreDeployBackup,
    [string]$BackupFolder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -WhatIf sets $WhatIfPreference in this script's scope only. Script modules
# resolve preference variables from their own scope and the global one, so
# when this script is run as .\Install-BlueTrack.ps1 -WhatIf every
# BlueTrack.*.psm1 function would act for real (D-168). Default-parameter
# values apply at each call site, so this passes -WhatIf explicitly to every
# BlueTrack module function. (Not '*:WhatIf': built-in cmdlets already see
# this scope's preference, and ForEach-Object rejects -WhatIf outright.)
if ($WhatIfPreference) { $PSDefaultParameterValues['*-BlueTrack*:WhatIf'] = $true }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DeployRoot = $PSScriptRoot
$ConnectionString = $null
$IisModuleImported = $false
$transcriptPath = $null

#region Elevation check
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Error 'This script must be run from an elevated (Run as Administrator) PowerShell session -- it installs Windows features, IIS sites, and SQL Agent jobs, none of which work otherwise. Re-launch PowerShell as Administrator and try again.'
    exit 1
}
#endregion

#region Step table
# Install order. Needs: the answers a step requires, resolved (prompted if
# still missing) before any step runs, so a run never stops halfway to ask.
# Condition: when it's false, the step is recorded NotApplicable instead of
# run. Prereq.* steps carry PrereqKey and run through Invoke-PrerequisiteStep.
$Steps = @(
    @{ Name = 'Prereq.DotNetSdk'; PrereqKey = 'DotNetSdk'; Description = '.NET 10 SDK' }
    @{ Name = 'Prereq.NodeJs'; PrereqKey = 'NodeJs'; Description = 'Node.js' }
    @{ Name = 'Prereq.Iis'; PrereqKey = 'Iis'; Description = 'IIS Web-Server role' }
    @{ Name = 'Prereq.IisWindowsAuth'; PrereqKey = 'IisWindowsAuth'; Description = 'IIS Windows Authentication role service' }
    @{ Name = 'Prereq.HostingBundle'; PrereqKey = 'HostingBundle'; Description = 'ASP.NET Core Hosting Bundle (ANCM)' }
    @{ Name = 'Prereq.UrlRewrite'; PrereqKey = 'UrlRewrite'; Description = 'IIS URL Rewrite Module' }
    @{ Name = 'Prereq.CyberArkSdk'; PrereqKey = 'CyberArkSdk'; Description = 'CyberArk Application Password SDK (check only)' }
    @{ Name = 'Build.Api'; Needs = @('ApiInstallPath'); Description = 'dotnet publish App/Api to the API install path' }
    @{ Name = 'Build.Web'; Needs = @('WebInstallPath'); Description = 'npm ci + npm run build in App/Web, then copy it to the site folder' }
    @{ Name = 'Db.Backup'; Needs = @('Database'); Description = 'Pre-deployment backup, if the database already has a schema'
        Condition = { -not $SkipPreDeployBackup }; SkipReason = '-SkipPreDeployBackup' }
    @{ Name = 'Db.Migrate'; Needs = @('Database'); Description = 'Run App/Migrator against Database/' }
    @{ Name = 'Db.SeedTestData'; Needs = @('Database', 'SeedTestData'); Description = 'Run App/Migrator against Database/Test'
        Condition = { $SeedTestData }; SkipReason = 'SeedTestData is off' }
    @{ Name = 'Db.AppSettings'; Needs = @('Environment', 'Database', 'ApiInstallPath'); Description = 'Write appsettings.<Environment>.json' }
    @{ Name = 'Db.NightlyJob'; Needs = @('Database', 'InstallNightlyJob'); Description = 'Install the nightly Import+Load SQL Agent job'
        Condition = { $InstallNightlyJob }; SkipReason = 'InstallNightlyJob is off' }
    @{ Name = 'Iis.AppPool'; Description = 'Create the Application Pool' }
    @{ Name = 'Db.AppPoolAccess'; Needs = @('Database', 'GrantAppPoolSqlAccess'); Description = 'Give the app pool''s account a SQL login + Database/41 grants'
        Condition = { $UseWindowsAuth -and $GrantAppPoolSqlAccess }; SkipReason = 'GrantAppPoolSqlAccess is off, or the API uses SQL authentication' }
    @{ Name = 'Iis.Certificate'; Needs = @('Certificate'); Description = 'Resolve or generate the HTTPS certificate' }
    @{ Name = 'Iis.Site'; Needs = @('Certificate', 'ApiInstallPath', 'WebInstallPath'); Description = 'Create the site, /BlueTrack Application and bindings' }
    @{ Name = 'Iis.WebConfig'; Description = 'Write the site-root web.config and recycle the app pool' }
    @{ Name = 'Iis.Reset'; Needs = @('ResetIis'); Description = 'iisreset so every IIS change takes effect (stops all sites briefly)'
        Condition = { $ResetIis }; SkipReason = 'ResetIis is off' }
    @{ Name = 'Iis.SiteRestart'; Description = 'Stop and start the BlueTrack site, so IIS re-applies its sign-in settings (stops only this site, briefly)' }
    @{ Name = 'Smoke'; Needs = @('Hostname'); Description = 'Call the Deployment Info health check'
        Condition = { -not $SkipSmokeTest }; SkipReason = '-SkipSmokeTest' }
)
$StepNames = @($Steps | ForEach-Object { $_.Name })
#endregion

#region Helpers for required interactive prompts
# A blank answer here (just pressing Enter) must never silently proceed --
# each of these values is load-bearing several steps later, so catching a
# blank answer immediately, at the prompt itself, is much cheaper than
# surfacing it as a cryptic parameter-binding error afterward.
function Read-BlueTrackRequiredValue {
    param([Parameter(Mandatory)] [string]$Prompt)
    do {
        $value = Read-Host $Prompt
        if (-not $value) { Write-Warning 'This value is required and cannot be blank.' }
    } while (-not $value)
    return $value
}

function Read-BlueTrackEnvironment {
    $allowed = 'Development', 'Test', 'Staging', 'Production'
    do {
        $value = Read-Host "Environment ($($allowed -join '/'))"
        if ($value -notin $allowed) { Write-Warning "Must be one of: $($allowed -join ', ')." }
    } while ($value -notin $allowed)
    return $value
}
#endregion

#region Answer and state helpers
# Names answered by the command line, -ConfigFile or saved answers. Tracked
# separately from the values themselves because [bool] answers like
# SeedTestData default to $false, which can't be told apart from "No".
$Answered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

# Settings with a real default: saved even when nobody answered them, since
# they'd never be prompted for anyway.
$DefaultedAnswers = 'DatabaseName', 'UseWindowsAuth', 'SiteName', 'HttpPort', 'HttpsPort', 'PrerequisiteSource'

function Register-InstallAnswer {
    # Records one answer in memory; Save-InstallAnswer writes them out.
    param([Parameter(Mandatory)] [string]$Name, $Value)
    Set-Variable -Name $Name -Value $Value -Scope Script -WhatIf:$false
    [void]$script:Answered.Add($Name)
}

function Import-AnswerSource {
    # Fills in answers not already answered by a higher-precedence source.
    param([Parameter(Mandatory)] [hashtable]$Answers)
    foreach ($name in $Answers.Keys) {
        if (-not $script:Answered.Contains($name)) {
            Register-InstallAnswer -Name $name -Value $Answers[$name]
        }
    }
}

function Save-InstallAnswer {
    # Honors -WhatIf through Save-BlueTrackAnswerFile.
    $values = @{}
    foreach ($name in (Get-BlueTrackAnswerName)) {
        if ($script:Answered.Contains($name) -or $name -in $script:DefaultedAnswers) {
            $values[$name] = Get-Variable -Name $name -Scope Script -ValueOnly
        }
    }
    Save-BlueTrackAnswerFile -Path $script:AnswersPath -Answers $values
}

function Resolve-InstallAnswer {
    # Prompts for one need from the step table, if it isn't answered yet.
    param([Parameter(Mandatory)] [string]$Name)
    switch ($Name) {
        'Environment' {
            if (-not $script:Environment) { Register-InstallAnswer Environment (Read-BlueTrackEnvironment) }
        }
        'Database' {
            if (-not $script:SqlServerInstance) {
                Register-InstallAnswer SqlServerInstance (Read-BlueTrackRequiredValue 'SQL Server instance (e.g. localhost, SERVER\INSTANCE)')
            }
            if (-not $script:UseWindowsAuth -and -not $script:SqlCredential) {
                # Not via Register-InstallAnswer: a credential is never saved.
                $script:SqlCredential = Get-Credential -Message 'SQL Server login for BlueTrack (Windows Integrated Security was declined)'
            }
        }
        'Hostname' {
            if (-not $script:Hostname) { Register-InstallAnswer Hostname (Read-BlueTrackRequiredValue 'Site hostname (e.g. bluetrack.company.com)') }
        }
        'SeedTestData' {
            if (-not $script:Answered.Contains('SeedTestData')) {
                Resolve-InstallAnswer Environment
                $seed = $false
                if ($script:Environment -ne 'Production') {
                    $seed = (Read-Host 'Seed DevFakeAuth/synthetic test data (Database/Test)? Never do this for Production. (y/N)') -match '^[Yy]'
                }
                Register-InstallAnswer SeedTestData $seed
            }
        }
        'InstallNightlyJob' {
            if (-not $script:Answered.Contains('InstallNightlyJob')) {
                Register-InstallAnswer InstallNightlyJob ((Read-Host 'Install the nightly Import+Load SQL Agent job now? (y/N)') -match '^[Yy]')
            }
            if ($script:InstallNightlyJob) {
                # D-176: an implementation may have only one CyberArk source;
                # the job then imports just that one.
                if (-not $script:ImportSources) {
                    $allowed = 'Both', 'PrivilegeCloud', 'SelfHosted'
                    do {
                        $value = Read-Host "Which CyberArk sources does this implementation have? ($($allowed -join '/'))"
                        if ($value -notin $allowed) { Write-Warning "Must be one of: $($allowed -join ', ')." }
                    } while ($value -notin $allowed)
                    Register-InstallAnswer ImportSources $value
                }
                if ($script:ImportSources -in 'Both', 'PrivilegeCloud' -and -not $script:ExportFolderPath) { Register-InstallAnswer ExportFolderPath (Read-BlueTrackRequiredValue 'Privilege Cloud CSV export folder path (local to the SQL Server service account)') }
                if ($script:ImportSources -in 'Both', 'SelfHosted' -and -not $script:EvdDatabaseName) { Register-InstallAnswer EvdDatabaseName (Read-BlueTrackRequiredValue 'Self-Hosted EVD database name (same SQL Server instance)') }
            }
        }
        'Certificate' {
            Resolve-InstallAnswer Hostname
            if (-not $script:CertificateThumbprint -and -not $script:GenerateSelfSignedCert) {
                $certChoice = Read-Host "HTTPS certificate: enter a thumbprint, or leave blank to generate a self-signed cert for '$($script:Hostname)' (Dev/Test only)"
                if ($certChoice) { Register-InstallAnswer CertificateThumbprint $certChoice } else { Register-InstallAnswer GenerateSelfSignedCert $true }
            }
        }
        'ApiInstallPath' {
            # Deliberately NOT $env:TEMP: that folder's ACLs are scoped to the
            # user running this script (plus SYSTEM/Administrators) and exclude
            # the IIS App Pool identity, and it's ephemeral (can be cleared any
            # time) -- both wrong for a directory IIS points a running
            # Application at permanently.
            if (-not $script:ApiInstallPath) {
                Resolve-InstallAnswer Environment
                Register-InstallAnswer ApiInstallPath (Join-Path $env:SystemDrive "inetpub\BlueTrack\$($script:Environment)\api")
            }
        }
        'WebInstallPath' {
            # D-187: the site's pages get their own folder next to the API's,
            # not the repo's App/Web/dist, so a local build never changes
            # what the site serves.
            if (-not $script:WebInstallPath) {
                Resolve-InstallAnswer Environment
                Register-InstallAnswer WebInstallPath (Join-Path $env:SystemDrive "inetpub\BlueTrack\$($script:Environment)\web")
            }
        }
        'ResetIis' {
            if (-not $script:Answered.Contains('ResetIis')) {
                $reset = (Read-Host 'Restart IIS (iisreset) after the IIS steps, so every change takes effect? This briefly stops EVERY site on this server. Recommended for the first install and after IIS changes. (y/N)') -match '^[Yy]'
                Register-InstallAnswer ResetIis $reset
            }
        }
        'GrantAppPoolSqlAccess' {
            # Only meaningful with Windows Integrated Security: with a SQL
            # login, the API connects as that login, not the app pool.
            if ($script:UseWindowsAuth -and -not $script:Answered.Contains('GrantAppPoolSqlAccess')) {
                $grant = (Read-Host "Give the IIS app pool's Windows account a SQL Server login and read/write/execute rights on '$($script:DatabaseName)' (Database/41, not db_owner)? The site can't reach the database without it. (y/N)") -match '^[Yy]'
                Register-InstallAnswer GrantAppPoolSqlAccess $grant
            }
        }
        default { throw "Unknown step need '$Name'." }
    }
}

function Get-InstallConnectionString {
    # Builds the connection string and confirms SQL Server is reachable, once per run.
    if (-not $script:ConnectionString) {
        $cs = Format-BlueTrackConnectionString -SqlServerInstance $script:SqlServerInstance -DatabaseName $script:DatabaseName `
            -UseWindowsAuth $script:UseWindowsAuth -SqlCredential $script:SqlCredential
        if (-not (Test-BlueTrackSqlConnection -ConnectionString $cs)) {
            throw "Could not connect to SQL Server instance '$($script:SqlServerInstance)'. This script does not install SQL Server itself -- confirm the instance is running and reachable, then re-run with -Resume."
        }
        Write-Host 'SQL Server connectivity confirmed.'
        $script:ConnectionString = $cs
    }
    return $script:ConnectionString
}

function Import-IisModule {
    # BlueTrack.Iis.psm1 is NOT imported with the other modules -- it
    # #Requires the WebAdministration module, which only exists once IIS
    # itself is installed. On a genuinely fresh box that's exactly what the
    # Prereq.Iis step might still need to install, so it's imported only
    # when the first Iis.* step runs.
    if (-not $script:IisModuleImported) {
        Import-Module (Join-Path $script:DeployRoot 'Modules\BlueTrack.Iis.psm1') -Force -Global
        $script:IisModuleImported = $true
    }
}

function Invoke-PrerequisiteStep {
    param(
        [Parameter(Mandatory)] [string]$Key,
        [switch]$Explicit
    )
    $item = Test-BlueTrackPrerequisite | Where-Object { $_.Key -eq $Key }
    if ($item.Installed) {
        Write-Host "$($item.Name): $($item.Detail)"
        return @{ Status = 'Completed'; Message = $item.Detail }
    }
    if (-not $item.AutoInstallable) {
        Write-Warning "$($item.Name): $($item.Detail)"
        return @{ Status = 'Completed'; Message = "Not installed (optional): $($item.Detail)" }
    }
    if ($SkipPrerequisiteInstall) {
        Write-Warning "$($item.Name) is missing and -SkipPrerequisiteInstall was set -- the install will likely fail later."
        return @{ Status = 'Declined'; Message = '-SkipPrerequisiteInstall' }
    }
    # Naming the step with -Step is the confirmation; otherwise ask.
    if (-not $Explicit -and (Read-Host "$($item.Name) is missing. Install it now? (y/N)") -notmatch '^[Yy]') {
        Write-Warning "Skipping $($item.Name) -- the install may fail later without it. Install it later with -Step Prereq.$Key."
        return @{ Status = 'Declined'; Message = 'Operator declined the install' }
    }

    Install-BlueTrackPrerequisite -Key $Key -Source $PrerequisiteSource -InstallerSourcePath $InstallerSourcePath | Out-Host
    if ($WhatIfPreference) { return @{ Status = 'Declined'; Message = 'WhatIf: not installed' } }

    $recheck = Test-BlueTrackPrerequisite | Where-Object { $_.Key -eq $Key }
    if (-not $recheck.Installed) {
        throw "$($item.Name): the installer finished, but the check still fails ($($recheck.Detail)). A reboot or a new PowerShell session may be needed; then re-run with -Step Prereq.$Key or -Resume."
    }
    Write-Host "$($item.Name): $($recheck.Detail)"
    return @{ Status = 'Completed'; Message = $recheck.Detail }
}
#endregion

#region Step actions
# Each returns @{ Status; Message } or nothing (= Completed). Throwing marks
# the step Failed and stops the run.
$StepActions = @{
    'Build.Api'       = {
        # Keep the .NET first-run banner out of the log, and don't create an
        # ASP.NET Core *development* HTTPS certificate on a server.
        $env:DOTNET_NOLOGO = '1'
        $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        Publish-BlueTrackApi -RepoRoot $RepoRoot -OutputDirectory $ApiInstallPath | Out-Null
    }
    'Build.Web'       = {
        $built = Invoke-BlueTrackWebBuild -RepoRoot $RepoRoot
        Publish-BlueTrackWeb -SourceDirectory $built -OutputDirectory $WebInstallPath | Out-Null
    }
    'Db.Backup'       = {
        # Rollback mechanism (Design_Deployment-Methodology.md, Option B):
        # back up before migrating, but only when there's something to lose --
        # a genuinely fresh/empty database has no existing schema/data worth a
        # rollback point for.
        $cs = Get-InstallConnectionString
        if (-not (Test-BlueTrackDatabaseHasExistingSchema -ConnectionString $cs -DatabaseName $DatabaseName)) {
            return @{ Status = 'Completed'; Message = 'No existing schema -- nothing to back up' }
        }
        if (-not $BackupFolder) {
            Register-InstallAnswer BackupFolder (Read-BlueTrackRequiredValue 'Existing database detected. Backup folder for the pre-deployment rollback point (local to the SQL Server service account)')
            Save-InstallAnswer
        }
        Backup-BlueTrackForRollback -ConnectionString $cs -DatabaseName $DatabaseName -BackupFolder $script:BackupFolder -RepoRoot $RepoRoot | Out-Null
        return @{ Status = 'Completed'; Message = "Backup taken in $($script:BackupFolder)" }
    }
    'Db.Migrate'      = {
        Invoke-BlueTrackMigrator -RepoRoot $RepoRoot -ConnectionString (Get-InstallConnectionString) -ScriptsFolder 'Database'
    }
    'Db.SeedTestData' = {
        Invoke-BlueTrackMigrator -RepoRoot $RepoRoot -ConnectionString (Get-InstallConnectionString) -ScriptsFolder 'Database/Test'
    }
    'Db.AppSettings'  = {
        Set-BlueTrackEnvironmentConfig -PublishDirectory $ApiInstallPath -Environment $Environment -ConnectionString (Get-InstallConnectionString)
    }
    'Db.NightlyJob'   = {
        Get-InstallConnectionString | Out-Null
        Install-BlueTrackNightlyJob -RepoRoot $RepoRoot -SqlServerInstance $SqlServerInstance -DatabaseName $DatabaseName `
            -ImportSources $ImportSources -ExportFolderPath $ExportFolderPath -EvdDatabaseName $EvdDatabaseName
    }
    'Db.AppPoolAccess' = {
        Import-IisModule
        Get-InstallConnectionString | Out-Null
        $account = $AppPoolSqlLogin
        if (-not $account) {
            $account = Get-BlueTrackAppPoolSqlLogin -AppPoolName "$SiteName-AppPool" -SqlServerInstance $SqlServerInstance
        }
        Write-Host "App pool '$SiteName-AppPool' reaches SQL Server as: $account"
        Grant-BlueTrackAppPoolSqlAccess -RepoRoot $RepoRoot -SqlServerInstance $SqlServerInstance -DatabaseName $DatabaseName -Account $account
        return @{ Status = 'Completed'; Message = "Granted $account" }
    }
    'Iis.AppPool'     = {
        Import-IisModule
        New-BlueTrackAppPool -Name "$SiteName-AppPool" -Force:$Force
    }
    'Iis.Certificate' = {
        Import-IisModule
        $thumbprint = New-BlueTrackCertificateBinding -CertificateThumbprint $CertificateThumbprint -Hostname $Hostname -GenerateSelfSigned:$GenerateSelfSignedCert
        if ($thumbprint -and $thumbprint -ne $CertificateThumbprint) {
            # Save the generated cert's thumbprint, so a re-run binds this one
            # instead of generating another.
            Register-InstallAnswer CertificateThumbprint $thumbprint
            Register-InstallAnswer GenerateSelfSignedCert $false
            Save-InstallAnswer
        }
        return @{ Status = 'Completed'; Message = "Thumbprint $thumbprint" }
    }
    'Iis.Site'        = {
        Import-IisModule
        if ($CertificateThumbprint) {
            # Confirms the certificate is still in Cert:\LocalMachine\My.
            $thumbprint = New-BlueTrackCertificateBinding -CertificateThumbprint $CertificateThumbprint -Hostname $Hostname
        } elseif ($WhatIfPreference) {
            # Under -WhatIf, Iis.Certificate didn't generate one to bind.
            $thumbprint = $null
        } else {
            throw 'No certificate thumbprint yet -- run the Iis.Certificate step first (-StartAt Iis.Certificate).'
        }
        New-BlueTrackSite -SiteName $SiteName -AppPoolName "$SiteName-AppPool" -SpaPhysicalPath $WebInstallPath -ApiPhysicalPath $ApiInstallPath `
            -Hostname $Hostname -HttpPort $HttpPort -HttpsPort $HttpsPort -CertificateThumbprint $thumbprint -Force:$Force
    }
    'Iis.WebConfig'   = {
        Import-IisModule
        Resolve-InstallAnswer WebInstallPath
        Set-BlueTrackSiteWebConfig -RepoRoot $RepoRoot -SpaPhysicalPath $WebInstallPath
        Restart-BlueTrackAppPool -Name "$SiteName-AppPool"
    }
    'Iis.Reset'       = {
        Import-IisModule
        Restart-BlueTrackIisService
        Start-Sleep -Seconds 5 # let W3SVC and the app pools come back before the smoke test
    }
    'Iis.SiteRestart' = {
        # D-188: always, after the IIS steps -- a site configuration change
        # cleared the /api/... login loop where iisreset didn't.
        Import-IisModule
        Restart-BlueTrackSite -SiteName $SiteName
    }
    'Smoke'           = {
        Start-Sleep -Seconds 5 # give the app pool a moment to warm up after recycling
        $siteUrl = "https://$($Hostname):$HttpsPort"
        if (Test-BlueTrackDeployment -SiteUrl $siteUrl) {
            return @{ Status = 'Completed'; Message = "Healthy at $siteUrl" }
        }
        return @{ Status = 'Failed'; Message = 'Did not confirm healthy (health checks or the browser /api/... path) -- see the warnings above' }
    }
}
#endregion

try {
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.State.psm1') -Force

    #region Load answers: command line, then -ConfigFile, then saved answers
    if (([bool]$Step) + ([bool]$StartAt) + ([bool]$Resume) -gt 1) {
        throw '-Step, -StartAt and -Resume are alternatives -- use only one.'
    }
    foreach ($name in $PSBoundParameters.Keys) { [void]$Answered.Add($name) }

    if ($ConfigFile) {
        Import-AnswerSource (Read-BlueTrackAnswerFile -Path $ConfigFile)
    }

    # Which site's saved state to use: -SiteName (or the config file's) if
    # given, otherwise the only saved site, otherwise the default.
    if (-not $Answered.Contains('SiteName')) {
        $stateDir = Join-Path $DeployRoot 'State'
        $savedSites = @(Get-ChildItem -Path $stateDir -Filter 'answers.*.json' -File -ErrorAction SilentlyContinue)
        if ($savedSites.Count -eq 1) {
            $SiteName = $savedSites[0].Name -replace '^answers\.(.+)\.json$', '$1'
        } elseif ($savedSites.Count -gt 1 -and ($Resume -or $Step -or $StartAt -or $ListSteps)) {
            throw "Saved state exists for more than one site ($(($savedSites | ForEach-Object { $_.Name -replace '^answers\.(.+)\.json$', '$1' }) -join ', ')). Pass -SiteName to pick one."
        }
    }
    $AnswersPath = Get-BlueTrackStatePath -DeployRoot $DeployRoot -SiteName $SiteName -Kind Answers
    $ProgressPath = Get-BlueTrackStatePath -DeployRoot $DeployRoot -SiteName $SiteName -Kind Progress

    $partialRun = [bool]($Resume -or $Step -or $StartAt)
    if ($partialRun -and (Test-Path $AnswersPath)) {
        Write-Host "Using saved answers from $AnswersPath"
        Import-AnswerSource (Read-BlueTrackAnswerFile -Path $AnswersPath)
    }
    $Progress = Read-BlueTrackProgress -Path $ProgressPath
    #endregion

    #region -ListSteps
    if ($ListSteps) {
        $Steps | ForEach-Object {
            $saved = if ($Progress.ContainsKey($_.Name)) { $Progress[$_.Name] } else { $null }
            [pscustomobject]@{
                Step        = $_.Name
                Description = $_.Description
                Status      = if ($saved) { $saved.Status } else { '' }
                When        = if ($saved) { $saved.Time } else { '' }
                Message     = if ($saved) { $saved.Message } else { '' }
            }
        } | Format-Table -AutoSize | Out-Host
        Write-Host "Saved state: $ProgressPath"
        exit 0
    }
    #endregion

    #region Transcript
    $logsDir = Join-Path $DeployRoot 'Logs'
    if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir -WhatIf:$false | Out-Null }
    $transcriptPath = Join-Path $logsDir "Install-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
    Start-Transcript -Path $transcriptPath -WhatIf:$false | Out-Null
    Write-Host "Logging this run to $transcriptPath"
    #endregion

    #region Select steps
    if ($Step) {
        $selected = @($Steps | Where-Object { $name = $_.Name; @($Step | Where-Object { $name -like $_ }).Count -gt 0 })
        $unmatched = @($Step | Where-Object { $pattern = $_; @($StepNames | Where-Object { $_ -like $pattern }).Count -eq 0 })
        if ($unmatched) { throw "Unknown step(s): $($unmatched -join ', '). Run with -ListSteps to see the step names." }
    } elseif ($StartAt) {
        $startIndex = [array]::IndexOf($StepNames, ($StepNames | Where-Object { $_ -eq $StartAt } | Select-Object -First 1))
        if ($startIndex -lt 0) { throw "Unknown step '$StartAt'. Run with -ListSteps to see the step names." }
        $selected = @($Steps[$startIndex..($Steps.Count - 1)])
    } elseif ($Resume) {
        if ($Progress.Count -eq 0) { throw "Nothing to resume: no saved progress at $ProgressPath. Run without -Resume to start an install." }
        $selected = @($Steps | Where-Object { -not ($Progress.ContainsKey($_.Name) -and $Progress[$_.Name].Status -in 'Completed', 'NotApplicable') })
        if ($selected.Count -eq 0) { Write-Host 'Every step has already completed. Use -Step or -StartAt to re-run specific steps.' }
    } else {
        # A fresh full run starts a fresh progress record.
        $selected = $Steps
        $Progress = @{}
        if ((Test-Path $ProgressPath) -and $PSCmdlet.ShouldProcess($ProgressPath, 'Clear saved progress from an earlier run')) {
            Remove-Item $ProgressPath
        }
    }
    Write-Host "Steps this run: $(($selected | ForEach-Object { $_.Name }) -join ', ')"
    #endregion

    #region Resolve every answer the selected steps need, then save them
    foreach ($s in $selected) {
        if ($s.ContainsKey('Needs')) {
            foreach ($need in $s.Needs) { Resolve-InstallAnswer $need }
        }
    }
    Save-InstallAnswer
    if (-not $WhatIfPreference) {
        Write-Host "Answers saved to $AnswersPath (re-run with -Resume to pick up from a failed step)."
    }
    #endregion

    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Prereqs.psm1') -Force
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Build.psm1') -Force
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Database.psm1') -Force
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Rollback.psm1') -Force
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Smoke.psm1') -Force

    if (@($selected | Where-Object { $_.ContainsKey('PrereqKey') }).Count -gt 0) {
        Write-Host "`n=== Prerequisites ===" -ForegroundColor Cyan
        Test-BlueTrackPrerequisite | Format-Table Name, Installed, Version, AutoInstallable, Detail -AutoSize | Out-Host
    }

    #region Run steps
    $runResults = @()
    foreach ($s in $selected) {
        Write-Host "`n=== Step: $($s.Name) ===" -ForegroundColor Cyan
        if ($s.ContainsKey('Condition') -and -not (& $s.Condition)) {
            Write-Host "Not applicable this run ($($s.SkipReason))."
            $result = @{ Status = 'NotApplicable'; Message = $s.SkipReason }
        } else {
            try {
                if ($s.ContainsKey('PrereqKey')) {
                    $result = Invoke-PrerequisiteStep -Key $s.PrereqKey -Explicit:([bool]$Step)
                } else {
                    $output = @(& $StepActions[$s.Name])
                    $result = $output | Where-Object { $_ -is [hashtable] -and $_.ContainsKey('Status') } | Select-Object -Last 1
                    if (-not $result) { $result = @{ Status = 'Completed'; Message = '' } }
                }
            } catch {
                Save-BlueTrackStepStatus -Path $ProgressPath -Progress $Progress -Step $s.Name -Status Failed -Message $_.Exception.Message
                Write-Host "`nStep $($s.Name) failed. Fix the cause, then re-run with -Resume (or -Step $($s.Name))." -ForegroundColor Yellow
                throw
            }
        }
        Save-BlueTrackStepStatus -Path $ProgressPath -Progress $Progress -Step $s.Name -Status $result.Status -Message $result.Message
        $runResults += [pscustomobject]@{ Step = $s.Name; Status = $result.Status; Message = $result.Message }
    }
    #endregion

    #region Summary
    Write-Host "`n=== Summary ===" -ForegroundColor Cyan
    if ($runResults) { $runResults | Format-Table -AutoSize -Wrap | Out-Host }

    # Without this grant every database-touching request fails with "Login
    # failed for user ..." (D-164, D-170, D-171), so say so here rather than
    # leave it to be found as a 500 later.
    # D-175: without a full IIS restart, browser sign-in through /api/... can
    # loop (401.1) after IIS changes even though the smoke test passes.
    if ($Progress.ContainsKey('Iis.Reset') -and $Progress['Iis.Reset'].Status -eq 'NotApplicable' -and
        @($runResults | Where-Object { $_.Step -like 'Iis.*' -and $_.Status -eq 'Completed' }).Count -gt 0) {
        Write-Warning "IIS settings changed this run but IIS wasn't restarted (ResetIis is off). If browser sign-in keeps asking for credentials, run iisreset (it briefly stops every site), or: .\Install-BlueTrack.ps1 -Step Iis.Reset -ResetIis `$true"
    }

    if ($UseWindowsAuth -and $Progress.ContainsKey('Db.AppPoolAccess') -and $Progress['Db.AppPoolAccess'].Status -eq 'NotApplicable') {
        $grantAccount = $AppPoolSqlLogin
        if (-not $grantAccount) {
            try { Import-IisModule; $grantAccount = Get-BlueTrackAppPoolSqlLogin -AppPoolName "$SiteName-AppPool" -SqlServerInstance $SqlServerInstance } catch { $grantAccount = "the app pool's account" }
        }
        Write-Warning "The app pool's SQL Server access was not granted (GrantAppPoolSqlAccess is off). Until $grantAccount has a SQL login, the site returns 500 ('Login failed for user ...'; the name in that message can differ from the account that needs the login -- D-171). Grant it with: .\Install-BlueTrack.ps1 -Step Db.AppPoolAccess -GrantAppPoolSqlAccess `$true"
    }

    $outstanding = @($Steps | Where-Object { -not ($Progress.ContainsKey($_.Name) -and $Progress[$_.Name].Status -in 'Completed', 'NotApplicable') })
    if ($outstanding.Count -gt 0) {
        Write-Host "Not yet completed: $(($outstanding | ForEach-Object { $_.Name }) -join ', ')" -ForegroundColor Yellow
        Write-Host 'Run with -Resume to continue, or -ListSteps to see each step''s status.'
    } else {
        Write-Host "Site:              https://$($Hostname):$HttpsPort  (site '$SiteName', app pool '$SiteName-AppPool')"
        Write-Host "Database:          $DatabaseName on $SqlServerInstance"
        Write-Host "Environment:       $Environment"
        Write-Host "`nManual follow-ups this script deliberately does NOT do (see User_Docs/Admin_DeploymentRunbook.md):"
        Write-Host '  - Replace the bootstrap BUILTIN\Administrators admin mapping with a real AD/Entra group.'
        Write-Host '  - Configure a real identity provider (SAML/OIDC) if not using Windows Integrated auth.'
        Write-Host '  - Cut over the Secrets Store backend from Windows DPAPI if a different backend is intended.'
        Write-Host '  - Load real CyberArk export data (see the Deployment Runbook''s "First Data Load" section).'
    }
    Write-Host "`nFull log: $transcriptPath"
    #endregion

    Stop-Transcript | Out-Null
} catch {
    Write-Host "`nInstall failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($transcriptPath) {
        Write-Host "See $transcriptPath for the full log."
        Stop-Transcript | Out-Null
    }
    exit 1
}
