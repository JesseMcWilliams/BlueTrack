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

    Installer sources (D-168), in the order -Source Auto tries them:
      1. Offline: a pre-downloaded installer in -InstallerSourcePath, for
         servers without internet access. Verified against a sidecar
         <file>.sha256 / <file>.sha512 if one is present.
      2. winget, when it's on PATH (.NET SDK and Node.js only).
      3. Download: the vendor's own release metadata (Microsoft's
         release-metadata/10.0/releases.json, nodejs.org/dist/index.json),
         hash-verified against the hash that metadata publishes. The URL
         Rewrite MSI has no published hash, so it's checked by Authenticode
         signature instead.
    Every downloaded or offline installer must also carry a valid Authenticode
    signature before it's run.
#>

Set-StrictMode -Version Latest

# Each auto-installable item: how to find it offline, its winget id (if any),
# and how to run its installer silently. Keyed by the same Key that
# Test-BlueTrackPrerequisite reports, which is also the Install-BlueTrack.ps1
# step name suffix (Prereq.<Key>).
$script:InstallerCatalog = @{
    DotNetSdk     = @{ OfflinePattern = 'dotnet-sdk-10.*-win-x64.exe'; WingetId = 'Microsoft.DotNet.SDK.10'; Kind = 'Exe' }
    NodeJs        = @{ OfflinePattern = 'node-v*-x64.msi'; WingetId = 'OpenJS.NodeJS.LTS'; Kind = 'Msi' }
    HostingBundle = @{ OfflinePattern = 'dotnet-hosting-10.*-win.exe'; WingetId = $null; Kind = 'Exe' }
    UrlRewrite    = @{ OfflinePattern = 'rewrite_amd64*.msi'; WingetId = $null; Kind = 'Msi' }
}

function Test-BlueTrackPrerequisite {
    <#
    .SYNOPSIS
        Runs every prerequisite check and returns a status object per item.
    .OUTPUTS
        [pscustomobject[]] with Key/Name/Installed/Version/AutoInstallable/Detail
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
        Key             = 'DotNetSdk'
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
        Key             = 'NodeJs'
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
        Key             = 'Iis'
        Name            = 'IIS (Web-Server role)'
        Installed       = $iisInstalled
        Version         = $null
        AutoInstallable = $true
        Detail          = if ($iisInstalled) { 'Installed' } else { 'Web-Server Windows feature not installed' }
    }

    # --- IIS Windows Authentication role service (D-162) --------------------
    # BlueTrack.Api defers Windows Integrated Auth to IIS's own native
    # handshake when IIS-hosted (AuthenticationExtensions.cs's
    # IsIisHosted()/GetPrimaryAuthenticationScheme()) rather than running its
    # own Negotiate handler, which cannot coexist with IIS/ANCM at all --
    # this Windows feature is what actually lets an IIS site's
    # windowsAuthentication setting be turned on.
    $windowsAuthFeature = Get-WindowsFeature -Name Web-Windows-Auth -ErrorAction SilentlyContinue
    $windowsAuthInstalled = [bool]($windowsAuthFeature -and $windowsAuthFeature.InstallState -eq 'Installed')
    $results += [pscustomobject]@{
        Key             = 'IisWindowsAuth'
        Name            = 'IIS Windows Authentication'
        Installed       = $windowsAuthInstalled
        Version         = $null
        AutoInstallable = $true
        Detail          = if ($windowsAuthInstalled) { 'Installed' } else { 'Web-Windows-Auth Windows feature not installed -- needed for BlueTrack.Api to defer Windows Integrated Auth to IIS when IIS-hosted (D-162)' }
    }

    # --- ASP.NET Core Hosting Bundle (installs the ANCM IIS module) --------
    $ancmPath = Join-Path ${env:ProgramFiles} 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    $ancmInstalled = Test-Path $ancmPath
    $results += [pscustomobject]@{
        Key             = 'HostingBundle'
        Name            = 'ASP.NET Core Hosting Bundle (ANCM)'
        Installed       = $ancmInstalled
        Version         = $null
        AutoInstallable = $true
        Detail          = if ($ancmInstalled) { "Found at $ancmPath" } else { 'aspnetcorev2.dll not found -- IIS cannot host the API without this' }
    }

    # --- URL Rewrite Module (not a Windows feature -- its own MSI) ----------
    $rewriteInstalled = [bool](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\IIS Extensions\URL Rewrite' -ErrorAction SilentlyContinue)
    $results += [pscustomobject]@{
        Key             = 'UrlRewrite'
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
        Key             = 'SqlServer'
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
        Key             = 'CyberArkSdk'
        Name            = 'CyberArk Application Password SDK'
        Installed       = $cpInstalled
        Version         = $null
        AutoInstallable = $false
        Detail          = if ($cpInstalled) { "Found at $cpSdkPath" } else { 'Only required if CyberArk Credential Provider (CP) will be the active secrets backend -- without it the API builds without the CP backend (D-169); Windows DPAPI (the default) does not need this' }
    }

    return $results
}

