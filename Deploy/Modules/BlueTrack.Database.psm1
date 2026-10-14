<#
.SYNOPSIS
    SQL Server connectivity, invoking App/Migrator, environment-specific
    appsettings, the Site URL setting, and the optional nightly Import/Load
    Agent job.
#>

Set-StrictMode -Version Latest

function Format-BlueTrackConnectionString {
    <#
    .SYNOPSIS
        Builds the ConnectionStrings:BlueTrackDb value from the gathered inputs.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$SqlServerInstance,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [bool]$UseWindowsAuth = $true,
        [pscredential]$SqlCredential,
        # D-197: skip validating SQL Server's certificate. Harmless on this
        # machine; for a remote server it lets a man-in-the-middle read a SQL
        # login's password. The installer passes its TrustServerCertificate
        # answer (default: only when SQL Server is local).
        [bool]$TrustServerCertificate = $true
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Server'] = $SqlServerInstance
    $builder['Database'] = $DatabaseName
    $builder['TrustServerCertificate'] = $TrustServerCertificate

    if ($UseWindowsAuth) {
        $builder['Integrated Security'] = $true
    } else {
        if (-not $SqlCredential) {
            throw 'SqlCredential is required when -UseWindowsAuth is $false.'
        }
        $builder['User ID'] = $SqlCredential.UserName
        $builder['Password'] = $SqlCredential.GetNetworkCredential().Password
    }

    return $builder.ConnectionString
}

function Test-BlueTrackSqlConnection {
    <#
    .SYNOPSIS
        Opens and immediately closes a connection to confirm SQL Server is
        reachable with the given connection string. Never installs SQL Server --
        that is a deliberate, explicit scope boundary (see BlueTrack.Prereqs.psm1).
    .OUTPUTS
        [bool]
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString
    )

    # Connect to `master`, not the target database, since the target database
    # may not exist yet -- App/Migrator creates it, this check only proves the
    # SQL Server *instance* itself is reachable with these credentials.
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    $builder['Database'] = 'master'

    try {
        $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
        $connection.Open()
        $connection.Close()
        return $true
    } catch {
        Write-Warning "Could not reach SQL Server: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-BlueTrackMigrator {
    <#
    .SYNOPSIS
        Runs App/Migrator against the given scripts folder (Database or
        Database/Test). Safe to re-run -- DbUp's own journal makes this idempotent.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [ValidateSet('Database', 'Database/Test')] [string]$ScriptsFolder
    )

    $migratorProject = Join-Path $RepoRoot 'App\Migrator'
    $scriptsPath = Join-Path $RepoRoot ($ScriptsFolder -replace '/', '\')
    if (-not (Test-Path $scriptsPath)) {
        throw "Scripts folder '$scriptsPath' does not exist."
    }

    if ($PSCmdlet.ShouldProcess($scriptsPath, "App/Migrator against $ScriptsFolder")) {
        # Piped through Out-Host so this function's console output can never
        # leak into a caller's return-value capture -- see BlueTrack.Build.psm1's
        # Invoke-BlueTrackWebBuild for the real bug this defends against.
        # No --no-build: nothing else in the install builds App/Migrator, so on
        # a fresh copy of the repo there's no build to use, and on a reused
        # one --no-build would run a stale build (one from before a script was
        # added to its exclusion list ran that script).
        & dotnet run --project $migratorProject --configuration Release -- $ConnectionString $scriptsPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "App/Migrator failed applying '$ScriptsFolder' (exit code $LASTEXITCODE) -- see the console output above for which script failed."
        }
    }
}

