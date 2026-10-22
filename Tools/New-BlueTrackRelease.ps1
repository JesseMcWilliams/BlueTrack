<#
.SYNOPSIS
    Makes a BlueTrack release in two steps: a release branch and pull request,
    then, after it's merged, the tag (D-226).
.DESCRIPTION
    Step 1, prepare (the default):
        .\New-BlueTrackRelease.ps1 -Version 1.1.0 [-Date 2026-10-20] [-Push]
      Checks that you're on main, the working tree is clean, main matches
      origin/main, the version is x.y.z and greater than the latest v* tag,
      neither the tag nor the release branch exists yet, and Release Notes'
      Unreleased section lists something. Then, on a new branch
      YYYY-MM-DD-release-v<Version>:
        - sets <Version> in App/Api/BlueTrack.Api.csproj;
        - in User_Docs/Release_Notes.md, renames "## Unreleased" to
          "## <Version> (<Date>)", drops its empty subsections, and starts a
          new, empty Unreleased section above it;
        - rebuilds the Published_Docs .docx files
          (python Published_Docs/build_published_docs.py, before the new empty
          Unreleased section is added, so the documents don't show it);
        - writes the CycloneDX SBOMs in Sbom/ for this version
          (Tools\New-BlueTrackSbom.ps1; needs dotnet and npx);
        - commits "Release v<Version>".
      With -Push it pushes the branch and opens a pull request into main with
      gh. If a step fails before the commit, the branch is removed and you're
      back on main as you were.

    Step 2, tag (after the pull request is merged):
        .\New-BlueTrackRelease.ps1 -Version 1.1.0 -Tag [-Push]
      Fetches origin, finds the commit on origin/main that set <Version> to
      this version (the merge, or the squashed commit), and creates the
      signed, annotated tag v<Version> on it (git tag -s, D-199: needs a
      signing key set up; Deploy\README.md, "Making a signed release").
      With -Push it pushes the tag, creates the GitHub release "BlueTrack
      v<Version>" with that version's Release Notes section as its text, and
      checks that GitHub verified the tag's signature (Update-BlueTrack.ps1
      refuses a release whose signature isn't verified).

    Both steps run git fetch (and step 1 runs python -c "import docx") even
    with -WhatIf; nothing else changes under -WhatIf.
.PARAMETER Version
    The release version, x.y.z (no leading v).
.PARAMETER Date
    The release date for the Release Notes heading, yyyy-MM-dd (default today).
.PARAMETER Push
    Push the branch and open the pull request (step 1), or push the tag and
    create the GitHub release (step 2).
.PARAMETER Tag
    Step 2: tag the merged release on origin/main.
.PARAMETER Remote
    The remote to check against and push to (default origin).
.EXAMPLE
    .\New-BlueTrackRelease.ps1 -Version 1.1.0 -WhatIf
.EXAMPLE
    .\New-BlueTrackRelease.ps1 -Version 1.1.0 -Push
.EXAMPLE
    .\New-BlueTrackRelease.ps1 -Version 1.1.0 -Tag -Push
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$Version,
    [Parameter(ParameterSetName = 'Prepare')] [ValidatePattern('^\d{4}-\d{2}-\d{2}$')] [string]$Date = (Get-Date -Format 'yyyy-MM-dd'),
    [Parameter(ParameterSetName = 'Tag', Mandatory)] [switch]$Tag,
    [switch]$Push,
    [string]$Remote = 'origin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$csprojRelative = 'App/Api/BlueTrack.Api.csproj'
$notesRelative = 'User_Docs/Release_Notes.md'
$csprojPath = Join-Path $repoRoot $csprojRelative
$notesPath = Join-Path $repoRoot $notesRelative
$tagName = "v$Version"

function Invoke-ReleaseGit {
    <# Runs git in the repository; returns its output lines, or throws with them when it fails (unless -AllowFailure). #>
    param([Parameter(Mandatory)] [string[]]$Arguments, [switch]$AllowFailure)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # git writes progress to stderr; Windows PowerShell would otherwise stop on it
    try {
        $output = @(& git -C $repoRoot @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed (exit code $code): $($output -join ' ')"
    }
    return , $output
}

function Invoke-ReleaseGh {
    <# Runs the GitHub CLI in the repository (it finds the GitHub repository from the remotes); returns the exit code and output lines. #>
    param([Parameter(Mandatory)] [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    Push-Location -LiteralPath $repoRoot
    try {
        $output = @(& gh @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{ Code = $code; Output = $output }
}

function Exit-Release {
    param([Parameter(Mandatory)] [string]$Message)
    Write-Host "FAIL  $Message" -ForegroundColor Red
    exit 1
}

function Write-Step {
    param([Parameter(Mandatory)] [string]$Message)
    Write-Host "PASS  $Message" -ForegroundColor Green
}

function Assert-GhReady {
    if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue)) { Exit-Release '-Push needs the GitHub CLI (gh) on PATH.' }
    if ((Invoke-ReleaseGh -Arguments @('auth', 'status')).Code -ne 0) { Exit-Release '-Push needs gh signed in (gh auth login).' }
    Write-Step 'gh is signed in'
}

function Read-TextFile {
    <# Reads a text file keeping what Set-ReleaseTextFile needs to write it back the same way: BOM and line ending. #>
    param([Parameter(Mandatory)] [string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($Path)
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    return [pscustomobject]@{ Text = $text; HasBom = $hasBom; Newline = $newline }
}

function Set-ReleaseTextFile {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$Text, [Parameter(Mandatory)] [bool]$HasBom)
    if ($PSCmdlet.ShouldProcess($Path, 'Write')) {
        [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($HasBom)))
    }
}

function Get-UnreleasedRange {
    <# Index of the "## Unreleased" line and of the line after its section; $null when there's no such heading. #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string[]]$Lines)
    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim() -eq '## Unreleased') { $start = $i; break }
    }
    if ($start -lt 0) { return $null }
    $end = $Lines.Count
    for ($i = $start + 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match '^## ') { $end = $i; break }
    }
    return [pscustomobject]@{ Start = $start; End = $end }
}

function Get-LatestReleaseTag {
    $latest = $null
    foreach ($name in (Invoke-ReleaseGit -Arguments @('tag', '--list', 'v[0-9]*'))) {
        if ($name -match '^v(\d+\.\d+\.\d+)$') {
            $candidate = [version]$Matches[1]
            if ($null -eq $latest -or $candidate -gt $latest) { $latest = $candidate }
        }
    }
    return $latest
}

function Get-CsprojVersion {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Text)
    $found = [regex]::Matches($Text, '<Version>([^<]*)</Version>')
    if ($found.Count -ne 1) { return $null }
    return $found[0].Groups[1].Value.Trim()
}

