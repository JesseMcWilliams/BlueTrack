<#
.SYNOPSIS
    SQL Server connectivity, invoking App/Migrator, the site settings file
    (appsettings.Site.json), the Site URL setting, and the optional nightly Import/Load
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

function Set-BlueTrackSiteConfig {
    <#
    .SYNOPSIS
        D-230: writes appsettings.Site.json into the published API folder (the
        connection string, Security:EnableHsts, FileAccess:AllowedRoots and
        BlueTrack:EnvironmentName), then removes the appsettings.<Environment>.json
        earlier installers wrote there. Returns what it did.
    .DESCRIPTION
        IIS runs the API as ASP.NET Core Production, which never loaded an
        appsettings.Test.json, .Staging.json or .Development.json. The API always
        loads appsettings.Site.json instead (Program.cs, SiteSettings.cs), so
        the Environment answer only names the environment (BlueTrack:EnvironmentName).

        An old appsettings.Test/Staging/Development.json was never loaded, so it
        is removed. An old appsettings.Production.json was loaded; it is removed
        only when it holds nothing but the settings this function now writes to
        the site file. Anything else in it (added by hand) is still in effect, so
        the file is kept and a warning names the extra settings.

        Never edits the tracked App/Api/appsettings.json -- only writes into the
        publish output, which is not committed to source control.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string]$PublishDirectory,
        [Parameter(Mandatory)] [ValidateSet('Development', 'Test', 'Staging', 'Production')] [string]$Environment,
        [Parameter(Mandatory)] [string]$ConnectionString,
        # D-197: Security:EnableHsts -- only with a trusted (not self-signed) certificate.
        [bool]$EnableHsts = $false,
        # D-197: FileAccess:AllowedRoots -- folders data feeds may read and backups may go to.
        [string[]]$AllowedFileRoots = @()
    )

    $siteFileName = 'appsettings.Site.json'
    $configPath = Join-Path $PublishDirectory $siteFileName
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
        BlueTrack         = [ordered]@{
            EnvironmentName = $Environment
        }
    }
    $done = New-Object System.Collections.Generic.List[string]

    if ($PSCmdlet.ShouldProcess($configPath, 'Write the site settings file')) {
        $config | ConvertTo-Json -Depth 5 | Set-Content -Path $configPath -Encoding UTF8
        Write-Host "Wrote $configPath (environment name: $Environment)."
        $done.Add("Wrote $siteFileName")

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
            Write-Warning "$siteFileName holds a SQL login's password; its access is now limited to Administrators, SYSTEM and IIS_IUSRS. Windows authentication (-UseWindowsAuth `$true) avoids storing a password at all, and the runtime login should not be the one used to install."
        }
    } else {
        $done.Add("WhatIf: would write $siteFileName")
    }

    # D-230 migration: the file earlier installers wrote for this Environment.
    $oldFileName = "appsettings.$Environment.json"
    $oldPath = Join-Path $PublishDirectory $oldFileName
    if (Test-Path -LiteralPath $oldPath) {
        $keep = $false
        if ($Environment -eq 'Production') {
            # Loaded by ASP.NET Core until now: keep it if it holds anything the site file doesn't.
            $written = @('ConnectionStrings:BlueTrackDb', 'Security:EnableHsts', 'FileAccess:AllowedRoots')
            $extra = @(Get-BlueTrackJsonSettingName -Path $oldPath | Where-Object { $_ -notin $written })
            if ($extra.Count -gt 0) {
                $keep = $true
                Write-Warning "Kept $oldPath`: ASP.NET Core still loads it, and it holds settings the installer doesn't write ($($extra -join ', ')). $siteFileName overrides any setting both files hold. Move those settings to $siteFileName by hand, then delete $oldFileName."
                $done.Add("kept $oldFileName (holds other settings)")
            }
        }
        if (-not $keep) {
            if ($PSCmdlet.ShouldProcess($oldPath, "Remove the old environment settings file (replaced by $siteFileName)")) {
                Remove-Item -LiteralPath $oldPath -Force
                Write-Host "Removed $oldPath; its settings are in $siteFileName now."
                $done.Add("removed the old $oldFileName")
            } else {
                $done.Add("WhatIf: would remove the old $oldFileName")
            }
        }
    }

    return ($done -join '; ')
}

