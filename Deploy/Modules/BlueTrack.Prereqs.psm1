#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Verifies (and where reasonable, auto-installs) the software BlueTrack needs on this box.

.DESCRIPTION
    Deliberate scope boundary: this module will verify SQL Server Database Engine
    connectivity but will NEVER attempt to install SQL Server itself -- that's an
    edition/licensing-sensitive decision too big for an unattended script to make.
    Everything else it checks (.NET SDK, Node.js, the IIS role, the ASP.NET Core
    Hosting Bundle, the URL Rewrite Module) is auto-installable with the caller's
    confirmation.
#>

Set-StrictMode -Version Latest

function Test-BlueTrackPrerequisite {
    <#
    .SYNOPSIS
        Runs every prerequisite check and returns a status object per item.
    .OUTPUTS
        [pscustomobject[]] with Name/Installed/Version/AutoInstallable/Detail
    #>
    [CmdletBinding()]
    param()

    $results = @()

    # --- .NET SDK ---------------------------------------------------------
    $dotnetVersion = $null
    try {
        $dotnetVersion = (& dotnet --version) 2>$null
    } catch {
        # dotnet not on PATH at all -- expected on a fresh box, not a real error.
        $dotnetVersion = $null
    }
    $results += [pscustomobject]@{
        Name            = '.NET 10 SDK'
        Installed       = [bool]($dotnetVersion -and $dotnetVersion -like '10.*')
        Version         = $dotnetVersion
        AutoInstallable = $true
        Detail          = if ($dotnetVersion) { "Found $dotnetVersion" } else { 'dotnet not found on PATH' }
    }

    # --- Node.js / npm ------------------------------------------------------
    $nodeVersion = $null
    try {
        $nodeVersion = (& node --version) 2>$null
    } catch {
        # node not on PATH at all -- expected on a fresh box, not a real error.
        $nodeVersion = $null
    }
    $results += [pscustomobject]@{
        Name            = 'Node.js'
        Installed       = [bool]$nodeVersion
        Version         = $nodeVersion
        AutoInstallable = $true
        Detail          = if ($nodeVersion) { "Found $nodeVersion" } else { 'node not found on PATH' }
    }

    # --- IIS role -----------------------------------------------------------
    $iisFeature = Get-WindowsFeature -Name Web-Server -ErrorAction SilentlyContinue
    $iisInstalled = [bool]($iisFeature -and $iisFeature.InstallState -eq 'Installed')
    $results += [pscustomobject]@{
        Name            = 'IIS (Web-Server role)'
        Installed       = $iisInstalled
        Version         = $null
        AutoInstallable = $true
        Detail          = if ($iisInstalled) { 'Installed' } else { 'Web-Server Windows feature not installed' }
    }

    # --- ASP.NET Core Hosting Bundle (installs the ANCM IIS module) --------
    $ancmPath = Join-Path ${env:ProgramFiles} 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    $ancmInstalled = Test-Path $ancmPath
    $results += [pscustomobject]@{
        Name            = 'ASP.NET Core Hosting Bundle (ANCM)'
        Installed       = $ancmInstalled
        Version         = $null
        AutoInstallable = $true
        Detail          = if ($ancmInstalled) { "Found at $ancmPath" } else { 'aspnetcorev2.dll not found -- IIS cannot host the API without this' }
    }

    # --- URL Rewrite Module (not a Windows feature -- its own MSI) ----------
    $rewriteInstalled = [bool](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\IIS Extensions\URL Rewrite' -ErrorAction SilentlyContinue)
    $results += [pscustomobject]@{
        Name            = 'IIS URL Rewrite Module'
        Installed       = $rewriteInstalled
        Version         = $null
        AutoInstallable = $true
        Detail          = if ($rewriteInstalled) { 'Installed' } else { 'Needed for the SPA client-side-routing fallback rule' }
    }

    # --- SQL Server reachability (never auto-installed) ---------------------
    # Deliberately not checked here (needs a real connection string, gathered
    # later in the main flow) -- see Test-BlueTrackSqlConnection in
    # BlueTrack.Database.psm1. Recorded here only as a reminder this module
    # never attempts to install the Database Engine itself.
    $results += [pscustomobject]@{
        Name            = 'SQL Server Database Engine'
        Installed       = $null
        Version         = $null
        AutoInstallable = $false
        Detail          = 'Verified separately once a connection string is known (BlueTrack.Database.psm1) -- this script never installs SQL Server itself.'
    }

    # --- CyberArk Application Password SDK (soft check only) ---------------
    $cpSdkPath = Join-Path ${env:ProgramFiles} 'CyberArk\ApplicationPasswordSdk\NetStandardPasswordSDK.dll'
    $cpInstalled = Test-Path $cpSdkPath
    $results += [pscustomobject]@{
        Name            = 'CyberArk Application Password SDK'
        Installed       = $cpInstalled
        Version         = $null
        AutoInstallable = $false
        Detail          = if ($cpInstalled) { "Found at $cpSdkPath" } else { 'Only required if CyberArk Credential Provider (CP) will be the active secrets backend -- Windows DPAPI (the default) does not need this' }
    }

    return $results
}

function Install-BlueTrackPrerequisite {
    <#
    .SYNOPSIS
        Attempts to install one auto-installable prerequisite, identified by Name
        from Test-BlueTrackPrerequisite's output.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$Name
    )

    switch ($Name) {
        '.NET 10 SDK' {
            if ($PSCmdlet.ShouldProcess('.NET 10 SDK', 'Install via winget')) {
                if (Get-Command winget -ErrorAction SilentlyContinue) {
                    winget install --id Microsoft.DotNet.SDK.10 --silent --accept-package-agreements --accept-source-agreements
                } else {
                    throw "winget is not available on this box -- install the .NET 10 SDK manually from https://dotnet.microsoft.com/download and re-run."
                }
            }
        }
        'Node.js' {
            if ($PSCmdlet.ShouldProcess('Node.js', 'Install via winget')) {
                if (Get-Command winget -ErrorAction SilentlyContinue) {
                    winget install --id OpenJS.NodeJS.LTS --silent --accept-package-agreements --accept-source-agreements
                } else {
                    throw "winget is not available on this box -- install Node.js manually from https://nodejs.org and re-run."
                }
            }
        }
        'IIS (Web-Server role)' {
            if ($PSCmdlet.ShouldProcess('Web-Server role + required sub-features', 'Install-WindowsFeature')) {
                Install-WindowsFeature -Name Web-Server, Web-Static-Content, Web-Default-Doc, Web-Http-Errors, `
                    Web-Http-Redirect, Web-Http-Logging, Web-Request-Monitor, Web-Filtering, Web-Mgmt-Console -IncludeManagementTools
            }
        }
        'ASP.NET Core Hosting Bundle (ANCM)' {
            if ($PSCmdlet.ShouldProcess('ASP.NET Core Hosting Bundle', 'Download and silently install')) {
                $installerUrl = 'https://dot.net/v1/dotnet-hosting-win.exe' # Microsoft's own stable "latest" redirect
                $installerPath = Join-Path $env:TEMP 'dotnet-hosting-bundle.exe'
                Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing
                Start-Process -FilePath $installerPath -ArgumentList '/quiet', '/norestart' -Wait
                Remove-Item $installerPath -ErrorAction SilentlyContinue
                Write-Warning 'The ASP.NET Core Hosting Bundle installer may require an IIS/W3SVC restart (net stop was/net start w3svc) to take effect -- a full reboot is the safest option if this is a fresh box.'
            }
        }
        'IIS URL Rewrite Module' {
            if ($PSCmdlet.ShouldProcess('IIS URL Rewrite Module', 'Download and silently install')) {
                # Microsoft's own stable download link for URL Rewrite 2.1 (x64).
                $installerUrl = 'https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi'
                $installerPath = Join-Path $env:TEMP 'urlrewrite.msi'
                Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing
                Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i', "`"$installerPath`"", '/quiet', '/norestart' -Wait
                Remove-Item $installerPath -ErrorAction SilentlyContinue
            }
        }
        default {
            throw "'$Name' is not an auto-installable prerequisite. See its Detail message from Test-BlueTrackPrerequisite for what to do manually."
        }
    }
}

Export-ModuleMember -Function Test-BlueTrackPrerequisite, Install-BlueTrackPrerequisite