if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) { Exit-Release 'git is not on PATH.' }
if (-not (Test-Path (Join-Path $repoRoot '.git'))) { Exit-Release "$repoRoot is not a git repository." }

Write-Host "BlueTrack release $tagName ($($PSCmdlet.ParameterSetName))" -ForegroundColor Cyan

[void](Invoke-ReleaseGit -Arguments @('fetch', '--quiet', '--tags', $Remote, 'main'))
Write-Step "fetched $Remote/main and tags"

$localTag = Invoke-ReleaseGit -Arguments @('tag', '--list', $tagName)
$remoteTag = Invoke-ReleaseGit -Arguments @('ls-remote', '--tags', $Remote, "refs/tags/$tagName")
if ($localTag.Count -gt 0 -or $remoteTag.Count -gt 0) { Exit-Release "tag $tagName already exists." }
Write-Step "tag $tagName doesn't exist yet"

if ($Tag) {
    # Step 2: the newest first-parent commit on main where <Version> became this version.
    $remoteMain = "$Remote/main"
    $releaseCommit = $null
    foreach ($commit in (Invoke-ReleaseGit -Arguments @('log', '--first-parent', '--format=%H', $remoteMain, '--', $csprojRelative))) {
        $after = Get-CsprojVersion -Text ((Invoke-ReleaseGit -Arguments @('show', "${commit}:$csprojRelative")) -join "`n")
        $beforeLines = Invoke-ReleaseGit -Arguments @('show', "${commit}^1:$csprojRelative") -AllowFailure
        $before = Get-CsprojVersion -Text ($beforeLines -join "`n")
        if ($after -eq $Version -and $before -ne $Version) { $releaseCommit = $commit; break }
    }
    if ($null -eq $releaseCommit) {
        Exit-Release "no commit on $remoteMain sets <Version> to $Version. Merge the release pull request first."
    }
    $mainVersion = Get-CsprojVersion -Text ((Invoke-ReleaseGit -Arguments @('show', "${remoteMain}:$csprojRelative")) -join "`n")
    if ($mainVersion -ne $Version) {
        Exit-Release "$remoteMain's <Version> is now $mainVersion, not $Version."
    }
    $subject = (Invoke-ReleaseGit -Arguments @('log', '-1', '--format=%h %s', $releaseCommit)) -join ''
    Write-Step "release commit on ${remoteMain}: $subject"

    $signingKey = (Invoke-ReleaseGit -Arguments @('config', '--get', 'user.signingkey') -AllowFailure) -join ''
    if ([string]::IsNullOrWhiteSpace($signingKey)) {
        Exit-Release "git has no signing key (user.signingkey). Releases must be signed tags (D-199); see Deploy\README.md, 'Making a signed release'."
    }
    Write-Step 'git has a signing key'

    # The GitHub release's text: this version's section of Release Notes, as merged.
    $mergedNotes = [string[]](Invoke-ReleaseGit -Arguments @('show', "${releaseCommit}:$notesRelative"))
    $releaseBody = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in $mergedNotes) {
        if ($line -match '^## ') {
            if ($inSection) { break }
            $inSection = $line -match ('^## ' + [regex]::Escape($Version) + '(\s|$)')
            continue
        }
        if ($inSection) { $releaseBody.Add($line) }
    }
    $releaseText = ($releaseBody -join "`n").Trim()
    if ($releaseText -eq '') { Exit-Release "$notesRelative at the release commit has no '## $Version' section." }
    Write-Step "release notes: the '## $Version' section, $($releaseBody.Count) line(s)"

    if ($Push) { Assert-GhReady }

    if ($PSCmdlet.ShouldProcess($releaseCommit, "Create signed tag $tagName")) {
        [void](Invoke-ReleaseGit -Arguments @('tag', '-s', $tagName, '-m', "BlueTrack $tagName", $releaseCommit))
        Write-Step "created signed tag $tagName"
    }
    if (-not $Push) {
        if (-not $WhatIfPreference) {
            Write-Host "Next: git push $Remote refs/tags/$tagName; then gh release create $tagName --verify-tag --title 'BlueTrack $tagName' with the '## $Version' section of $notesRelative as its notes." -ForegroundColor Cyan
        }
        exit 0
    }

    if ($PSCmdlet.ShouldProcess("$Remote refs/tags/$tagName", 'git push')) {
        [void](Invoke-ReleaseGit -Arguments @('push', '--quiet', $Remote, "refs/tags/$tagName"))
        Write-Step "pushed $tagName to $Remote"
    }
    if ($PSCmdlet.ShouldProcess($tagName, 'gh release create')) {
        $notesFile = [System.IO.Path]::GetTempFileName()
        try {
            [System.IO.File]::WriteAllText($notesFile, $releaseText, (New-Object System.Text.UTF8Encoding($false)))
            $created = Invoke-ReleaseGh -Arguments @('release', 'create', $tagName, '--verify-tag', '--title', "BlueTrack $tagName", '--notes-file', $notesFile)
        } finally {
            Remove-Item -LiteralPath $notesFile -ErrorAction SilentlyContinue
        }
        if ($created.Code -ne 0) { Exit-Release "gh release create failed (exit code $($created.Code)): $($created.Output -join ' '). The tag is pushed; create the release by hand." }
        Write-Step "GitHub release: $(@($created.Output)[-1])"

        # D-199: Update-BlueTrack.ps1 accepts the release only if GitHub verified the tag's signature.
        $ref = Invoke-ReleaseGh -Arguments @('api', "repos/{owner}/{repo}/git/ref/tags/$tagName", '--jq', '.object.sha')
        $verified = $null
        $tagObject = ($ref.Output -join '').Trim()
        if ($ref.Code -eq 0 -and $tagObject -match '^[0-9a-f]{40}$') {
            $check = Invoke-ReleaseGh -Arguments @('api', "repos/{owner}/{repo}/git/tags/$tagObject", '--jq', '.verification.verified')
            if ($check.Code -eq 0 -and ($check.Output -join '').Trim() -ne '') { $verified = ($check.Output -join '').Trim() }
        }
        if ($verified -eq 'true') {
            Write-Step 'GitHub verified the tag signature'
        } elseif ($null -eq $verified) {
            Write-Host "WARN  couldn't read the tag's signature verification from GitHub; check it before announcing the release (Deploy\README.md, 'Making a signed release')." -ForegroundColor Yellow
        } else {
            Exit-Release "GitHub did not verify the tag's signature ($verified), so Update-BlueTrack.ps1 will refuse this release. Register the signing key on GitHub (Deploy\README.md, 'Making a signed release')."
        }
    }
    exit 0
}

