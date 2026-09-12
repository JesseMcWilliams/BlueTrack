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
        for a fresh (or fresh-ish) environment, but safe to re-run: each phase
        checks current state before acting rather than failing or duplicating.

    Every value below can be supplied as a parameter, via -ConfigFile (a JSON
    file with the same property names), or left blank to be prompted for
    interactively. Nothing credential-like is ever prompted as plain text.

.PARAMETER ConfigFile
    Path to a JSON file supplying any of this script's other parameters, for
    unattended/repeatable runs. Explicit command-line parameters take
    precedence over the config file for the same value.

.EXAMPLE
    .\Install-BlueTrack.ps1 -Environment Test -SqlServerInstance localhost `
        -SiteName BlueTrackTest -Hostname bluetrack-test.company.com -GenerateSelfSignedCert

.EXAMPLE
    .\Install-BlueTrack.ps1 -ConfigFile .\answers.prod.json -WhatIf
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

    # --- IIS ---
    [string]$SiteName = 'BlueTrack',
    [string]$Hostname,
    [int]$HttpPort = 80,
    [int]$HttpsPort = 443,
    [string]$CertificateThumbprint,
    [switch]$GenerateSelfSignedCert,

    # --- Behavior ---
    [switch]$Force,
    [switch]$SkipPrerequisiteInstall,
    [switch]$SkipSmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DeployRoot = $PSScriptRoot

#region Elevation check
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Error 'This script must be run from an elevated (Run as Administrator) PowerShell session -- it installs Windows features, IIS sites, and SQL Agent jobs, none of which work otherwise. Re-launch PowerShell as Administrator and try again.'
    exit 1
}
#endregion

#region Transcript
$logsDir = Join-Path $DeployRoot 'Logs'
if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir | Out-Null }
$transcriptPath = Join-Path $logsDir "Install-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
Start-Transcript -Path $transcriptPath | Out-Null
Write-Host "Logging this run to $transcriptPath"
#endregion

try {
    #region Load config file, then prompt for anything still missing
    if ($ConfigFile) {
        if (-not (Test-Path $ConfigFile)) { throw "Config file '$ConfigFile' not found." }
        $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json
        foreach ($property in $config.PSObject.Properties) {
            # Explicit command-line parameters win over the config file --
            # only fill in values the caller didn't already supply.
            if (-not $PSBoundParameters.ContainsKey($property.Name)) {
                Set-Variable -Name $property.Name -Value $property.Value
            }
        }
    }

    if (-not $Environment) {
        $Environment = Read-Host 'Environment (Development/Test/Staging/Production)'
    }
    if (-not $SqlServerInstance) {
        $SqlServerInstance = Read-Host 'SQL Server instance (e.g. localhost, SERVER\INSTANCE)'
    }
    if (-not $UseWindowsAuth -and -not $SqlCredential) {
        $SqlCredential = Get-Credential -Message 'SQL Server login for BlueTrack (Windows Integrated Security was declined)'
    }
    if (-not $Hostname) {
        $Hostname = Read-Host 'Site hostname (e.g. bluetrack.company.com)'
    }
    if (-not $PSBoundParameters.ContainsKey('SeedTestData') -and $Environment -ne 'Production') {
        $SeedTestData = (Read-Host "Seed DevFakeAuth/synthetic test data (Database/Test)? Never do this for Production. (y/N)") -match '^[Yy]'
    }
    if (-not $PSBoundParameters.ContainsKey('InstallNightlyJob')) {
        $InstallNightlyJob = (Read-Host 'Install the nightly Import+Load SQL Agent job now? (y/N)') -match '^[Yy]'
    }
    if ($InstallNightlyJob) {
        if (-not $ExportFolderPath) { $ExportFolderPath = Read-Host 'Privilege Cloud CSV export folder path (local to the SQL Server service account)' }
        if (-not $EvdDatabaseName) { $EvdDatabaseName = Read-Host 'Self-Hosted EVD database name (same SQL Server instance)' }
    }
    if (-not $CertificateThumbprint -and -not $GenerateSelfSignedCert) {
        $certChoice = Read-Host "HTTPS certificate: enter a thumbprint, or leave blank to generate a self-signed cert for '$Hostname' (Dev/Test only)"
        if ($certChoice) { $CertificateThumbprint = $certChoice } else { $GenerateSelfSignedCert = $true }
    }
    #endregion

    # BlueTrack.Iis.psm1 is NOT imported here -- it #Requires the
    # WebAdministration module, which only exists once IIS itself is
    # installed. On a genuinely fresh box that's exactly what the
    # Prerequisites phase below might still need to install, so importing
    # BlueTrack.Iis.psm1 has to wait until right before the IIS phase runs,
    # after that's confirmed -- otherwise this Import-Module call would fail
    # before ever getting a chance to install IIS.
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Prereqs.psm1') -Force
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Build.psm1') -Force
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Database.psm1') -Force
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Smoke.psm1') -Force

    #region Phase: Prerequisites
    Write-Host "`n=== Phase: Prerequisites ===" -ForegroundColor Cyan
    $prereqResults = Test-BlueTrackPrerequisite
    $prereqResults | Format-Table -AutoSize | Out-Host

    foreach ($item in $prereqResults) {
        if ($item.Installed -eq $false -and $item.AutoInstallable) {
            if ($SkipPrerequisiteInstall) {
                Write-Warning "$($item.Name) is missing and -SkipPrerequisiteInstall was set -- the install will likely fail later."
                continue
            }
            $answer = Read-Host "$($item.Name) is missing. Install it now? (y/N)"
            if ($answer -match '^[Yy]') {
                Install-BlueTrackPrerequisite -Name $item.Name
            } else {
                Write-Warning "Skipping $($item.Name) -- the install may fail later without it."
            }
        } elseif ($item.Installed -eq $false) {
            Write-Warning "$($item.Name): $($item.Detail)"
        }
    }
    #endregion

    #region Phase: Build
    Write-Host "`n=== Phase: Build ===" -ForegroundColor Cyan
    $publishDir = Join-Path $env:TEMP "BlueTrack-publish-$Environment"
    Publish-BlueTrackApi -RepoRoot $RepoRoot -OutputDirectory $publishDir
    $spaDist = Invoke-BlueTrackWebBuild -RepoRoot $RepoRoot
    #endregion

    #region Phase: Database
    Write-Host "`n=== Phase: Database ===" -ForegroundColor Cyan
    $connectionString = Format-BlueTrackConnectionString -SqlServerInstance $SqlServerInstance -DatabaseName $DatabaseName `
        -UseWindowsAuth $UseWindowsAuth -SqlCredential $SqlCredential

    if (-not (Test-BlueTrackSqlConnection -ConnectionString $connectionString)) {
        throw "Could not connect to SQL Server instance '$SqlServerInstance'. This script does not install SQL Server itself -- confirm the instance is running and reachable, then re-run."
    }
    Write-Host 'SQL Server connectivity confirmed.'

    Invoke-BlueTrackMigrator -RepoRoot $RepoRoot -ConnectionString $connectionString -ScriptsFolder 'Database'
    if ($SeedTestData) {
        Invoke-BlueTrackMigrator -RepoRoot $RepoRoot -ConnectionString $connectionString -ScriptsFolder 'Database/Test'
    }

    Set-BlueTrackEnvironmentConfig -PublishDirectory $publishDir -Environment $Environment -ConnectionString $connectionString

    if ($InstallNightlyJob) {
        Install-BlueTrackNightlyJob -RepoRoot $RepoRoot -SqlServerInstance $SqlServerInstance -DatabaseName $DatabaseName `
            -ExportFolderPath $ExportFolderPath -EvdDatabaseName $EvdDatabaseName
    }
    #endregion

    #region Phase: IIS
    Write-Host "`n=== Phase: IIS ===" -ForegroundColor Cyan
    # Imported here, not with the other modules above -- see the comment
    # where those were imported for why (needs IIS's WebAdministration
    # module, which the Prerequisites phase may have only just installed).
    Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Iis.psm1') -Force

    $appPoolName = "$SiteName-AppPool"
    New-BlueTrackAppPool -Name $appPoolName -Force:$Force

    $certThumbprint = New-BlueTrackCertificateBinding -CertificateThumbprint $CertificateThumbprint -Hostname $Hostname -GenerateSelfSigned:$GenerateSelfSignedCert

    New-BlueTrackSite -SiteName $SiteName -AppPoolName $appPoolName -SpaPhysicalPath $spaDist -ApiPhysicalPath $publishDir `
        -Hostname $Hostname -HttpPort $HttpPort -HttpsPort $HttpsPort -CertificateThumbprint $certThumbprint -Force:$Force

    Set-BlueTrackSiteWebConfig -RepoRoot $RepoRoot -SpaPhysicalPath $spaDist
    Restart-BlueTrackAppPool -Name $appPoolName
    #endregion

    #region Phase: Smoke test
    $smokeTestPassed = $null
    if (-not $SkipSmokeTest) {
        Write-Host "`n=== Phase: Smoke test ===" -ForegroundColor Cyan
        Start-Sleep -Seconds 5 # give the app pool a moment to warm up after recycling
        $siteUrl = "https://$($Hostname):$HttpsPort"
        $smokeTestPassed = Test-BlueTrackDeployment -SiteUrl $siteUrl
    }
    #endregion

    #region Summary
    Write-Host "`n=== Summary ===" -ForegroundColor Cyan
    Write-Host "Site:              https://$($Hostname):$HttpsPort  (site '$SiteName', app pool '$appPoolName')"
    Write-Host "Database:          $DatabaseName on $SqlServerInstance"
    Write-Host "Environment:       $Environment"
    Write-Host "Nightly job:       $(if ($InstallNightlyJob) { 'installed' } else { 'not installed (run manually later if needed)' })"
    if (-not $SkipSmokeTest) {
        Write-Host "Smoke test:        $(if ($smokeTestPassed) { 'passed' } else { 'did not confirm healthy -- check the Deployment Info admin page manually' })"
    }
    Write-Host "`nManual follow-ups this script deliberately does NOT do (see Design Documents/Design_Deployment_Runbook.md):"
    Write-Host '  - Replace the bootstrap BUILTIN\Administrators admin mapping with a real AD/Entra group.'
    Write-Host '  - Configure a real identity provider (SAML/OIDC) if not using Windows Integrated auth.'
    Write-Host '  - Cut over the Secrets Store backend from Windows DPAPI if a different backend is intended.'
    Write-Host '  - Load real CyberArk export data (see the Deployment Runbook''s "First Data Load" section).'
    Write-Host "`nFull log: $transcriptPath"
    #endregion

    Stop-Transcript | Out-Null
} catch {
    Write-Host "`nInstall failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "See $transcriptPath for the full log."
    Stop-Transcript | Out-Null
    exit 1
}

Stop-Transcript | Out-Null
