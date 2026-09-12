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
        & dotnet run --project $migratorProject --configuration Release --no-build -- $ConnectionString $scriptsPath
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
        Design_Deployment_Methodology.md left this an open question.
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
        copy of Database/14_BlueTrack_ScheduleImportLoadJob.sql with the two
        hardcoded literals (export folder, EVD database name) substituted for
        this environment's real values, then running it via sqlcmd.
    .DESCRIPTION
        The tracked script file is never modified -- only a generated temp copy is.
        Confirmed directly against the script's own source: @Folder and the EVD
        database name are literal T-SQL inside the job step's @command text, not
        sqlcmd -v variables (only $(DatabaseName) is a real substitutable variable).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$SqlServerInstance,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [Parameter(Mandatory)] [string]$ExportFolderPath,
        [Parameter(Mandatory)] [string]$EvdDatabaseName
    )

    $sourceScript = Join-Path $RepoRoot 'Database\14_BlueTrack_ScheduleImportLoadJob.sql'
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
        throw "Did not find the expected export-folder literal in 14_BlueTrack_ScheduleImportLoadJob.sql -- the script may have changed upstream. Update this function's substitution logic before continuing."
    }
    if ($content -notmatch [regex]::Escape($originalEvdLiteral)) {
        throw "Did not find the expected EVD-database-name literal in 14_BlueTrack_ScheduleImportLoadJob.sql -- the script may have changed upstream. Update this function's substitution logic before continuing."
    }

    $normalizedFolder = $ExportFolderPath.TrimEnd('\')
    $newFolderLiteral = "N''$normalizedFolder''"
    $newEvdLiteral = "N''$EvdDatabaseName''"

    $content = $content.Replace($originalFolderLiteral, $newFolderLiteral)
    $content = $content.Replace($originalEvdLiteral, $newEvdLiteral)

    $tempScript = Join-Path $env:TEMP "14_BlueTrack_ScheduleImportLoadJob.$([guid]::NewGuid()).sql"
    Set-Content -Path $tempScript -Value $content -Encoding UTF8

    try {
        Write-Host "Generated a temp copy of the nightly job script with:`n  Export folder: $normalizedFolder`n  EVD database:  $EvdDatabaseName`n(the tracked repo file was not modified)"
        if ($PSCmdlet.ShouldProcess("$SqlServerInstance / $DatabaseName", 'Install nightly Import+Load SQL Agent job via sqlcmd')) {
            & sqlcmd -S $SqlServerInstance -C -v DatabaseName="$DatabaseName" -i $tempScript
            if ($LASTEXITCODE -ne 0) {
                throw "sqlcmd failed installing the nightly job (exit code $LASTEXITCODE)."
            }
        }
    } finally {
        Remove-Item -Path $tempScript -ErrorAction SilentlyContinue
    }
}

Export-ModuleMember -Function Format-BlueTrackConnectionString, Test-BlueTrackSqlConnection, Invoke-BlueTrackMigrator, Set-BlueTrackEnvironmentConfig, Install-BlueTrackNightlyJob