# Step 1: checks.
$branch = (Invoke-ReleaseGit -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')) -join ''
if ($branch -ne 'main') { Exit-Release "on branch '$branch'; switch to main first." }
Write-Step 'on main'

$status = Invoke-ReleaseGit -Arguments @('status', '--porcelain')
if ($status.Count -gt 0) { Exit-Release "the working tree has changes: $($status -join '; ')" }
Write-Step 'working tree is clean'

$head = (Invoke-ReleaseGit -Arguments @('rev-parse', 'HEAD')) -join ''
$upstream = (Invoke-ReleaseGit -Arguments @('rev-parse', "$Remote/main")) -join ''
if ($head -ne $upstream) { Exit-Release "main isn't the same commit as $Remote/main; pull (or push) first." }
Write-Step "main matches $Remote/main"

try {
    $releaseDate = [datetime]::ParseExact($Date, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
} catch {
    Exit-Release "-Date '$Date' is not a real yyyy-MM-dd date."
}
$Date = $releaseDate.ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)

$latest = Get-LatestReleaseTag
if ($null -ne $latest -and [version]$Version -le $latest) { Exit-Release "$Version is not greater than the latest release tag v$latest." }
Write-Step $(if ($null -eq $latest) { "$Version (no earlier v* tag)" } else { "$Version is greater than v$latest" })

$branchName = "$(Get-Date -Format 'yyyy-MM-dd')-release-$tagName"
$localBranch = Invoke-ReleaseGit -Arguments @('branch', '--list', $branchName)
$remoteBranch = Invoke-ReleaseGit -Arguments @('ls-remote', '--heads', $Remote, $branchName)
if ($localBranch.Count -gt 0 -or $remoteBranch.Count -gt 0) { Exit-Release "branch $branchName already exists." }

$csproj = Read-TextFile -Path $csprojPath
$currentVersion = Get-CsprojVersion -Text $csproj.Text
if ($null -eq $currentVersion) { Exit-Release "$csprojRelative must have exactly one <Version> element." }

$notes = Read-TextFile -Path $notesPath
$noteLines = [string[]]($notes.Text -split '\r?\n')
$range = Get-UnreleasedRange -Lines $noteLines
if ($null -eq $range) { Exit-Release "$notesRelative has no '## Unreleased' heading." }
$items = @()
if ($range.End -gt $range.Start + 1) {
    $items = @($noteLines[($range.Start + 1)..($range.End - 1)] | Where-Object { $_ -match '^\s*([-*]|\d+\.)\s+\S' })
}
if ($items.Count -eq 0) { Exit-Release "$notesRelative's Unreleased section lists nothing to release." }
Write-Step "Unreleased lists $($items.Count) item(s)"

$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) { Exit-Release 'python is not on PATH; Published_Docs/build_published_docs.py needs Python 3 with python-docx (pip install python-docx).' }
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $python.Source -c 'import docx' 2>&1 | Out-Null
$docxCode = $LASTEXITCODE
$ErrorActionPreference = $previous
if ($docxCode -ne 0) { Exit-Release "python ($($python.Source)) can't import docx; run: pip install python-docx" }
Write-Step "python with python-docx: $($python.Source)"