function Sync-BlueTrackSessionPath {
    <#
    .SYNOPSIS
        Reloads PATH from the registry, so a tool an installer just added
        (dotnet, node, npm) is callable in this same session without
        reopening PowerShell.
    #>
    [CmdletBinding()]
    param()

    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($machinePath, $userPath) | Where-Object { $_ }) -join ';'
}

function Get-BlueTrackInstallerDownload {
    <#
    .SYNOPSIS
        Resolves the current vendor download URL (and published hash, where the
        vendor publishes one) for one auto-installable prerequisite.
    .OUTPUTS
        [pscustomobject] with Url/FileName/HashAlgorithm/Hash (hash may be $null)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidateSet('DotNetSdk', 'NodeJs', 'HostingBundle', 'UrlRewrite')] [string]$Key
    )

    switch ($Key) {
        { $_ -in 'DotNetSdk', 'HostingBundle' } {
            # Microsoft's own per-channel release metadata. Each file entry
            # carries its versioned URL and a SHA-512 hash. (The older
            # https://dot.net/v1/dotnet-hosting-win.exe "latest" link this
            # module used to use redirects to the dotnet.microsoft.com home
            # page, so it downloaded HTML instead of an installer.)
            $metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -UseBasicParsing
            $latest = $metadata.releases | Where-Object { $_.'release-version' -eq $metadata.'latest-release' } | Select-Object -First 1
            if (-not $latest) { throw "Could not find release $($metadata.'latest-release') in the .NET 10.0 release metadata." }
            if ($Key -eq 'DotNetSdk') {
                $file = $latest.sdk.files | Where-Object { $_.name -eq 'dotnet-sdk-win-x64.exe' } | Select-Object -First 1
            } else {
                $file = $latest.'aspnetcore-runtime'.files | Where-Object { $_.name -eq 'dotnet-hosting-win.exe' } | Select-Object -First 1
            }
            if (-not $file) { throw "The .NET 10.0 release metadata has no Windows installer entry for $Key." }
            return [pscustomobject]@{
                Url           = $file.url
                FileName      = Split-Path $file.url -Leaf
                HashAlgorithm = 'SHA512'
                Hash          = $file.hash
            }
        }
        'NodeJs' {
            $index = Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json' -UseBasicParsing
            $lts = $index | Where-Object { $_.lts } | Select-Object -First 1
            if (-not $lts) { throw 'Could not find an LTS release in https://nodejs.org/dist/index.json.' }
            $version = $lts.version
            $fileName = "node-$version-x64.msi"
            $sums = (Invoke-WebRequest -Uri "https://nodejs.org/dist/$version/SHASUMS256.txt" -UseBasicParsing).Content
            $line = ($sums -split "`n") | Where-Object { $_ -match "\s$([regex]::Escape($fileName))\s*$" } | Select-Object -First 1
            if (-not $line) { throw "SHASUMS256.txt for Node.js $version has no entry for $fileName." }
            return [pscustomobject]@{
                Url           = "https://nodejs.org/dist/$version/$fileName"
                FileName      = $fileName
                HashAlgorithm = 'SHA256'
                Hash          = ($line -split '\s+')[0]
            }
        }
        'UrlRewrite' {
            # Microsoft's own stable download link for URL Rewrite 2.1 (x64).
            # No published hash; Invoke-BlueTrackInstaller's Authenticode
            # check is what verifies this one.
            $url = 'https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi'
            return [pscustomobject]@{
                Url           = $url
                FileName      = Split-Path $url -Leaf
                HashAlgorithm = $null
                Hash          = $null
            }
        }
    }
}

