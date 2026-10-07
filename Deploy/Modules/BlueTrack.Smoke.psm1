<#
.SYNOPSIS
    Post-install smoke test against the Deployment Info health-check endpoint.
#>

Set-StrictMode -Version Latest

function Test-BlueTrackDeployment {
    <#
    .SYNOPSIS
        Calls GET /BlueTrack/api/admin/deployment under the caller's own
        Windows identity (Negotiate) and reports each returned health check.
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

    $endpoint = "$($SiteUrl.TrimEnd('/'))/BlueTrack/api/admin/deployment"
    Write-Host "Smoke test: GET $endpoint (Windows-integrated auth, current identity)..."

    try {
        $response = Invoke-RestMethod -Uri $endpoint -UseDefaultCredentials -TimeoutSec $TimeoutSeconds
    } catch {
        Write-Warning "Smoke test failed calling '$endpoint': $($_.Exception.Message)"
        Write-BlueTrackSmokeDiagnosis -ErrorRecord $_
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

function Write-BlueTrackSmokeDiagnosis {
    <#
    .SYNOPSIS
        Explains a failed smoke-test call by HTTP status (D-170). Each status
        has a different cause; before this, every failure printed the same
        certificate/ViewDeploymentInfo advice, which was wrong for a 500.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $status = $null
    $exception = $ErrorRecord.Exception
    if ($exception.PSObject.Properties['Response'] -and $exception.Response) {
        $status = [int]$exception.Response.StatusCode
    }

    switch ($status) {
        401 {
            Write-Warning 'HTTP 401: IIS rejected the Windows login before the request reached BlueTrack (BlueTrack''s permissions are not involved yet).'
            Write-Warning 'Seen live as 401.1 / 0x8009030e and cleared by "klist purge" (a stale Kerberos ticket is the likely, unconfirmed cause): run klist purge, then -Step Smoke. If it persists, check the app pool identity, SPNs and useAppPoolCredentials -- Deploy/README.md, "Smoke test failures".'
        }
        403 {
            Write-Warning 'HTTP 403: you are signed in, but your account lacks ViewDeploymentInfo. The bootstrap Admin role has it, mapped to BUILTIN\Administrators (S-1-5-32-544) by Database/09_BlueTrack_WebSeed.sql -- run this script elevated as a local administrator, or map your group to Admin.'
        }
        { $_ -ge 500 } {
            Write-Warning "HTTP $($status): the request reached BlueTrack.Api and it failed. The exception is in the Application event log (source '.NET Runtime', event 1000)."
            # The usual cause on a fresh install: the app pool's account has no
            # SQL Server login. SQL Server logs that as event 18456 naming the
            # account -- only visible here when SQL Server runs on this machine.
            $loginFailure = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 18456; StartTime = (Get-Date).AddMinutes(-10) } -MaxEvents 1 -ErrorAction SilentlyContinue
            if ($loginFailure) {
                Write-Warning "SQL Server logged a login failure for '$($loginFailure.Properties[0].Value)': $($loginFailure.Properties[1].Value)"
                Write-Warning 'Grant that account access with: .\Install-BlueTrack.ps1 -Step Db.AppPoolAccess -GrantAppPoolSqlAccess $true'
            }
        }
        default {
            Write-Warning 'No HTTP response: confirm the hostname resolves to this server, the HTTPS binding exists, and the certificate is trusted by this machine.'
        }
    }
}

Export-ModuleMember -Function Test-BlueTrackDeployment