if ($Push) { Assert-GhReady }

# Step 1: changes. Release Notes are written twice: the released section first
# (the .docx files are built from that), then the new empty Unreleased section.
$nl = $notes.Newline
$released = New-Object System.Collections.Generic.List[string]
$released.Add("## $Version ($Date)")
$section = @($noteLines[($range.Start + 1)..($range.End - 1)])
$i = 0
while ($i -lt $section.Count) {
    if ($section[$i] -match '^### ') {
        $j = $i + 1
        while ($j -lt $section.Count -and $section[$j] -notmatch '^### ') { $j++ }
        $body = @()
        if ($j -gt $i + 1) { $body = @($section[($i + 1)..($j - 1)] | Where-Object { $_.Trim() -ne '' }) }
        if ($body.Count -gt 0) {
            foreach ($line in $section[$i..($j - 1)]) { $released.Add($line) }
        }
        $i = $j
    } else {
        $released.Add($section[$i])
        $i++
    }
}
$notesBefore = if ($range.Start -gt 0) { @($noteLines[0..($range.Start - 1)]) } else { @() }
$notesAfter = if ($range.End -lt $noteLines.Count) { @($noteLines[$range.End..($noteLines.Count - 1)]) } else { @() }
$releasedText = (@($notesBefore) + @($released) + @($notesAfter)) -join $nl
$unreleased = @('## Unreleased', '', '### New', '', '### Changed', '', '### Fixed', '', '### Upgrading', '')
$finalText = (@($notesBefore) + $unreleased + @($released) + @($notesAfter)) -join $nl
$newCsproj = [regex]::Replace($csproj.Text, '<Version>[^<]*</Version>', "<Version>$Version</Version>")

