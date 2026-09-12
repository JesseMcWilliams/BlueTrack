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
        & dotnet publish $apiProject -c Release -o $OutputDirectory
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
            & npm ci
            if ($LASTEXITCODE -ne 0) {
                throw "npm ci failed in App/Web (exit code $LASTEXITCODE)."
            }
        }
        if ($PSCmdlet.ShouldProcess($webProject, 'npm run build')) {
            & npm run build
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