function Set-BlueTrackEnvironmentConfig {
    <#
    .SYNOPSIS
        Writes appsettings.{Environment}.json into the published API output
        directory with the resolved connection string -- ASP.NET Core's own
        built-in environment-specific config convention, applied here since
        Design_Deployment-Methodology.md left this an open question.
    .NOTES
        Never edits the tracked App/Api/appsettings.json -- only writes into
        the publish output, which is not committed to source control.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$PublishDirectory,
        [Parameter(Mandatory)] [ValidateSet('Development', 'Test', 'Staging', 'Production')] [string]$Environment,
        [Parameter(Mandatory)] [string]$ConnectionString,
        # D-197: Security:EnableHsts -- only with a trusted (not self-signed) certificate.
        [bool]$EnableHsts = $false,
        # D-197: FileAccess:AllowedRoots -- folders data feeds may read and backups may go to.
        [string[]]$AllowedFileRoots = @()
    )

    $configPath = Join-Path $PublishDirectory "appsettings.$Environment.json"
    $config = [ordered]@{
        ConnectionStrings = [ordered]@{
            BlueTrackDb = $ConnectionString
        }
        Security          = [ordered]@{
            EnableHsts = $EnableHsts
        }
        FileAccess        = [ordered]@{
            AllowedRoots = @($AllowedFileRoots | Where-Object { $_ })
        }
    }

    if ($PSCmdlet.ShouldProcess($configPath, 'Write environment-specific appsettings')) {
        $config | ConvertTo-Json -Depth 5 | Set-Content -Path $configPath -Encoding UTF8

        # D-197: with SQL authentication the file holds a password. Limit it to
        # Administrators, SYSTEM and IIS's worker processes (IIS_IUSRS holds
        # every app pool's identity), instead of the folder's inherited access.
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
        if ($builder['Password']) {
            $acl = New-Object System.Security.AccessControl.FileSecurity
            $acl.SetAccessRuleProtection($true, $false)
            foreach ($identity in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM') {
                $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule $identity, 'FullControl', 'Allow'))
            }
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule 'BUILTIN\IIS_IUSRS', 'Read', 'Allow'))
            Set-Acl -Path $configPath -AclObject $acl
            Write-Warning "appsettings.$Environment.json holds a SQL login's password; its access is now limited to Administrators, SYSTEM and IIS_IUSRS. Windows authentication (-UseWindowsAuth `$true) avoids storing a password at all, and the runtime login should not be the one used to install."
        }
    }
}

function Test-BlueTrackSqlInstanceIsThisMachine {
    <#
    .SYNOPSIS
        Quick check (no DNS lookups) whether a SQL Server instance name is
        this machine -- for D-197's TrustServerCertificate default, where
        guessing "remote" only means the certificate gets validated.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string]$SqlServerInstance
    )

    $hostPart = (($SqlServerInstance -replace '^[a-zA-Z]+:', '') -split '[\x5C,]' | Select-Object -First 1).Trim()
    if ($hostPart -in '.', '(local)', 'localhost', '127.0.0.1', '::1', '') { return $true }
    return ($hostPart.Split('.')[0] -eq $env:COMPUTERNAME)
}

