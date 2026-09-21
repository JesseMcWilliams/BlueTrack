<#
.SYNOPSIS
    Backs up BlueTrack (and, optionally, msdb) as a rollback point, outside
    of a full Install-BlueTrack.ps1 run -- e.g. immediately before a manual
    schema change or an emergency ad-hoc backup.

.DESCRIPTION
    Deployment_Methodology's rollback mechanism (Option B): formalize
    backup/restore rather than per-script "down" migrations. Verifies the
    backup with RESTORE VERIFYONLY and writes a manifest recording what was
    backed up and which git commit it preceded, so a later Restore-BlueTrack.ps1
    run can be matched to the right build.

.EXAMPLE
    .\Backup-BlueTrack.ps1 -SqlServerInstance localhost -DatabaseName BlueTrack -BackupFolder D:\Backups\BlueTrack

.EXAMPLE
    .\Backup-BlueTrack.ps1 -SqlServerInstance localhost -DatabaseName BlueTrack -BackupFolder D:\Backups\BlueTrack -IncludeMsdb
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string]$SqlServerInstance,
    [string]$DatabaseName = 'BlueTrack',
    [bool]$UseWindowsAuth = $true,
    [pscredential]$SqlCredential,
    [Parameter(Mandatory)] [string]$BackupFolder,
    [switch]$IncludeMsdb
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DeployRoot = $PSScriptRoot

Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Database.psm1') -Force
Import-Module (Join-Path $DeployRoot 'Modules\BlueTrack.Rollback.psm1') -Force

if (-not $UseWindowsAuth -and -not $SqlCredential) {
    $SqlCredential = Get-Credential -Message "SQL Server login for $DatabaseName"
}

$connectionString = Format-BlueTrackConnectionString -SqlServerInstance $SqlServerInstance -DatabaseName $DatabaseName `
    -UseWindowsAuth $UseWindowsAuth -SqlCredential $SqlCredential

if (-not (Test-BlueTrackSqlConnection -ConnectionString $connectionString)) {
    throw "Could not connect to SQL Server instance '$SqlServerInstance'."
}

$result = Backup-BlueTrackForRollback -ConnectionString $connectionString -DatabaseName $DatabaseName `
    -BackupFolder $BackupFolder -IncludeMsdb:$IncludeMsdb -RepoRoot $RepoRoot

Write-Host "`nDatabase backup: $($result.DatabaseBackupPath)"
if ($result.MsdbBackupPath) {
    Write-Host "msdb backup:     $($result.MsdbBackupPath)"
}
Write-Host "Manifest:        $($result.ManifestPath)"
