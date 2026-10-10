<#
.SYNOPSIS
    Post-install smoke test against the Deployment Info health-check endpoint.
#>

Set-StrictMode -Version Latest

function Test-BlueTrackDeployment {
    <#
    .SYNOPSIS
        Calls GET /BlueTrack/api/admin/deployment under the caller's own
        Windows identity (Negotiate) -- the same path the SPA uses (D-196) --
        and reports each returned health check, then checks that the site
        root serves the SPA.
    .DESCRIPTION
        This endpoint is gated by the ViewDeploymentInfo permission, not
        anonymous (confirmed in App/Api/Controllers/DeploymentController.cs) --
        it only succeeds if the identity running this script holds that
        permission. The seeded bootstrap admin group (BUILTIN\Administrators)
        does by default. Failure here is reported, not thrown -- the install
        itself may still be fine; this just means the smoke test couldn't
        confirm it (e.g. a cert trust issue, or the caller isn't a BlueTrack admin).
    .OUTPUTS
        [bool] whether every reported health check came back healthy and the
        site root served the SPA.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string]$SiteUrl,   # e.g. https://bluetrack.company.com
        [int]$TimeoutSeconds = 30
    )

    $endpoint = "$($SiteUrl.TrimEnd('/'))/BlueTrack/api/admin/deployment"
    $siteRoot = "$($SiteUrl.TrimEnd('/'))/"
    Write-Host "Smoke test: GET $endpoint (Windows-integrated auth, current identity)..."

    try {
        $response = Invoke-RestMethod -Uri $endpoint -UseDefaultCredentials -TimeoutSec $TimeoutSeconds
    } catch {
        Write-Warning "Smoke test failed calling '$endpoint': $($_.Exception.Message)"
        Write-BlueTrackSmokeDiagnosis -ErrorRecord $_
        Write-BlueTrackSmokeTestUrl -AppEndpoint $endpoint -SiteRoot $siteRoot
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

    # D-196: the SPA calls /BlueTrack/api/... itself (checked above); the site
    # root only has to serve the SPA's index.html.
    Write-Host "Smoke test: GET $siteRoot (the SPA)..."
    try {
        $page = Invoke-WebRequest -Uri $siteRoot -UseDefaultCredentials -UseBasicParsing -TimeoutSec $TimeoutSeconds
        if ($page.Content -notmatch '<div id="app">') {
            Write-Warning "Smoke test: '$siteRoot' answered, but not with the BlueTrack SPA's index.html -- check the site's physical path (-WebInstallPath) and the Build.Web step."
            Write-BlueTrackSmokeTestUrl -AppEndpoint $endpoint -SiteRoot $siteRoot
            return $false
        }
        Write-Host 'Smoke test: the site root serves the SPA.' -ForegroundColor Green
    } catch {
        Write-Warning "Smoke test failed calling '$siteRoot': $($_.Exception.Message)"
        Write-BlueTrackSmokeDiagnosis -ErrorRecord $_
        Write-BlueTrackSmokeTestUrl -AppEndpoint $endpoint -SiteRoot $siteRoot
        return $false
    }

    return $allHealthy
}

function Write-BlueTrackSmokeTestUrl {
    <#
    .SYNOPSIS
        After a smoke test failure, prints the two URLs it calls so they can
        be tried by hand: in a browser on this server, and with PowerShell as
        the current Windows identity.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$AppEndpoint,
        [Parameter(Mandatory)] [string]$SiteRoot
    )

    Write-Warning 'To test by hand, open these in a browser on this server, or run the PowerShell line:'
    Write-Warning "  1. The API (the SPA calls it at this path, D-196), should return JSON:  $AppEndpoint"
    Write-Warning "     Invoke-RestMethod -Uri '$AppEndpoint' -UseDefaultCredentials"
    Write-Warning "  2. The site, should show the BlueTrack sign-in or dashboard:  $SiteRoot"
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
            Write-Warning 'HTTP 403: you are signed in, but your account lacks ViewDeploymentInfo. The bootstrap Admin role has it, mapped to BUILTIN\Administrators (S-1-5-32-544) by Database/05_BlueTrack_Baseline_WebSeed.sql -- run this script elevated as a local administrator, or map your group to Admin.'
        }
        { $_ -ge 500 } {
            Write-Warning "HTTP $($status): the request reached BlueTrack.Api and it failed. The exception is in the Application event log (source '.NET Runtime', event 1000)."
            # The usual cause on a fresh install: the app pool's account has no
            # SQL Server login. SQL Server logs that as event 18456 naming the
            # account -- only visible here when SQL Server runs on this machine.
            $loginFailure = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 18456; StartTime = (Get-Date).AddMinutes(-10) } -MaxEvents 1 -ErrorAction SilentlyContinue
            if ($loginFailure) {
                # The account name in the message can be wrong: with SQL Server
                # on this machine it names the computer account while the
                # connection really arrives as IIS APPPOOL\<pool> (D-171). The
                # event's own SID is the account that needs the login.
                $nameInMessage = $loginFailure.Properties[0].Value
                $sidAccount = $null
                if ($loginFailure.UserId) {
                    try { $sidAccount = $loginFailure.UserId.Translate([System.Security.Principal.NTAccount]).Value } catch { $sidAccount = $loginFailure.UserId.Value }
                }
                Write-Warning "SQL Server logged a login failure: $($loginFailure.Properties[1].Value.Trim())"
                if ($sidAccount -and $sidAccount -ne $nameInMessage) {
                    Write-Warning "The message names '$nameInMessage', but the connection arrived as '$sidAccount' (the event's SID) -- that is the account that needs the SQL login."
                } else {
                    Write-Warning "Account: '$nameInMessage'."
                }
                Write-Warning 'Grant it with: .\Install-BlueTrack.ps1 -Step Db.AppPoolAccess -GrantAppPoolSqlAccess $true (or -AppPoolSqlLogin to name the account yourself).'
            }
        }
        default {
            Write-Warning 'No HTTP response: confirm the hostname resolves to this server, the HTTPS binding exists, and the certificate is trusted by this machine.'
        }
    }
}

Export-ModuleMember -Function Test-BlueTrackDeployment