function Install-BlueTrackNightlyJob {
    <#
    .SYNOPSIS
        Installs the nightly Import+Load SQL Agent job by running a temp copy
        of Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql with its
        four __TOKENS__ filled in from this environment's answers.
    .DESCRIPTION
        The tracked script file is never modified -- only a generated temp copy is.

        D-224: the job's one step is EXEC usp_RunNightlyImportLoad; (Import,
        Load, then the daily snapshot even when either failed). D-211: the
        Import (usp_Import_Configured) reads the sources from
        web.cyberark_source_config. The script writes that row
        from these answers only when none exists; once it does, Admin > Data
        Sources owns it, and the script prints a WARNING if the answers differ.

        D-176: -ImportSources picks the sources. The export folder is only
        needed for Privilege Cloud and the EVD database only for Self-Hosted;
        a source that's off is saved without its value.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$SqlServerInstance,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [ValidateSet('Both', 'PrivilegeCloud', 'SelfHosted')] [string]$ImportSources = 'Both',
        [string]$ExportFolderPath,
        [string]$EvdDatabaseName,
        [bool]$TrustServerCertificate = $true
    )

    # D-197: both values are pasted into T-SQL string literals, so refuse
    # characters that would end the literal.
    if ($ExportFolderPath -and $ExportFolderPath.Contains("'")) { throw "The export folder path can't contain an apostrophe: $ExportFolderPath" }
    if ($EvdDatabaseName -and $EvdDatabaseName -notmatch '^[A-Za-z0-9_]+$') { throw "The EVD database name may contain only letters, digits and underscores: $EvdDatabaseName" }

    $importPrivilegeCloud = $ImportSources -in 'Both', 'PrivilegeCloud'
    $importSelfHosted = $ImportSources -in 'Both', 'SelfHosted'
    if ($importPrivilegeCloud -and -not $ExportFolderPath) { throw "ImportSources '$ImportSources' includes Privilege Cloud, so -ExportFolderPath is required." }
    if ($importSelfHosted -and -not $EvdDatabaseName) { throw "ImportSources '$ImportSources' includes Self-Hosted, so -EvdDatabaseName is required." }

    $sourceScript = Join-Path $RepoRoot 'Database\Manual\04_BlueTrack_ScheduleImportLoadJob.sql'
    if (-not (Test-Path $sourceScript)) {
        throw "Could not find '$sourceScript'."
    }

    $content = Get-Content -Path $sourceScript -Raw

    # Plain string replacements; a token missing upstream fails loudly here
    # rather than producing a job with the wrong settings.
    $folderValue = if ($importPrivilegeCloud) { $ExportFolderPath.TrimEnd('\') } else { '' }
    $evdValue = if ($importSelfHosted) { $EvdDatabaseName } else { '' }
    $tokens = [ordered]@{
        '__IMPORT_PC__'     = [string][int]$importPrivilegeCloud
        '__IMPORT_SH__'     = [string][int]$importSelfHosted
        '__EXPORT_FOLDER__' = $folderValue
        '__EVD_DB__'        = $evdValue
    }
    foreach ($token in $tokens.Keys) {
        if (-not $content.Contains($token)) {
            throw "Did not find $token in 04_BlueTrack_ScheduleImportLoadJob.sql -- the script may have changed upstream. Update this function before continuing."
        }
        $content = $content.Replace($token, $tokens[$token])
    }

    $tempScript = Join-Path $env:TEMP "04_BlueTrack_ScheduleImportLoadJob.$([guid]::NewGuid()).sql"
    Set-Content -Path $tempScript -Value $content -Encoding UTF8

    try {
        $folderShown = if ($folderValue) { $folderValue } else { '(not used)' }
        $evdShown = if ($evdValue) { $evdValue } else { '(not used)' }
        Write-Host "Generated a temp copy of the nightly job script with:`n  Sources:       $ImportSources`n  Export folder: $folderShown`n  EVD database:  $evdShown`n(saved as the CyberArk source settings only if none exist yet; the tracked repo file was not modified)"
        if ($PSCmdlet.ShouldProcess("$SqlServerInstance / $DatabaseName", 'Install nightly Import+Load SQL Agent job via sqlcmd')) {
            [string[]]$trustArgs = if ($TrustServerCertificate) { @("-C") } else { @() }
            & sqlcmd -S $SqlServerInstance @trustArgs -b -v DatabaseName="$DatabaseName" -i $tempScript | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "sqlcmd failed installing the nightly job (exit code $LASTEXITCODE)."
            }
        }
    } finally {
        Remove-Item -Path $tempScript -ErrorAction SilentlyContinue
    }
}

function Grant-BlueTrackAppPoolSqlAccess {
    <#
    .SYNOPSIS
        Gives the IIS Application Pool's account the SQL Server access the API
        needs (D-170): CREATE LOGIN if it's missing, then
        Database/Manual/02_BlueTrack_GrantAppServiceAccountAccess.sql for that account.
    .DESCRIPTION
        Script 41's own rules still apply: least privilege (db_datareader,
        db_datawriter, EXECUTE on dbo/web -- not db_owner), and the tracked
        file is never modified -- only a temp copy with __TARGET_ACCOUNT__
        filled in. Runs via sqlcmd under the installing user's Windows
        login, which needs securityadmin (for CREATE LOGIN) and rights to
        manage users/roles in the target database.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$SqlServerInstance,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [Parameter(Mandatory)] [string]$Account,
        [bool]$TrustServerCertificate = $true
    )
    [string[]]$trustArgs = if ($TrustServerCertificate) { @("-C") } else { @() }   # D-197

    # The account goes into both N'...' literals and [...] identifiers below
    # by plain text substitution, so refuse anything that could break out of
    # either. Real Windows account names never contain these.
    if ($Account -notmatch '^[^\\''\[\]]+\\[^\\''\[\]]+$') {
        throw "'$Account' isn't a DOMAIN\Name account name this step can safely substitute into SQL."
    }

    $sourceScript = Join-Path $RepoRoot 'Database\Manual\02_BlueTrack_GrantAppServiceAccountAccess.sql'
    if (-not (Test-Path $sourceScript)) {
        throw "Could not find '$sourceScript'."
    }

    $createLogin = "IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$Account') CREATE LOGIN [$Account] FROM WINDOWS;"
    $tempScript = Join-Path $env:TEMP "02_BlueTrack_GrantAppServiceAccountAccess.$([guid]::NewGuid()).sql"
    Set-Content -Path $tempScript -Value ((Get-Content -Path $sourceScript -Raw).Replace('__TARGET_ACCOUNT__', $Account)) -Encoding UTF8

    try {
        if ($PSCmdlet.ShouldProcess($SqlServerInstance, "CREATE LOGIN [$Account] FROM WINDOWS (if missing)")) {
            & sqlcmd -S $SqlServerInstance @trustArgs -b -Q $createLogin | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "sqlcmd failed creating the SQL Server login for '$Account' (exit code $LASTEXITCODE). The installing user needs the securityadmin (or sysadmin) server role."
            }
        }
        if ($PSCmdlet.ShouldProcess("$SqlServerInstance / $DatabaseName", "Run 02_BlueTrack_GrantAppServiceAccountAccess.sql for '$Account'")) {
            # Script 41 starts with :on error exit (D-167), so its first
            # failure stops it and sets a nonzero exit code.
            & sqlcmd -S $SqlServerInstance @trustArgs -d $DatabaseName -i $tempScript | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "sqlcmd failed granting '$Account' access to '$DatabaseName' (exit code $LASTEXITCODE) -- see the output above."
            }
        }
    } finally {
        Remove-Item -Path $tempScript -ErrorAction SilentlyContinue -WhatIf:$false
    }
}