$branchCreated = $false
$committed = $false
try {
    if ($PSCmdlet.ShouldProcess($branchName, 'Create and switch to branch')) {
        [void](Invoke-ReleaseGit -Arguments @('switch', '--quiet', '-c', $branchName))
        $branchCreated = $true
        Write-Step "created branch $branchName"
    }

    Set-ReleaseTextFile -Path $csprojPath -Text $newCsproj -HasBom $csproj.HasBom
    Set-ReleaseTextFile -Path $notesPath -Text $releasedText -HasBom $notes.HasBom
    if (-not $WhatIfPreference) { Write-Step "<Version> $currentVersion -> $Version; Unreleased -> $Version ($Date)" }

    $buildScript = Join-Path $repoRoot 'Published_Docs/build_published_docs.py'
    if ($PSCmdlet.ShouldProcess($buildScript, 'python')) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $buildOutput = @(& $python.Source $buildScript 2>&1 | ForEach-Object { "$_" })
        $buildCode = $LASTEXITCODE
        $ErrorActionPreference = $previous
        if ($buildCode -ne 0) { throw "build_published_docs.py failed (exit code $buildCode): $($buildOutput -join ' ')" }
        Write-Step "rebuilt Published_Docs ($($buildOutput.Count) document(s))"
    }

    if ($PSCmdlet.ShouldProcess('Sbom', "Tools\New-BlueTrackSbom.ps1 -Version $Version")) {
        & (Join-Path $PSScriptRoot 'New-BlueTrackSbom.ps1') -Version $Version | Out-Null
        Write-Step "wrote the SBOMs in Sbom/ for $Version"
    }

    Set-ReleaseTextFile -Path $notesPath -Text $finalText -HasBom $notes.HasBom

    if ($PSCmdlet.ShouldProcess($branchName, "Commit 'Release $tagName'")) {
        [void](Invoke-ReleaseGit -Arguments @('add', '--', $csprojRelative, $notesRelative, 'Published_Docs', 'Sbom'))
        [void](Invoke-ReleaseGit -Arguments @('commit', '--quiet', '-m', "Release $tagName"))
        $committed = $true
        Write-Step "committed 'Release $tagName'"
    }
} catch {
    Write-Host "FAIL  $($_.Exception.Message)" -ForegroundColor Red
    if ($branchCreated -and -not $committed) {
        # The tree was clean before this run, so only this run's changes are discarded.
        Invoke-ReleaseGit -Arguments @('reset', '--quiet', '--hard', 'HEAD') -AllowFailure | Out-Null
        Invoke-ReleaseGit -Arguments @('switch', '--quiet', 'main') -AllowFailure | Out-Null
        Invoke-ReleaseGit -Arguments @('branch', '--quiet', '-D', $branchName) -AllowFailure | Out-Null
        Write-Host "      Removed branch $branchName; back on main." -ForegroundColor Yellow
    }
    exit 1
}

if ($Push) {
    if ($PSCmdlet.ShouldProcess("$Remote $branchName", 'git push')) {
        [void](Invoke-ReleaseGit -Arguments @('push', '--quiet', '-u', $Remote, $branchName))
        Write-Step "pushed $branchName"
    }
    if ($PSCmdlet.ShouldProcess($branchName, 'gh pr create into main')) {
        $bodyFile = [System.IO.Path]::GetTempFileName()
        try {
            $body = @(
                "Release $tagName ($Date): sets <Version> to $Version, moves the Unreleased release notes to $Version, rebuilds Published_Docs and writes the SBOMs (Sbom/).",
                '',
                "After merging, tag and publish it: Tools\New-BlueTrackRelease.ps1 -Version $Version -Tag -Push"
            ) -join "`n"
            [System.IO.File]::WriteAllText($bodyFile, $body, (New-Object System.Text.UTF8Encoding($false)))
            $pr = Invoke-ReleaseGh -Arguments @('pr', 'create', '--base', 'main', '--head', $branchName, '--title', "Release $tagName", '--body-file', $bodyFile)
            if ($pr.Code -ne 0) { Exit-Release "gh pr create failed (exit code $($pr.Code)): $($pr.Output -join ' '). The branch is pushed; open the pull request by hand." }
            Write-Step "pull request: $(@($pr.Output)[-1])"
        } finally {
            Remove-Item -LiteralPath $bodyFile -ErrorAction SilentlyContinue
        }
    }
    if (-not $WhatIfPreference) {
        Write-Host "Next: merge the pull request, then run .\New-BlueTrackRelease.ps1 -Version $Version -Tag -Push" -ForegroundColor Cyan
    }
} elseif (-not $WhatIfPreference) {
    Write-Host "Next: git push -u $Remote $branchName, open a pull request into main, merge it, then run .\New-BlueTrackRelease.ps1 -Version $Version -Tag -Push" -ForegroundColor Cyan
}
exit 0
