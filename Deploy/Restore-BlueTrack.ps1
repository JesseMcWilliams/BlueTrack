<#
.SYNOPSIS
    Emergency restore of BlueTrack (and, optionally, msdb) from a specific
    backup file produced by Backup-BlueTrack.ps1 (or Install-BlueTrack.ps1's
    own pre-deployment backup phase).

.DESCRIPTION
    Deployment_Methodology's rollback mechanism (Option B). Destructive --
    RESTORE DATABASE ... WITH REPLACE overwrites the target database's
    current contents. Requires an explicit -BackupFilePath: there is no
    "latest in folder" auto-detection, since silently picking the wrong
    backup is worse than requiring the operator to name it.

    This only ever handles the database side of a rollback. Also redeploy
    the application build that matches the backup's manifest (see its
    GitCommit field, if one was recorded) so the app and database stay in
    sync -- this script does not touch the deployed application at all.

.EXAMPLE
    .\Restore-BlueTrack.ps1 -SqlServerInstance localhost -DatabaseName BlueTrackTest -BackupFilePath D:\Backups\BlueTrack\BlueTrackTest_20260916-020000.bak
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)] [string]$SqlServerInstance,
    [string]$DatabaseName = 'BlueTrack',
    [bool]$UseWindowsAuth = $true,
    [pscredential]$SqlCredential,
    [Parameter(Mandatory)] [string]$BackupFilePath,
    [string]$RestoreMsdbFilePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

if (-not $PSBoundParameters.ContainsKey('Confirm')) {
    $confirmation = Read-Host "This will REPLACE the current contents of '$DatabaseName' on '$SqlServerInstance' with '$BackupFilePath'. Type YES to continue"
    if ($confirmation -ne 'YES') {
        Write-Host 'Aborted -- no changes made.'
        exit 0
    }
}

Restore-BlueTrackFromRollback -ConnectionString $connectionString -DatabaseName $DatabaseName `
    -BackupFilePath $BackupFilePath -RestoreMsdbFilePath $RestoreMsdbFilePath -Confirm:$false
