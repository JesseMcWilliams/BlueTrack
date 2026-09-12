#Requires -Modules WebAdministration
<#
.SYNOPSIS
    Creates the IIS Application Pool, Site, and nested /api Application
    BlueTrack needs -- the piece Design_Deployment_Methodology.md flagged as
    not yet built anywhere in the repo. Every step here is idempotent
    (check-then-create); pass -Force to remove and recreate something that
    already exists instead of leaving it alone.
#>

Set-StrictMode -Version Latest

function New-BlueTrackAppPool {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [switch]$Force
    )

    $existing = Get-Item "IIS:\AppPools\$Name" -ErrorAction SilentlyContinue
    if ($existing) {
        if (-not $Force) {
            Write-Host "Application Pool '$Name' already exists -- leaving it alone (pass -Force to recreate)."
            return
        }
        if ($PSCmdlet.ShouldProcess("IIS:\AppPools\$Name", 'Remove existing Application Pool')) {
            Remove-WebAppPool -Name $Name
        }
    }

    if ($PSCmdlet.ShouldProcess("IIS:\AppPools\$Name", 'Create Application Pool (No Managed Code -- ANCM manages its own runtime)')) {
        New-WebAppPool -Name $Name | Out-Null
        Set-ItemProperty "IIS:\AppPools\$Name" -Name managedRuntimeVersion -Value ''
        Set-ItemProperty "IIS:\AppPools\$Name" -Name startMode -Value 'AlwaysRunning'
    }
}

function New-BlueTrackCertificateBinding {
    <#
    .SYNOPSIS
        Resolves the certificate to bind: an existing thumbprint if given, a
        freshly generated self-signed certificate otherwise (loudly warned as
        non-production-appropriate).
    .OUTPUTS
        The certificate thumbprint to bind.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string]$CertificateThumbprint,
        [Parameter(Mandatory)] [string]$Hostname,
        [switch]$GenerateSelfSigned
    )

    if ($CertificateThumbprint) {
        $cert = Get-Item "Cert:\LocalMachine\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
        if (-not $cert) {
            throw "No certificate with thumbprint '$CertificateThumbprint' found in Cert:\LocalMachine\My."
        }
        return $CertificateThumbprint
    }

    if (-not $GenerateSelfSigned) {
        throw 'No -CertificateThumbprint supplied and -GenerateSelfSigned was not requested -- cannot configure an HTTPS binding.'
    }

    Write-Warning "Generating a SELF-SIGNED certificate for '$Hostname'. This is only appropriate for Dev/Test -- browsers will show a trust warning, and this must never be used for a real Staging/Production environment."
    if ($PSCmdlet.ShouldProcess($Hostname, 'New-SelfSignedCertificate')) {
        $cert = New-SelfSignedCertificate -DnsName $Hostname -CertStoreLocation Cert:\LocalMachine\My -FriendlyName "BlueTrack self-signed ($Hostname)"
        return $cert.Thumbprint
    }
}

function New-BlueTrackSite {
    <#
    .SYNOPSIS
        Creates the IIS site (physical root = the built SPA), its bindings,
        and the nested /api Application pointing at the published API output.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$SiteName,
        [Parameter(Mandatory)] [string]$AppPoolName,
        [Parameter(Mandatory)] [string]$SpaPhysicalPath,
        [Parameter(Mandatory)] [string]$ApiPhysicalPath,
        [Parameter(Mandatory)] [string]$Hostname,
        [int]$HttpPort = 80,
        [int]$HttpsPort = 443,
        [string]$CertificateThumbprint,
        [switch]$Force
    )

    $existingSite = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    if ($existingSite) {
        if (-not $Force) {
            Write-Host "Site '$SiteName' already exists -- leaving it alone (pass -Force to recreate). Only the nested /api Application and web.config will be (re)checked."
        } else {
            if ($PSCmdlet.ShouldProcess($SiteName, 'Remove existing site')) {
                Remove-Website -Name $SiteName
                $existingSite = $null
            }
        }
    }

    if (-not $existingSite) {
        if ($PSCmdlet.ShouldProcess($SiteName, "Create site at $SpaPhysicalPath")) {
            New-Website -Name $SiteName -PhysicalPath $SpaPhysicalPath -ApplicationPool $AppPoolName `
                -Port $HttpPort -HostHeader $Hostname | Out-Null

            if ($CertificateThumbprint) {
                New-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -HostHeader $Hostname -SslFlags 1
                $binding = Get-WebBinding -Name $SiteName -Protocol https
                $binding.AddSslCertificate($CertificateThumbprint, 'My')
            }
        }
    }

    $existingApiApp = Get-WebApplication -Site $SiteName -Name 'api' -ErrorAction SilentlyContinue
    if ($existingApiApp -and -not $Force) {
        Write-Host "Application '/api' under site '$SiteName' already exists -- leaving it alone (pass -Force to recreate)."
    } else {
        if ($existingApiApp -and $PSCmdlet.ShouldProcess("$SiteName/api", 'Remove existing Application')) {
            Remove-WebApplication -Site $SiteName -Name 'api'
        }
        if ($PSCmdlet.ShouldProcess("$SiteName/api", "Create Application at $ApiPhysicalPath")) {
            New-WebApplication -Site $SiteName -Name 'api' -PhysicalPath $ApiPhysicalPath -ApplicationPool $AppPoolName | Out-Null
        }
    }
    # Note: the /api Application's own web.config (ANCM registration) is
    # generated automatically by `dotnet publish` into $ApiPhysicalPath --
    # nothing to author here.
}

function Set-BlueTrackSiteWebConfig {
    <#
    .SYNOPSIS
        Writes the site-root web.config (SPA fallback-to-index.html URL Rewrite
        rule) from Deploy/Templates/site-web.config.template. This is the piece
        Design_Deployment_Methodology.md flagged as not yet built anywhere.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$SpaPhysicalPath
    )

    $templatePath = Join-Path $RepoRoot 'Deploy\Templates\site-web.config.template'
    if (-not (Test-Path $templatePath)) {
        throw "Could not find '$templatePath'."
    }

    $destinationPath = Join-Path $SpaPhysicalPath 'web.config'
    if ($PSCmdlet.ShouldProcess($destinationPath, 'Write site-root web.config (SPA URL Rewrite fallback rule)')) {
        Copy-Item -Path $templatePath -Destination $destinationPath -Force
    }
}

function Restart-BlueTrackAppPool {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$Name
    )

    if ($PSCmdlet.ShouldProcess($Name, 'Recycle Application Pool')) {
        Restart-WebAppPool -Name $Name
    }
}

Export-ModuleMember -Function New-BlueTrackAppPool, New-BlueTrackCertificateBinding, New-BlueTrackSite, Set-BlueTrackSiteWebConfig, Restart-BlueTrackAppPool
