<#
.SYNOPSIS
    Downloads a BlueTrack version from GitHub -- the latest release, the
    main branch, or a branch you pick -- into the download folder, then
    offers to redeploy from it with this site's saved installer answers.

.DESCRIPTION
    D-192. Uses only PowerShell and HTTPS to github.com (the repository's
    source zip through GitHub's public API); git isn't needed on the server.

    By default it downloads the latest GitHub release. If the repository has
    no release yet, it says so and offers main, a branch from a list, or
    cancel. -Branch and -ListBranches choose a branch directly; -Release a
    specific release tag.

    Each download is extracted to its own folder under the download folder,
    e.g. BlueTrack-v1.2.0 or BlueTrack-main-20261009-2030, so earlier
    versions stay available for rollback (delete old ones yourself). The
    folder records what was downloaded in download.json.

    The download folder is the UpdateDownloadFolder answer in this site's
    saved answers (Deploy\State\answers.<SiteName>.json). If it isn't set,
    -DownloadFolder sets it, or the script asks once; either way it's saved.

    Afterwards it copies the saved answers into the new copy's Deploy\State
    and asks whether to run that copy's Install-BlueTrack.ps1 with them (a
    full redeploy, which includes the pre-deployment backup). -RunInstaller
    skips the question.

.PARAMETER SiteName
    Which site's saved answers to use. Not needed when Deploy\State holds
    only one site.
.PARAMETER Release
    A release tag to download instead of the latest release.
.PARAMETER Branch
    A branch to download (e.g. main) instead of a release.
.PARAMETER ListBranches
    List the repository's branches and choose one.
.PARAMETER DownloadFolder
    Where to put downloads; saved as UpdateDownloadFolder for next time.
.PARAMETER RunInstaller
    Run the downloaded copy's installer without asking.
.PARAMETER Repository
    The GitHub repository, owner/name.

.EXAMPLE
    .\Update-BlueTrack.ps1
    Latest release (or, with none, a choice of main or a branch).
.EXAMPLE
    .\Update-BlueTrack.ps1 -Branch main
.EXAMPLE
    .\Update-BlueTrack.ps1 -ListBranches
.EXAMPLE
    .\Update-BlueTrack.ps1 -Release v1.2.0 -RunInstaller
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Release')]
param(
    [string]$SiteName,
    [Parameter(ParameterSetName = 'Release')] [string]$Release,
    [Parameter(Mandatory, ParameterSetName = 'Branch')] [string]$Branch,
    [Parameter(Mandatory, ParameterSetName = 'ListBranches')] [switch]$ListBranches,
    [string]$DownloadFolder,
    [switch]$RunInstaller,
    [string]$Repository = 'JesseMcWilliams/BlueTrack'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 may not offer TLS 1.2 by default; GitHub requires it.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$DeployRoot = $PSScriptRoot
$ApiRoot = "https://api.github.com/repos/$Repository"
$Headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'BlueTrack-Update' }

#region Saved answers: which site, and the download folder
$stateDir = Join-Path $DeployRoot 'State'
if (-not $SiteName) {
    $saved = @(Get-ChildItem -Path $stateDir -Filter 'answers.*.json' -File -ErrorAction SilentlyContinue)
    if ($saved.Count -eq 0) {
        throw "No saved installer answers in $stateDir. Run Install-BlueTrack.ps1 once first, or pass -SiteName."
    }
    if ($saved.Count -gt 1) {
        throw "Saved answers exist for more than one site ($(($saved | ForEach-Object { $_.Name -replace '^answers\.(.+)\.json$', '$1' }) -join ', ')). Pass -SiteName."
    }
    $SiteName = $saved[0].Name -replace '^answers\.(.+)\.json$', '$1'
}
$answersPath = Join-Path $stateDir "answers.$SiteName.json"
if (-not (Test-Path $answersPath)) {
    throw "No saved answers for site '$SiteName' ($answersPath)."
}
$answers = Get-Content -Path $answersPath -Raw | ConvertFrom-Json

$savedFolder = if ($answers.PSObject.Properties['UpdateDownloadFolder']) { $answers.UpdateDownloadFolder } else { $null }
if (-not $DownloadFolder) { $DownloadFolder = $savedFolder }
if (-not $DownloadFolder) {
    $suggested = Join-Path $env:SystemDrive 'BlueTrack\Downloads'
    $entered = Read-Host "Download folder for BlueTrack versions [$suggested]"
    $DownloadFolder = if ($entered) { $entered } else { $suggested }
}
if ($DownloadFolder -ne $savedFolder -and $PSCmdlet.ShouldProcess($answersPath, "Save UpdateDownloadFolder = $DownloadFolder")) {
    $answers | Add-Member -NotePropertyName UpdateDownloadFolder -NotePropertyValue $DownloadFolder -Force
    $answers | ConvertTo-Json -Depth 5 | Set-Content -Path $answersPath -Encoding UTF8
    Write-Host "Saved the download folder in $answersPath."
}
#endregion

#region What to download
function Get-GitHubJson {
    param([Parameter(Mandatory)] [string]$Uri)
    Invoke-RestMethod -Uri $Uri -Headers $Headers -UseBasicParsing
}