function Find-BlueTrackOfflineInstaller {
    <#
    .SYNOPSIS
        Finds a pre-downloaded installer for one prerequisite in
        -InstallerSourcePath, plus its sidecar hash if one is present.
    .OUTPUTS
        [pscustomobject] with Path/HashAlgorithm/Hash, or $null if not found.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Key,
        [Parameter(Mandatory)] [string]$InstallerSourcePath
    )

    if (-not (Test-Path $InstallerSourcePath -PathType Container)) {
        throw "-InstallerSourcePath '$InstallerSourcePath' does not exist or is not a folder."
    }
    $pattern = $script:InstallerCatalog[$Key].OfflinePattern
    # A folder holding several versions uses the latest one. Sorted by the
    # parsed version, not the name: by name, 10.0.9 sorts after 10.0.12.
    $file = Get-ChildItem -Path $InstallerSourcePath -Filter $pattern -File |
        Sort-Object { if ($_.Name -match '(\d+(\.\d+){1,3})') { [version]$Matches[1] } else { [version]'0.0' } }, Name |
        Select-Object -Last 1
    if (-not $file) { return $null }

    $hashAlgorithm = $null
    $hash = $null
    foreach ($algorithm in 'SHA512', 'SHA256') {
        $sidecar = "$($file.FullName).$($algorithm.ToLowerInvariant())"
        if (Test-Path $sidecar) {
            $hashAlgorithm = $algorithm
            # Accepts either a bare hash or "<hash>  <filename>" (sha256sum style).
            $hash = ((Get-Content $sidecar -Raw).Trim() -split '\s+')[0]
            break
        }
    }

    return [pscustomobject]@{ Path = $file.FullName; HashAlgorithm = $hashAlgorithm; Hash = $hash }
}

function Invoke-BlueTrackInstaller {
    <#
    .SYNOPSIS
        Verifies one installer file (hash if known, Authenticode always), then
        runs it silently and checks its exit code.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [ValidateSet('Exe', 'Msi')] [string]$Kind,
        [string]$HashAlgorithm,
        [string]$Hash
    )

    if ($Hash) {
        $actual = (Get-FileHash -Path $Path -Algorithm $HashAlgorithm).Hash
        if ($actual -ne $Hash.ToUpperInvariant()) {
            throw "$HashAlgorithm hash mismatch for '$Path' (expected $Hash, got $actual). The file is corrupt or not the expected installer -- delete it and re-run."
        }
        Write-Host "  $HashAlgorithm hash verified."
    } else {
        Write-Host '  No published hash for this installer -- relying on the Authenticode signature check.'
    }

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid') {
        throw "'$Path' does not carry a valid Authenticode signature (status: $($signature.Status)). Refusing to run it."
    }
    Write-Host "  Signed by: $($signature.SignerCertificate.Subject)"

    if (-not $PSCmdlet.ShouldProcess($Path, 'Run installer silently')) { return }

    if ($Kind -eq 'Msi') {
        $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i', "`"$Path`"", '/qn', '/norestart' -Wait -PassThru
    } else {
        $process = Start-Process -FilePath $Path -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    }

    # 3010 = success, reboot required; 1641 = success, reboot initiated.
    switch ($process.ExitCode) {
        0 { }
        { $_ -in 3010, 1641 } { Write-Warning "Installer succeeded but reports a reboot is required (exit code $($process.ExitCode))." }
        default { throw "Installer '$Path' failed with exit code $($process.ExitCode)." }
    }
}

