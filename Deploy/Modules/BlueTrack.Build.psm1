<#
.SYNOPSIS
    Builds the API (dotnet publish) and the SPA (npm run build) from source.
#>

Set-StrictMode -Version Latest

function Publish-BlueTrackApi {
    <#
    .SYNOPSIS
        Publishes App/Api in Release configuration to the given output directory.
    .OUTPUTS
        The publish output directory path, on success. Throws on failure.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$OutputDirectory
    )

    $apiProject = Join-Path $RepoRoot 'App\Api'
    if (-not (Test-Path $apiProject)) {
        throw "Could not find App/Api under '$RepoRoot' -- is -RepoRoot pointed at the BlueTrack repo root?"
    }

    if ($PSCmdlet.ShouldProcess($apiProject, "dotnet publish -c Release -o $OutputDirectory")) {
        # Piped through Out-Host -- see the matching comment in
        # Invoke-BlueTrackWebBuild below; this function also returns a typed
        # value ($OutputDirectory) that a future caller could capture.
        & dotnet publish $apiProject -c Release -o $OutputDirectory | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for App/Api (exit code $LASTEXITCODE) -- see the output above for the actual build error."
        }
    }

    return $OutputDirectory
}

function Invoke-BlueTrackWebBuild {
    <#
    .SYNOPSIS
        Runs `npm ci` then `npm run build` in App/Web.
    .OUTPUTS
        The SPA's dist/ directory path, on success. Throws on failure.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot
    )

    $webProject = Join-Path $RepoRoot 'App\Web'
    if (-not (Test-Path $webProject)) {
        throw "Could not find App/Web under '$RepoRoot' -- is -RepoRoot pointed at the BlueTrack repo root?"
    }

    Push-Location $webProject
    try {
        if ($PSCmdlet.ShouldProcess($webProject, 'npm ci')) {
            # Piped through Out-Host, not left as pipeline output: this
            # function's caller assigns its return value ($spaDist = ...),
            # and without this, npm's own console output would be captured
            # into that assignment alongside the path this function returns,
            # turning $spaDist into a multi-element array instead of a
            # single string -- exactly the bug that shipped once already.
            & npm ci | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "npm ci failed in App/Web (exit code $LASTEXITCODE)."
            }
        }
        if ($PSCmdlet.ShouldProcess($webProject, 'npm run build')) {
            & npm run build | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "npm run build failed in App/Web (exit code $LASTEXITCODE) -- see the output above for the actual build error."
            }
        }
    } finally {
        Pop-Location
    }

    return (Join-Path $webProject 'dist')
}

Export-ModuleMember -Function Publish-BlueTrackApi, Invoke-BlueTrackWebBuild
