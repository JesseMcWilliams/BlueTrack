<#
.SYNOPSIS
    Drops and rebuilds a developer's own test database, so local test runs
    don't collide with CI's BlueTrackTest (D-240).
.DESCRIPTION
    Does what the CI steps do (.github/workflows/ci.yml), against another
    database: drops it if it exists, builds the schema from Database/ with
    App/Migrator, then adds the test-only seed from Database/Test/.

    The name must start with BlueTrackTest_ (default BlueTrackTest_Local).
    BlueTrack (the real database) and BlueTrackTest (CI's) are refused.
    Never run Database/Test against a real environment.

    The API tests and the end-to-end tests use this database when
    BLUETRACK_TEST_CONNECTION isn't set. CI sets it explicitly, so CI is
    not affected.
.PARAMETER SqlServerInstance
    The SQL Server to build on (default: this computer).
.PARAMETER DatabaseName
    The database to drop and rebuild (default BlueTrackTest_Local).
.EXAMPLE
    .\Reset-BlueTrackLocalTestDb.ps1
.EXAMPLE
    .\Reset-BlueTrackLocalTestDb.ps1 -SqlServerInstance WIN-K5POLANERI5.Company.com -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SqlServerInstance = $env:COMPUTERNAME,
    [string]$DatabaseName = 'BlueTrackTest_Local'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($DatabaseName -notmatch '^BlueTrackTest_[A-Za-z0-9_]+$') {
    throw "Refusing database '$DatabaseName': the name must match BlueTrackTest_<suffix>. BlueTrack is the real database and BlueTrackTest is CI's."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$connection = "Server=$SqlServerInstance;Database=$DatabaseName;Integrated Security=true;TrustServerCertificate=true"

if ($PSCmdlet.ShouldProcess("$SqlServerInstance / $DatabaseName", 'Drop and rebuild the test database')) {
    Write-Host "Dropping $DatabaseName on $SqlServerInstance (if it exists)" -ForegroundColor Cyan
    $drop = "IF DB_ID('$DatabaseName') IS NOT NULL BEGIN ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DatabaseName]; END"
    & sqlcmd -S $SqlServerInstance -C -b -Q $drop | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed dropping $DatabaseName (exit code $LASTEXITCODE)." }

    Push-Location $repoRoot
    try {
        foreach ($folder in 'Database', 'Database/Test') {
            Write-Host "Applying $folder" -ForegroundColor Cyan
            & dotnet run --project App/Migrator --configuration Release -- $connection $folder | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Migrator failed on $folder (exit code $LASTEXITCODE)." }
        }
    } finally {
        Pop-Location
    }

    Write-Host "Done. $DatabaseName is ready." -ForegroundColor Green
    if ($DatabaseName -ne 'BlueTrackTest_Local') {
        Write-Host "Set BLUETRACK_TEST_CONNECTION to use it:`n  $connection"
    }
}