function Install-BlueTrackPrerequisite {
    <#
    .SYNOPSIS
        Attempts to install one auto-installable prerequisite, identified by Key
        from Test-BlueTrackPrerequisite's output.
    .PARAMETER Source
        Auto (default): offline folder if -InstallerSourcePath is given, else
        winget where available and supported, else vendor download.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$Key,
        [ValidateSet('Auto', 'Offline', 'Winget', 'Download')] [string]$Source = 'Auto',
        [string]$InstallerSourcePath
    )

    switch ($Key) {
        'Iis' {
            if ($PSCmdlet.ShouldProcess('Web-Server role + required sub-features', 'Install-WindowsFeature')) {
                Install-WindowsFeature -Name Web-Server, Web-Static-Content, Web-Default-Doc, Web-Http-Errors, `
                    Web-Http-Redirect, Web-Http-Logging, Web-Request-Monitor, Web-Filtering, Web-Mgmt-Console -IncludeManagementTools
            }
            return
        }
        'IisWindowsAuth' {
            if ($PSCmdlet.ShouldProcess('Web-Windows-Auth role service', 'Install-WindowsFeature')) {
                Install-WindowsFeature -Name Web-Windows-Auth
            }
            return
        }
    }

    if (-not $script:InstallerCatalog.ContainsKey($Key)) {
        throw "'$Key' is not an auto-installable prerequisite. See its Detail message from Test-BlueTrackPrerequisite for what to do manually."
    }
    $catalog = $script:InstallerCatalog[$Key]

    if ($Source -eq 'Auto') {
        if ($InstallerSourcePath) {
            $Source = 'Offline'
        } elseif ($catalog.WingetId -and (Get-Command winget -ErrorAction SilentlyContinue)) {
            $Source = 'Winget'
        } else {
            $Source = 'Download'
        }
    }
    Write-Host "Installing $Key (source: $Source)..."

    switch ($Source) {
        'Offline' {
            if (-not $InstallerSourcePath) { throw '-PrerequisiteSource Offline needs -InstallerSourcePath.' }
            $offline = Find-BlueTrackOfflineInstaller -Key $Key -InstallerSourcePath $InstallerSourcePath
            if (-not $offline) {
                throw "No installer matching '$($catalog.OfflinePattern)' found in '$InstallerSourcePath'. Download it on a machine with internet access (see Deploy/README.md, 'Offline prerequisites'), copy it there and re-run."
            }
            Write-Host "  Using $($offline.Path)"
            Invoke-BlueTrackInstaller -Path $offline.Path -Kind $catalog.Kind -HashAlgorithm $offline.HashAlgorithm -Hash $offline.Hash
        }
        'Winget' {
            if (-not $catalog.WingetId) { throw "$Key has no winget package; use -PrerequisiteSource Download or Offline." }
            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget is not available on this box; use -PrerequisiteSource Download or Offline.' }
            if ($PSCmdlet.ShouldProcess($catalog.WingetId, 'winget install')) {
                & winget install --id $catalog.WingetId --exact --silent --accept-package-agreements --accept-source-agreements | Out-Host
                if ($LASTEXITCODE -ne 0) { throw "winget install $($catalog.WingetId) failed (exit code $LASTEXITCODE)." }
            }
        }
        'Download' {
            # Windows PowerShell 5.1 doesn't always offer TLS 1.2 by default,
            # and its progress bar slows Invoke-WebRequest downloads severely.
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $ProgressPreference = 'SilentlyContinue'

            $download = Get-BlueTrackInstallerDownload -Key $Key
            $installerPath = Join-Path $env:TEMP $download.FileName
            Write-Host "  Downloading $($download.Url)"
            Invoke-WebRequest -Uri $download.Url -OutFile $installerPath -UseBasicParsing
            try {
                Invoke-BlueTrackInstaller -Path $installerPath -Kind $catalog.Kind -HashAlgorithm $download.HashAlgorithm -Hash $download.Hash
            } finally {
                Remove-Item $installerPath -ErrorAction SilentlyContinue -WhatIf:$false
            }
        }
    }

    Sync-BlueTrackSessionPath
    if ($Key -eq 'HostingBundle') {
        Write-Warning 'The ASP.NET Core Hosting Bundle installer may require an IIS/W3SVC restart (net stop was /y, then net start w3svc) to take effect -- a full reboot is the safest option if this is a fresh box.'
    }
}

Export-ModuleMember -Function Test-BlueTrackPrerequisite, Install-BlueTrackPrerequisite, Get-BlueTrackInstallerDownload
