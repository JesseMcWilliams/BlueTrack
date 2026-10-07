#Requires -Modules WebAdministration
<#
.SYNOPSIS
    Creates the IIS Application Pool, Site, and nested /BlueTrack Application
    BlueTrack needs -- the piece Design_Deployment-Methodology.md flagged as
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
        and the nested /BlueTrack Application pointing at the published API output.
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
            Write-Host "Site '$SiteName' already exists -- leaving it alone (pass -Force to recreate). Only the nested /BlueTrack Application and web.config will be (re)checked."
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

    # D-163: named "BlueTrack", not "api" -- see the matching comment in
    # Deploy/Templates/site-web.config.template for why. The real external
    # API root ends up being /BlueTrack/api/... (the "api" segment comes
    # from the controllers' own route templates, not from this Application's
    # name), not the /BlueTrack/api/api/... a name of "api" would require.
    $existingApiApp = Get-WebApplication -Site $SiteName -Name 'BlueTrack' -ErrorAction SilentlyContinue
    if ($existingApiApp -and -not $Force) {
        Write-Host "Application '/BlueTrack' under site '$SiteName' already exists -- leaving it alone (pass -Force to recreate)."
    } else {
        if ($existingApiApp -and $PSCmdlet.ShouldProcess("$SiteName/BlueTrack", 'Remove existing Application')) {
            Remove-WebApplication -Site $SiteName -Name 'BlueTrack'
        }
        if ($PSCmdlet.ShouldProcess("$SiteName/BlueTrack", "Create Application at $ApiPhysicalPath")) {
            New-WebApplication -Site $SiteName -Name 'BlueTrack' -PhysicalPath $ApiPhysicalPath -ApplicationPool $AppPoolName | Out-Null
        }
    }
    # Note: the /BlueTrack Application's own web.config (ANCM registration)
    # is generated automatically by `dotnet publish` into $ApiPhysicalPath --
    # nothing to author here.

    # D-162: BlueTrack.Api defers Windows Integrated Auth to IIS's own
    # native handshake when IIS-hosted (AuthenticationExtensions.cs's
    # IsIisHosted()/GetPrimaryAuthenticationScheme()) rather than running
    # its own Negotiate handler, which cannot coexist with IIS/ANCM at
    # all. Both windowsAuthentication AND anonymousAuthentication stay
    # enabled together (not windowsAuthentication alone) so the app's own
    # Cookie/OIDC/SAML/DevFakeAuth-authenticated requests -- which never
    # present Windows credentials -- aren't rejected by IIS before they
    # even reach the app; ASP.NET Core's own [Authorize] enforcement
    # downstream still gates unauthenticated requests exactly as it does
    # for a self-hosted deployment.
    if ($PSCmdlet.ShouldProcess("$SiteName/BlueTrack", 'Enable IIS Windows Authentication (with Anonymous also enabled)')) {
        # These two sections are locked (overrideModeDefault="Deny") on a
        # stock IIS install -- confirmed directly on a real host, where
        # Set-WebConfigurationProperty against either one failed with
        # "This configuration section cannot be used at this path" until
        # unlocked. Unlocking is a one-time, server-wide, idempotent action
        # (safe to repeat every run) that only WIDENS what any site is
        # *allowed* to configure -- it doesn't change any existing site's
        # actual auth behavior on its own.
        & "$env:windir\system32\inetsrv\appcmd.exe" unlock config -section:system.webServer/security/authentication/windowsAuthentication | Out-Host
        & "$env:windir\system32\inetsrv\appcmd.exe" unlock config -section:system.webServer/security/authentication/anonymousAuthentication | Out-Host

        Set-WebConfigurationProperty -Filter '/system.webServer/security/authentication/windowsAuthentication' -PSPath "IIS:\Sites\$SiteName\BlueTrack" -Name Enabled -Value $true
        Set-WebConfigurationProperty -Filter '/system.webServer/security/authentication/anonymousAuthentication' -PSPath "IIS:\Sites\$SiteName\BlueTrack" -Name Enabled -Value $true
    }

    # D-172: the SPA calls /api/... at the SITE ROOT, which D-166's rewrite
    # rule hands to /BlueTrack/api/... inside IIS. With kernel-mode auth,
    # HTTP.sys handles the Negotiate/NTLM exchange using the settings of the
    # URL as the browser sent it -- the root's -- before that rewrite. With
    # Windows auth off at the root, every sign-in through /api/... failed
    # with 401.1 (confirmed live on DCACYBSQL01: /BlueTrack/api/me signed in,
    # /api/me never did) while the smoke test, which calls /BlueTrack/...
    # directly, passed. Enabled at the root too, with Anonymous still on, so
    # the SPA's static files never ask for credentials. Written to
    # applicationHost.config (not the root web.config) because that file is
    # regenerated from Templates/ by Set-BlueTrackSiteWebConfig and wiped by
    # every SPA build.
    if ($PSCmdlet.ShouldProcess($SiteName, 'Enable IIS Windows Authentication at the site root (with Anonymous also enabled)')) {
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name enabled -Value $true
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $true
    }
}