function Select-BlueTrackBranch {
    $branches = @(Get-GitHubJson "$ApiRoot/branches?per_page=100" | ForEach-Object { $_.name } | Sort-Object)
    if ($branches.Count -eq 0) { throw "The repository $Repository has no branches visible here." }
    Write-Host "Branches of ${Repository}:"
    for ($i = 0; $i -lt $branches.Count; $i++) { Write-Host ("  {0,3}. {1}" -f ($i + 1), $branches[$i]) }
    $choice = Read-Host 'Number of the branch to download (blank to cancel)'
    if (-not $choice) { return $null }
    $index = 0
    if (-not [int]::TryParse($choice, [ref]$index) -or $index -lt 1 -or $index -gt $branches.Count) {
        throw "'$choice' isn't one of the numbers listed."
    }
    return $branches[$index - 1]
}

$ref = $null        # what to download: a tag or a branch name
$label = $null      # how the folder is named
switch ($PSCmdlet.ParameterSetName) {
    'Branch' { $ref = $Branch }
    'ListBranches' { if ($ListBranches) { $ref = Select-BlueTrackBranch } }
    default {
        $releaseInfo = $null
        try {
            $releaseInfo = if ($Release) { Get-GitHubJson "$ApiRoot/releases/tags/$Release" } else { Get-GitHubJson "$ApiRoot/releases/latest" }
        } catch {
            $status = $null
            if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
            if ($status -ne 404) { throw }
        }
        if ($releaseInfo) {
            $ref = $releaseInfo.tag_name
            $label = $releaseInfo.tag_name
            Write-Host "Release $($releaseInfo.tag_name) ($($releaseInfo.name)), published $($releaseInfo.published_at)."
        } elseif ($Release) {
            throw "There's no release '$Release' in $Repository."
        } else {
            $defaultBranch = (Get-GitHubJson $ApiRoot).default_branch
            Write-Host "$Repository has no releases yet."
            Write-Host "  1. Download $defaultBranch (the default branch)"
            Write-Host '  2. Choose a branch from the list'
            Write-Host '  3. Cancel'
            switch (Read-Host 'Choice') {
                '1' { $ref = $defaultBranch }
                '2' { $ref = Select-BlueTrackBranch }
                default { $ref = $null }
            }
        }
    }
}
if (-not $ref) {
    Write-Host 'Nothing downloaded.'
    return
}
if (-not $label) { $label = "$($ref -replace '[^A-Za-z0-9._-]', '-')-$(Get-Date -Format 'yyyyMMdd-HHmm')" }
#endregion

#region Download and extract
$target = Join-Path $DownloadFolder "BlueTrack-$label"
if (Test-Path $target) {
    throw "$target already exists -- that version was downloaded before. Delete or rename it to download again."
}
if (-not $PSCmdlet.ShouldProcess($target, "Download $Repository at '$ref' and extract it")) { return }

New-Item -ItemType Directory -Path $DownloadFolder -Force | Out-Null
$zip = Join-Path $DownloadFolder "BlueTrack-$label.zip"
$staging = Join-Path $DownloadFolder "BlueTrack-$label.extracting"
try {
    Write-Host "Downloading $Repository at '$ref'..."
    Invoke-WebRequest -Uri "$ApiRoot/zipball/$([uri]::EscapeDataString($ref))" -Headers $Headers -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $staging -Force
    # GitHub's zip holds one top folder, <owner>-<repo>-<short commit>.
    $inner = @(Get-ChildItem -Path $staging -Directory)
    if ($inner.Count -ne 1) { throw "Unexpected zip layout: expected one top-level folder, found $($inner.Count)." }
    $commit = ($inner[0].Name -split '-')[-1]
    Move-Item -Path $inner[0].FullName -Destination $target
    [pscustomobject]@{
        Repository   = $Repository
        Ref          = $ref
        Commit       = $commit
        DownloadedAt = (Get-Date).ToString('o')
        DownloadedBy = "$env:USERDOMAIN\$env:USERNAME"
    } | ConvertTo-Json | Set-Content -Path (Join-Path $target 'download.json') -Encoding UTF8
} finally {
    Remove-Item -Path $zip, $staging -Recurse -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path (Join-Path $target 'Deploy\Install-BlueTrack.ps1'))) {
    throw "$target doesn't contain Deploy\Install-BlueTrack.ps1 -- is '$ref' a BlueTrack version?"
}
Write-Host "Downloaded '$ref' (commit $commit) to $target." -ForegroundColor Green
#endregion

#region Offer to redeploy from it
$newState = Join-Path $target 'Deploy\State'
New-Item -ItemType Directory -Path $newState -Force | Out-Null
$newAnswers = Join-Path $newState "answers.$SiteName.json"
Copy-Item -Path $answersPath -Destination $newAnswers
Write-Host "Copied this site's saved answers to $newAnswers."

$installer = Join-Path $target 'Deploy\Install-BlueTrack.ps1'
$command = "& '$installer' -ConfigFile '$newAnswers' -SiteName '$SiteName'"
$run = $RunInstaller -or ((Read-Host "Redeploy site '$SiteName' from this version now? It runs the full installer with the saved answers, including the pre-deployment backup. (y/N)") -match '^(y|yes)$')
if ($run) {
    & $installer -ConfigFile $newAnswers -SiteName $SiteName
} else {
    Write-Host 'Not redeployed. To redeploy later, run:'
    Write-Host "  $command"
}
#endregion