function Get-BlueTrackSiteUrl {
    <#
    .SYNOPSIS
        D-224: the site's address from the installer's answers --
        https://<Hostname>, with :<HttpsPort> unless it is 443.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string]$Hostname,
        [ValidateRange(1, 65535)] [int]$HttpsPort = 443
    )

    $name = $Hostname.Trim().ToLowerInvariant()
    $hostType = [System.Uri]::CheckHostName($name)
    if ($hostType -eq [System.UriHostNameType]::Unknown) {
        throw "'$Hostname' isn't a host name the Site URL can be built from."
    }
    if ($hostType -eq [System.UriHostNameType]::IPv6) { $name = "[$name]" }
    if ($HttpsPort -eq 443) { return "https://$name" }
    return "https://${name}:$HttpsPort"
}

function Set-BlueTrackSiteUrl {
    <#
    .SYNOPSIS
        D-224: sets web.app_config.SiteUrl (the address the emails link to)
        only when it is empty, so an address an admin set on Global
        Application Configuration is never overwritten. Returns what it did.
    .DESCRIPTION
        Needs the column from Database/21_BlueTrack_SnapshotAndSiteUrl.sql,
        so it runs after Db.Migrate. The value is passed as a SQL parameter.
        Under -WhatIf it doesn't connect at all (the database may not exist
        yet).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$SiteUrl
    )

    if (-not $PSCmdlet.ShouldProcess('web.app_config.SiteUrl', "Set the Site URL to $SiteUrl if it is empty")) {
        return "WhatIf: would set the Site URL to $SiteUrl if it is empty"
    }

    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = @'
UPDATE web.app_config SET SiteUrl = @SiteUrl WHERE SiteUrl IS NULL OR LTRIM(RTRIM(SiteUrl)) = N'';
SELECT TOP (1) SiteUrl FROM web.app_config;
'@
        [void]$command.Parameters.Add('@SiteUrl', [System.Data.SqlDbType]::NVarChar, 500)
        $command.Parameters['@SiteUrl'].Value = $SiteUrl
        $current = $command.ExecuteScalar()
    } finally {
        $connection.Close()
    }

    if ($null -eq $current -or $current -is [DBNull]) {
        throw 'web.app_config has no row to hold the Site URL. Run the Db.Migrate step first.'
    }
    if ([string]$current -eq $SiteUrl) {
        Write-Host "Site URL: $SiteUrl (the emails link to pages under it; change it on Global Application Configuration)."
        return "Site URL is $SiteUrl"
    }
    Write-Host "Site URL already set to $current on Global Application Configuration; kept (the installer would have used $SiteUrl)."
    return "Kept the Site URL $current"
}

Export-ModuleMember -Function Format-BlueTrackConnectionString, Test-BlueTrackSqlConnection, Test-BlueTrackSqlInstanceIsThisMachine, Invoke-BlueTrackMigrator, Set-BlueTrackEnvironmentConfig, Install-BlueTrackNightlyJob, Grant-BlueTrackAppPoolSqlAccess, Get-BlueTrackSiteUrl, Set-BlueTrackSiteUrl
