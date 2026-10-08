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
        # D-175: a deployed API running in-process under IIS holds
        # BlueTrack.Api.dll open, so publishing over it failed with MSB3021
        # ("being used by another process") and needed an iisreset. The ASP.NET
        # Core Module's own mechanism instead: while app_offline.htm exists in
        # the app's folder, it shuts the app down and releases its files (and
        # serves that page). Only this app stops; it starts again on the next
        # request after the file is removed.
        $appOffline = Join-Path $OutputDirectory 'app_offline.htm'
        $takenOffline = $false
        if (Test-Path (Join-Path $OutputDirectory 'BlueTrack.Api.dll')) {
            Set-Content -Path $appOffline -Value '<!DOCTYPE html><html><body><h1>BlueTrack is being updated</h1><p>Try again in a minute.</p></body></html>' -Encoding UTF8
            $takenOffline = $true
            Write-Host 'Took the running API offline (app_offline.htm) so its files can be replaced.'
            # The module notices the file and stops the app within a moment;
            # give it time to release the DLL before publishing over it.
            Start-Sleep -Seconds 5
        }
        try {
            # Piped through Out-Host -- see the matching comment in
            # Invoke-BlueTrackWebBuild below; this function also returns a typed
            # value ($OutputDirectory) that a future caller could capture.
            & dotnet publish $apiProject -c Release -o $OutputDirectory | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish failed for App/Api (exit code $LASTEXITCODE) -- see the output above for the actual build error."
            }
        } finally {
            if ($takenOffline) {
                Remove-Item -Path $appOffline -ErrorAction SilentlyContinue
                Write-Host 'API back online (app_offline.htm removed).'
            }
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