function Set-BlueTrackSiteWebConfig {
    <#
    .SYNOPSIS
        Writes the site-root web.config (SPA fallback-to-index.html URL Rewrite
        rule) from Deploy/Templates/site-web.config.template. This is the piece
        Design_Deployment-Methodology.md flagged as not yet built anywhere.
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

function Test-BlueTrackSqlServerIsLocal {
    <#
    .SYNOPSIS
        Whether a SQL Server instance name (Server=/Data Source= value) points
        at this machine: localhost, ., (local), this computer's name or FQDN,
        or a name/address that resolves to one of this machine's addresses.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string]$SqlServerInstance
    )

    # Strip a protocol prefix (tcp:, np:, lpc:), an instance name and a port.
    $hostPart = ($SqlServerInstance -replace '^[a-zA-Z]+:', '') -split '[\x5C,]' | Select-Object -First 1
    $hostPart = $hostPart.Trim()
    if ($hostPart -in '.', '(local)', 'localhost', '127.0.0.1', '::1', '') { return $true }

    $computer = Get-CimInstance Win32_ComputerSystem
    $localNames = @($env:COMPUTERNAME)
    if ($computer.PartOfDomain) { $localNames += "$($env:COMPUTERNAME).$($computer.Domain)" }
    if ($hostPart -in $localNames) { return $true }

    try {
        $localAddresses = @([System.Net.Dns]::GetHostAddresses($env:COMPUTERNAME) | ForEach-Object { $_.ToString() })
        $targetAddresses = @([System.Net.Dns]::GetHostAddresses($hostPart) | ForEach-Object { $_.ToString() })
        return [bool]($targetAddresses | Where-Object { $_ -in $localAddresses -or $_ -in '127.0.0.1', '::1' })
    } catch {
        # Unresolvable here: treat as remote and let the grant or the
        # smoke test's 18456 diagnosis show otherwise.
        return $false
    }
}

function Get-BlueTrackAppPoolSqlLogin {
    <#
    .SYNOPSIS
        The Windows account an Application Pool actually presents to SQL
        Server under Windows Integrated Security (D-171).
    .DESCRIPTION
        A custom identity (a gMSA or service account) arrives as itself.
        Otherwise it depends on where SQL Server runs:
          - Same machine as IIS: the pool's own local identity --
            IIS APPPOOL\<pool> for ApplicationPoolIdentity. Confirmed live on
            two hosts (Server 2019 and 2022) from the SID on SQL Server's
            18456 events. SQL Server's message text names the computer
            account (DOMAIN\HOSTNAME$) instead, but a login for that is never
            matched (D-170 granted it and still failed). NetworkService,
            LocalSystem and LocalService follow the same rule (their own
            NT AUTHORITY account); untested.
          - Another machine: the computer account, DOMAIN\HOSTNAME$ --
            standard Windows behavior for these identities on the network,
            not yet tested on a BlueTrack host.
    .OUTPUTS
        [string] the login name, e.g. IIS APPPOOL\BlueTrack-AppPool.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string]$AppPoolName,
        [Parameter(Mandatory)] [string]$SqlServerInstance
    )

    $processModel = Get-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel -ErrorAction SilentlyContinue
    if (-not $processModel) {
        throw "Application Pool '$AppPoolName' not found -- run the Iis.AppPool step first."
    }
    if ($processModel.identityType -eq 'SpecificUser') {
        return $processModel.userName
    }

    if (Test-BlueTrackSqlServerIsLocal -SqlServerInstance $SqlServerInstance) {
        switch ($processModel.identityType) {
            'ApplicationPoolIdentity' { return "IIS APPPOOL\$AppPoolName" }
            'NetworkService' { return 'NT AUTHORITY\NETWORK SERVICE' }
            'LocalSystem' { return 'NT AUTHORITY\SYSTEM' }
            'LocalService' { return 'NT AUTHORITY\LOCAL SERVICE' }
        }
    }

    if ($processModel.identityType -eq 'LocalService') {
        throw "Application Pool '$AppPoolName' runs as LocalService, which has no network identity and cannot use Windows authentication to a SQL Server on another machine. Use ApplicationPoolIdentity or a dedicated account."
    }
    if (-not (Get-CimInstance Win32_ComputerSystem).PartOfDomain) {
        throw "This server isn't domain-joined, so there's no DOMAIN\$($env:COMPUTERNAME)`$ computer account for a remote SQL Server to accept. Use a SQL login (-UseWindowsAuth `$false) or pass -AppPoolSqlLogin."
    }
    # Resolved via its SID, so the domain part is the machine's own NetBIOS
    # domain, not the (possibly different) installing user's.
    $account = New-Object System.Security.Principal.NTAccount("$($env:COMPUTERNAME)`$")
    $sid = $account.Translate([System.Security.Principal.SecurityIdentifier])
    return $sid.Translate([System.Security.Principal.NTAccount]).Value
}

Export-ModuleMember -Function New-BlueTrackAppPool, New-BlueTrackCertificateBinding, New-BlueTrackSite, Set-BlueTrackSiteWebConfig, Restart-BlueTrackAppPool, Get-BlueTrackAppPoolSqlLogin, Test-BlueTrackSqlServerIsLocal
