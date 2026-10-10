<#
.SYNOPSIS
    SQL Server connectivity, invoking App/Migrator, environment-specific
    appsettings, and the optional nightly Import/Load Agent job.
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
        [pscredential]$SqlCredential
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Server'] = $SqlServerInstance
    $builder['Database'] = $DatabaseName
    $builder['TrustServerCertificate'] = $true

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
        [Parameter(Mandatory)] [string]$ConnectionString
    )

    $configPath = Join-Path $PublishDirectory "appsettings.$Environment.json"
    $config = [ordered]@{
        ConnectionStrings = [ordered]@{
            BlueTrackDb = $ConnectionString
        }
    }

    if ($PSCmdlet.ShouldProcess($configPath, 'Write environment-specific appsettings')) {
        $config | ConvertTo-Json -Depth 5 | Set-Content -Path $configPath -Encoding UTF8
    }
}

function Install-BlueTrackNightlyJob {
    <#
    .SYNOPSIS
        Installs the nightly Import+Load SQL Agent job by generating a temp
        copy of Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql with the two
        hardcoded literals (export folder, EVD database name) substituted for
        this environment's real values, then running it via sqlcmd.
    .DESCRIPTION
        The tracked script file is never modified -- only a generated temp copy is.
        Confirmed directly against the script's own source: @Folder and the EVD
        database name are literal T-SQL inside the job step's @command text, not
        sqlcmd -v variables (only $(DatabaseName) is a real substitutable variable).

        D-176: -ImportSources sets the step's @ImportPrivilegeCloud /
        @ImportSelfHosted flags the same way. The export folder is only
        needed for Privilege Cloud and the EVD database only for Self-Hosted;
        a source that's off keeps the script's placeholder value, which
        usp_Import_All then ignores.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$SqlServerInstance,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [ValidateSet('Both', 'PrivilegeCloud', 'SelfHosted')] [string]$ImportSources = 'Both',
        [string]$ExportFolderPath,
        [string]$EvdDatabaseName
    )

    $importPrivilegeCloud = $ImportSources -in 'Both', 'PrivilegeCloud'
    $importSelfHosted = $ImportSources -in 'Both', 'SelfHosted'
    if ($importPrivilegeCloud -and -not $ExportFolderPath) { throw "ImportSources '$ImportSources' includes Privilege Cloud, so -ExportFolderPath is required." }
    if ($importSelfHosted -and -not $EvdDatabaseName) { throw "ImportSources '$ImportSources' includes Self-Hosted, so -EvdDatabaseName is required." }

    $sourceScript = Join-Path $RepoRoot 'Database\Manual\04_BlueTrack_ScheduleImportLoadJob.sql'
    if (-not (Test-Path $sourceScript)) {
        throw "Could not find '$sourceScript'."
    }

    $content = Get-Content -Path $sourceScript -Raw

    # These are the exact two literals confirmed in the script's own Step 1
    # @command text -- substituted as plain string replacements, not regex,
    # so a change to either literal's exact text upstream fails loudly here
    # (via -replace simply not matching, followed by the "unchanged" guard
    # below) rather than silently producing a job pointed at the wrong data.
    $originalFolderLiteral = "N''C:\Code\aPePAS\Output''"
    $originalEvdLiteral = "N''CyberArkSH''"

    if ($content -notmatch [regex]::Escape($originalFolderLiteral)) {
        throw "Did not find the expected export-folder literal in 04_BlueTrack_ScheduleImportLoadJob.sql -- the script may have changed upstream. Update this function's substitution logic before continuing."
    }
    if ($content -notmatch [regex]::Escape($originalEvdLiteral)) {
        throw "Did not find the expected EVD-database-name literal in 04_BlueTrack_ScheduleImportLoadJob.sql -- the script may have changed upstream. Update this function's substitution logic before continuing."
    }

    # The two source flags, exact text in Step 1 (D-176).
    $originalPcFlag = '@ImportPrivilegeCloud = 1,'
    $originalShFlag = '@ImportSelfHosted = 1;'
    if (-not $content.Contains($originalPcFlag) -or -not $content.Contains($originalShFlag)) {
        throw "Did not find the expected @ImportPrivilegeCloud / @ImportSelfHosted lines in 04_BlueTrack_ScheduleImportLoadJob.sql -- the script may have changed upstream. Update this function's substitution logic before continuing."
    }

    $normalizedFolder = '(not used)'
    if ($importPrivilegeCloud) {
        $normalizedFolder = $ExportFolderPath.TrimEnd('\')
        $content = $content.Replace($originalFolderLiteral, "N''$normalizedFolder''")
    }
    $evdShown = '(not used)'
    if ($importSelfHosted) {
        $evdShown = $EvdDatabaseName
        $content = $content.Replace($originalEvdLiteral, "N''$EvdDatabaseName''")
    }
    $content = $content.Replace($originalPcFlag, "@ImportPrivilegeCloud = $([int]$importPrivilegeCloud),")
    $content = $content.Replace($originalShFlag, "@ImportSelfHosted = $([int]$importSelfHosted);")

    $tempScript = Join-Path $env:TEMP "04_BlueTrack_ScheduleImportLoadJob.$([guid]::NewGuid()).sql"
    Set-Content -Path $tempScript -Value $content -Encoding UTF8

    try {
        Write-Host "Generated a temp copy of the nightly job script with:`n  Sources:       $ImportSources`n  Export folder: $normalizedFolder`n  EVD database:  $evdShown`n(the tracked repo file was not modified)"
        if ($PSCmdlet.ShouldProcess("$SqlServerInstance / $DatabaseName", 'Install nightly Import+Load SQL Agent job via sqlcmd')) {
            & sqlcmd -S $SqlServerInstance -C -v DatabaseName="$DatabaseName" -i $tempScript | Out-Host
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
        [Parameter(Mandatory)] [string]$Account
    )

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
            & sqlcmd -S $SqlServerInstance -C -b -Q $createLogin | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "sqlcmd failed creating the SQL Server login for '$Account' (exit code $LASTEXITCODE). The installing user needs the securityadmin (or sysadmin) server role."
            }
        }
        if ($PSCmdlet.ShouldProcess("$SqlServerInstance / $DatabaseName", "Run 02_BlueTrack_GrantAppServiceAccountAccess.sql for '$Account'")) {
            # Script 41 starts with :on error exit (D-167), so its first
            # failure stops it and sets a nonzero exit code.
            & sqlcmd -S $SqlServerInstance -C -d $DatabaseName -i $tempScript | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "sqlcmd failed granting '$Account' access to '$DatabaseName' (exit code $LASTEXITCODE) -- see the output above."
            }
        }
    } finally {
        Remove-Item -Path $tempScript -ErrorAction SilentlyContinue -WhatIf:$false
    }
}

Export-ModuleMember -Function Format-BlueTrackConnectionString, Test-BlueTrackSqlConnection, Invoke-BlueTrackMigrator, Set-BlueTrackEnvironmentConfig, Install-BlueTrackNightlyJob, Grant-BlueTrackAppPoolSqlAccess