function Get-BlueTrackJsonSettingName {
    <#
    .SYNOPSIS
        Lists a JSON settings file's settings as configuration keys
        (Section:Key), stopping at arrays -- for Set-BlueTrackSiteConfig's
        check of an old appsettings.Production.json.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string]$Path
    )

    $stack = New-Object System.Collections.Stack
    $stack.Push(@('', (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)))
    while ($stack.Count -gt 0) {
        $prefix, $node = $stack.Pop()
        if ($node -is [System.Management.Automation.PSCustomObject]) {
            foreach ($property in $node.PSObject.Properties) {
                $name = if ($prefix) { "$prefix`:$($property.Name)" } else { $property.Name }
                $stack.Push(@($name, $property.Value))
            }
        } elseif ($prefix) {
            $prefix
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
        needed for Privilege Cloud; a source that's off is saved without its
        value. D-278: Self-Hosted is Self-Hosted Direct, whose PVWA address and
        credential are set on Admin > Data Sources (the nightly import fails,
        saying so, until they are).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$SqlServerInstance,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [ValidateSet('Both', 'PrivilegeCloud', 'SelfHosted')] [string]$ImportSources = 'Both',
        [string]$ExportFolderPath,
        [bool]$TrustServerCertificate = $true
    )

    # D-197: the folder is pasted into a T-SQL string literal, so refuse a
    # character that would end the literal.
    if ($ExportFolderPath -and $ExportFolderPath.Contains("'")) { throw "The export folder path can't contain an apostrophe: $ExportFolderPath" }

    $importPrivilegeCloud = $ImportSources -in 'Both', 'PrivilegeCloud'
    $importSelfHosted = $ImportSources -in 'Both', 'SelfHosted'
    if ($importPrivilegeCloud -and -not $ExportFolderPath) { throw "ImportSources '$ImportSources' includes Privilege Cloud, so -ExportFolderPath is required." }

    $sourceScript = Join-Path $RepoRoot 'Database\Manual\04_BlueTrack_ScheduleImportLoadJob.sql'
    if (-not (Test-Path $sourceScript)) {
        throw "Could not find '$sourceScript'."
    }

    $content = Get-Content -Path $sourceScript -Raw

    # Plain string replacements; a token missing upstream fails loudly here
    # rather than producing a job with the wrong settings.
    $folderValue = if ($importPrivilegeCloud) { $ExportFolderPath.TrimEnd('\') } else { '' }
    $tokens = [ordered]@{
        '__IMPORT_PC__'     = [string][int]$importPrivilegeCloud
        '__IMPORT_SH__'     = [string][int]$importSelfHosted
        '__EXPORT_FOLDER__' = $folderValue
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
        Write-Host "Generated a temp copy of the nightly job script with:`n  Sources:       $ImportSources`n  Export folder: $folderShown`n(saved as the CyberArk source settings only if none exist yet; the tracked repo file was not modified)"
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

function Test-BlueTrackEvdSourceInUse {
    <#
    .SYNOPSIS
        D-278: true when the target database exists and its CyberArk source
        settings still import Self-Hosted from the EVD, which this version no
        longer has. A database that doesn't exist yet, or predates the setting,
        is false.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$DatabaseName
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    $builder['Database'] = 'master'
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $connection.Open()
        $exists = $connection.CreateCommand()
        $exists.CommandText = 'SELECT DB_ID(@DatabaseName)'
        $exists.Parameters.AddWithValue('@DatabaseName', $DatabaseName) | Out-Null
        $dbId = $exists.ExecuteScalar()
        if ($null -eq $dbId -or $dbId -is [System.DBNull]) { return $false }
    } finally {
        $connection.Close()
    }

    $builder['Database'] = $DatabaseName
    $target = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $target.Open()
        $check = $target.CreateCommand()
        # Dynamic SQL: the column doesn't exist on a database older than script 38.
        $check.CommandText = "IF COL_LENGTH('web.cyberark_source_config', 'SelfHostedSource') IS NULL SELECT 0; " +
            "ELSE EXEC (N'SELECT COUNT(*) FROM web.cyberark_source_config WHERE ImportSelfHosted = 1 AND SelfHostedSource <> N''Direct''');"
        return [int]$check.ExecuteScalar() -gt 0
    } finally {
        $target.Close()
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

function Install-BlueTrackSqlServerAudit {
    <#
    .SYNOPSIS
        D-295: records every UPDATE and DELETE on the audit log's tables in the
        Windows Security log (SQL Server Audit, Database/Manual/07).
    .DESCRIPTION
        Microsoft's three requirements for an audit written to the Security log
        ("Write SQL Server Audit events to the Security log"), set here when SQL
        Server is this machine:
          1. the audit policy subcategory Application Generated on for success
             and failure (auditpol, by its GUID so it works in any Windows
             language);
          2. the SQL Server service account holding "Generate security audits"
             (SeAuditPrivilege; secedit) -- it takes effect when SQL Server
             restarts, which this function does not do;
          3. that account allowed full control of
             HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Security.
        Then it runs Manual/07 and reports the audit's status. When SQL Server
        is on another machine, those three are for that machine's administrator
        (the function says so) and only Manual/07 runs. A domain audit policy
        can override step 1; check it with
        auditpol /get /subcategory:"Application Generated".
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$SqlServerInstance,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [bool]$TrustServerCertificate = $true
    )

    $scriptPath = Join-Path $RepoRoot 'Database\Manual\07_BlueTrack_SqlServerAudit.sql'
    if (-not (Test-Path $scriptPath)) { throw "Could not find '$scriptPath'." }
    [string[]]$trustArgs = if ($TrustServerCertificate) { @('-C') } else { @() }
    $restartNeeded = $false

    if (Test-BlueTrackSqlInstanceIsThisMachine -SqlServerInstance $SqlServerInstance) {
        # All the output first: Select-Object -First 1 in the pipeline would stop sqlcmd and leave a failing exit code.
        $output = @(& sqlcmd -S $SqlServerInstance @trustArgs -b -h -1 -W -Q "SET NOCOUNT ON; SELECT TOP (1) service_account FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server (%'")
        $account = $output | Select-Object -First 1
        if ($LASTEXITCODE -ne 0 -or -not $account) { throw 'Could not read the SQL Server service account (sys.dm_server_services needs VIEW SERVER STATE).' }
        $account = $account.Trim()
        $sid = ([System.Security.Principal.NTAccount]$account).Translate([System.Security.Principal.SecurityIdentifier]).Value
        Write-Host "SQL Server runs as $account ($sid)."

        # 1. Audit policy: Application Generated ({0CCE9222-...}), success and failure.
        if ($PSCmdlet.ShouldProcess('Windows audit policy', 'Turn on Application Generated (success and failure)')) {
            & auditpol.exe /set /subcategory:'{0CCE9222-69AE-11D9-BED3-505054503030}' /success:enable /failure:enable | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "auditpol failed (exit code $LASTEXITCODE)." }
        }

        # 2. "Generate security audits" for the service account, unless it already has it.
        $export = Join-Path $env:TEMP "BlueTrack.secpol.$([guid]::NewGuid()).inf"
        $database = Join-Path $env:TEMP "BlueTrack.secpol.$([guid]::NewGuid()).sdb"
        try {
            & secedit.exe /export /cfg $export /areas USER_RIGHTS /quiet | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "secedit /export failed (exit code $LASTEXITCODE)." }
            $lines = @(Get-Content -Path $export)
            $index = [array]::FindIndex($lines, [Predicate[string]] { param($line) $line -match '^SeAuditPrivilege\s*=' })
            $holders = if ($index -ge 0) { @(($lines[$index] -split '=', 2)[1].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { @() }
            # secedit lists a holder as *SID or by name (a domain account as its bare name, e.g. MSSQL_Service), so compare SIDs.
            $holderSids = @($holders | ForEach-Object {
                    if ($_.StartsWith('*')) { $_.Substring(1) }
                    else { try { ([System.Security.Principal.NTAccount]$_).Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { $null } }
                })
            if ($sid -in $holderSids) {
                Write-Host "$account already holds Generate security audits."
            } elseif ($PSCmdlet.ShouldProcess($account, 'Grant Generate security audits (SeAuditPrivilege)')) {
                $line = 'SeAuditPrivilege = ' + ((@($holders) + "*$sid") -join ',')
                $rights = @('[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"', 'Revision=1', '[Privilege Rights]', $line)
                Set-Content -Path $export -Value $rights -Encoding Unicode
                & secedit.exe /configure /db $database /cfg $export /areas USER_RIGHTS /quiet | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "secedit /configure failed (exit code $LASTEXITCODE)." }
                $restartNeeded = $true
                Write-Host "Granted Generate security audits to $account."
            }
        } finally {
            Remove-Item -Path $export, $database -ErrorAction SilentlyContinue -WhatIf:$false
            Remove-Item -Path ($database -replace '\.sdb$', '.jfm') -ErrorAction SilentlyContinue -WhatIf:$false
        }

        # 3. Full control of the Security event log's registry key.
        $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Security'
        $acl = Get-Acl -Path $key
        $has = $acl.Access | Where-Object {
            $_.AccessControlType -eq 'Allow' -and ($_.RegistryRights -band [System.Security.AccessControl.RegistryRights]::FullControl) -eq [System.Security.AccessControl.RegistryRights]::FullControl -and
            $(try { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -eq $sid } catch { $false })
        }
        if ($has) {
            Write-Host "$account already has full control of the Security event log key."
        } elseif ($PSCmdlet.ShouldProcess($key, "Give $account full control")) {
            $rule = New-Object System.Security.AccessControl.RegistryAccessRule (
                [System.Security.Principal.SecurityIdentifier]$sid, 'FullControl', 'ContainerInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
            Set-Acl -Path $key -AclObject $acl
            Write-Host "Gave $account full control of the Security event log key."
        }
    } else {
        Write-Warning "SQL Server ($SqlServerInstance) is on another machine. Its administrator must turn on the audit policy Application Generated, give the SQL Server service account 'Generate security audits' and full control of HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Security, and restart SQL Server (Deploy/README.md, Db.SqlServerAudit)."
    }

    if ($PSCmdlet.ShouldProcess("$SqlServerInstance / $DatabaseName", 'Create the SQL Server audit of the audit log tables (Database/Manual/07)')) {
        & sqlcmd -S $SqlServerInstance @trustArgs -b -I -v DatabaseName="$DatabaseName" -i $scriptPath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed running 07_BlueTrack_SqlServerAudit.sql (exit code $LASTEXITCODE)." }
        $status = @(& sqlcmd -S $SqlServerInstance @trustArgs -b -h -1 -W -Q "SET NOCOUNT ON; SELECT status_desc FROM sys.dm_server_audit_status WHERE name = N'BlueTrack ($DatabaseName) - Audit Log Changes'") | Select-Object -First 1
        if ("$status".Trim() -eq 'STARTED') {
            Write-Host 'The SQL Server audit of the audit log is running (Windows Security log).'
        } elseif ($restartNeeded) {
            Write-Warning "The SQL Server audit is created but not writing yet ($status): restart the SQL Server service so the new 'Generate security audits' right takes effect, then run -Step Db.SqlServerAudit again to check."
        } else {
            Write-Warning "The SQL Server audit is created but not writing ($status). Check the three Windows requirements (Deploy/README.md, Db.SqlServerAudit) and the SQL Server error log, restart SQL Server if a right was just granted, then run -Step Db.SqlServerAudit again."
        }
    }
}

Export-ModuleMember -Function Format-BlueTrackConnectionString, Test-BlueTrackSqlConnection, Test-BlueTrackSqlInstanceIsThisMachine, Invoke-BlueTrackMigrator, Set-BlueTrackSiteConfig, Install-BlueTrackNightlyJob, Test-BlueTrackEvdSourceInUse, Grant-BlueTrackAppPoolSqlAccess, Get-BlueTrackSiteUrl, Set-BlueTrackSiteUrl, Install-BlueTrackSqlServerAudit
