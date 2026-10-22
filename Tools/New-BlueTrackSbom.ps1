<#
.SYNOPSIS
    Writes BlueTrack's software bills of materials (SBOMs), CycloneDX JSON, to Sbom/.
.DESCRIPTION
    One file per part that is installed on a server:
        Sbom/BlueTrack.Api.cdx.json       App/Api: the NuGet packages it uses, and theirs
        Sbom/BlueTrack.Migrator.cdx.json  App/Migrator (run by the installer): the same
        Sbom/BlueTrack.Web.cdx.json       App/Web: the npm packages in the built site
                                          (build-only packages such as Vite left out)
    Each lists every package with its version, package URL, licence and hashes
    (the npm one from package-lock.json, as the lock file pins them).

    The tools are pinned: CycloneDX for .NET as a local tool in
    dotnet-tools.json (restored here with dotnet tool restore), and
    @cyclonedx/cyclonedx-npm at the version below (run with npx).
    Tools\New-BlueTrackRelease.ps1 runs this on the release branch with the
    release version; run it by hand to refresh the files between releases.
.PARAMETER Version
    The version written into each SBOM's metadata (default: <Version> in
    App/Api/BlueTrack.Api.csproj).
.PARAMETER OutputFolder
    Where to write the files (default: Sbom at the repository root).
.EXAMPLE
    .\New-BlueTrackSbom.ps1
.EXAMPLE
    .\New-BlueTrackSbom.ps1 -Version 1.6.0
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-+].*)?$')] [string]$Version,
    [string]$OutputFolder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Pinned (2026-10-21): published 2026-08-11, Apache-2.0. The .NET tool's version is in dotnet-tools.json.
$NpmSbomTool = '@cyclonedx/cyclonedx-npm@6.0.1'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputFolder) { $OutputFolder = Join-Path $repoRoot 'Sbom' }
if (-not $Version) {
    $csproj = Get-Content -Raw -Path (Join-Path $repoRoot 'App/Api/BlueTrack.Api.csproj')
    if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw 'No <Version> in App/Api/BlueTrack.Api.csproj; pass -Version.' }
    $Version = $Matches[1].Trim()
}

function Invoke-SbomTool {
    param([Parameter(Mandatory)] [string]$FilePath, [Parameter(Mandatory)] [string[]]$Arguments, [Parameter(Mandatory)] [string]$WorkingDirectory)
    Push-Location $WorkingDirectory
    try {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
        $ErrorActionPreference = $previous
        if ($code -ne 0) { throw "$FilePath $($Arguments -join ' ') failed (exit code $code): $($output -join ' ')" }
    } finally {
        Pop-Location
    }
}

if (-not $PSCmdlet.ShouldProcess($OutputFolder, "Write the CycloneDX SBOMs for version $Version")) { return }
if (-not (Test-Path $OutputFolder)) { [void](New-Item -ItemType Directory -Path $OutputFolder) }

Invoke-SbomTool -FilePath 'dotnet' -Arguments @('tool', 'restore') -WorkingDirectory $repoRoot

foreach ($project in @(
        @{ Name = 'BlueTrack.Api'; Path = 'App/Api/BlueTrack.Api.csproj' },
        @{ Name = 'BlueTrack.Migrator'; Path = 'App/Migrator/BlueTrack.Migrator.csproj' })) {
    # CycloneDX 1.6, as the npm tool writes; -ed: no development-only packages; -ns: no random serial number.
    Invoke-SbomTool -FilePath 'dotnet' -WorkingDirectory $repoRoot -Arguments @(
        'CycloneDX', (Join-Path $repoRoot $project.Path), '-o', $OutputFolder, '-fn', "$($project.Name).cdx.json",
        '-F', 'Json', '-spv', '1.6', '-ed', '-ns', '-sn', $project.Name, '-sv', $Version)
    Write-Host "Wrote $($project.Name).cdx.json"
}

# The built site's packages only (--omit dev), from the lock file (no node_modules needed), without timestamps or
# random serial numbers (--output-reproducible).
$webFile = Join-Path $OutputFolder 'BlueTrack.Web.cdx.json'
Invoke-SbomTool -FilePath 'npx' -WorkingDirectory (Join-Path $repoRoot 'App/Web') -Arguments @(
    '--yes', $NpmSbomTool, '--package-lock-only', '--omit', 'dev', '--output-reproducible', '--sv', '1.6', '--of', 'JSON', '-o', $webFile)

# The npm tool names the main component after package.json (bluetrack-web 0.1.0); give it the product's name and version.
$web = Get-Content -Raw -Path $webFile -Encoding UTF8 | ConvertFrom-Json
$web.metadata.component.name = 'BlueTrack.Web'
$web.metadata.component.version = $Version
[System.IO.File]::WriteAllText($webFile, ($web | ConvertTo-Json -Depth 100), (New-Object System.Text.UTF8Encoding($false)))
Write-Host 'Wrote BlueTrack.Web.cdx.json'
