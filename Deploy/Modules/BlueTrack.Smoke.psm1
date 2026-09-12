<#
.SYNOPSIS
    Post-install smoke test against the Deployment Info health-check endpoint.
#>

Set-StrictMode -Version Latest

function Test-BlueTrackDeployment {
    <#
    .SYNOPSIS
        Calls GET /api/admin/deployment under the caller's own Windows identity
        (Negotiate) and reports each returned health check.
    .DESCRIPTION
        This endpoint is gated by the ViewDeploymentInfo permission, not
        anonymous (confirmed in App/Api/Controllers/DeploymentController.cs) --
        it only succeeds if the identity running this script holds that
        permission. The seeded bootstrap admin group (BUILTIN\Administrators)
        does by default. Failure here is reported, not thrown -- the install
        itself may still be fine; this just means the smoke test couldn't
        confirm it (e.g. a cert trust issue, or the caller isn't a BlueTrack admin).
    .OUTPUTS
        [bool] whether every reported health check came back healthy.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$SiteUrl,   # e.g. https://bluetrack.company.com
        [int]$TimeoutSeconds = 30
    )

    $endpoint = "$($SiteUrl.TrimEnd('/'))/api/admin/deployment"
    Write-Host "Smoke test: GET $endpoint (Windows-integrated auth, current identity)..."

    try {
        $response = Invoke-RestMethod -Uri $endpoint -UseDefaultCredentials -TimeoutSec $TimeoutSeconds
    } catch {
        Write-Warning "Smoke test could not reach or authenticate against '$endpoint': $($_.Exception.Message)"
        Write-Warning 'This does not necessarily mean the install failed -- confirm the certificate is trusted, the hostname resolves, and the identity running this script holds ViewDeploymentInfo (the bootstrap BUILTIN\Administrators group does by default).'
        return $false
    }

    Write-Host "Environment: $($response.environmentName)  Version: $($response.version)  Built: $($response.buildTimestampUtc)"

    $allHealthy = $true
    foreach ($check in $response.healthChecks) {
        $status = if ($check.status -eq 'Healthy') { 'OK' } else { $allHealthy = $false; 'FAILED' }
        Write-Host "  [$status] $($check.name): $($check.description)"
    }

    if ($allHealthy) {
        Write-Host 'Smoke test: all reported health checks are healthy.' -ForegroundColor Green
    } else {
        Write-Warning 'Smoke test: at least one health check is not healthy -- review the Deployment Info admin page.'
    }

    return $allHealthy
}

Export-ModuleMember -Function Test-BlueTrackDeployment
