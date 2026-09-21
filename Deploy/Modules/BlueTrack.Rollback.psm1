<#
.SYNOPSIS
    Backup-before-deploy and emergency restore -- BlueTrack's rollback
    mechanism (Design_Deployment_Methodology.md: Option B, formalize
    backup/restore rather than per-script "down" migrations).
#>

Set-StrictMode -Version Latest

function Backup-BlueTrackForRollback {
    <#
    .SYNOPSIS
        Backs up the BlueTrack database (and, optionally, msdb) to a timestamped
        .bak file, verifies it, and writes a manifest recording what was backed
        up and which commit it preceded.
    .DESCRIPTION
        -IncludeMsdb is opt-in, not automatic: SQL Server Agent job definitions
        (e.g. the nightly Import+Load job, Database/14_BlueTrack_ScheduleImportLoadJob.sql)
        live in msdb, which is shared by every database on the SQL Server
        instance -- restoring it later is an instance-wide action, not scoped
        to just this one BlueTrack database. Only ask for it when you actually
        need job-definition rollback protection, and treat restoring it as a
        separate, more consequential decision than restoring BlueTrack itself.
    .OUTPUTS
        [pscustomobject] with DatabaseBackupPath, ManifestPath, and (when
        -IncludeMsdb) MsdbBackupPath.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [Parameter(Mandatory)] [string]$BackupFolder,
        [switch]$IncludeMsdb,
        [string]$RepoRoot
    )

    if (-not (Test-Path $BackupFolder)) {
        New-Item -ItemType Directory -Path $BackupFolder | Out-Null
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $databaseBackupPath = Join-Path $BackupFolder "$DatabaseName`_$timestamp.bak"

    if ($PSCmdlet.ShouldProcess($databaseBackupPath, "BACKUP DATABASE [$DatabaseName]")) {
        Invoke-BlueTrackBackupDatabase -ConnectionString $ConnectionString -DatabaseName $DatabaseName -BackupFilePath $databaseBackupPath
        Test-BlueTrackBackupFile -ConnectionString $ConnectionString -BackupFilePath $databaseBackupPath
        Write-Host "Backed up and verified: $databaseBackupPath"
    }

    $gitCommit = $null
    if ($RepoRoot -and (Get-Command git -ErrorAction SilentlyContinue)) {
        try {
            $gitCommit = (git -C $RepoRoot rev-parse HEAD 2>$null)
        } catch {
            $gitCommit = $null
        }
    }

    $manifest = [ordered]@{
        DatabaseName      = $DatabaseName
        TimestampUtc      = (Get-Date).ToUniversalTime().ToString('o')
        DatabaseBackupFile = Split-Path -Leaf $databaseBackupPath
        GitCommit         = $gitCommit
        IncludesMsdb      = [bool]$IncludeMsdb
    }

    $msdbBackupPath = $null
    if ($IncludeMsdb) {
        Write-Warning 'Backing up msdb as well. Restoring msdb later is INSTANCE-WIDE -- it affects every database''s SQL Agent job definitions on this SQL Server instance, not just BlueTrack. Only restore it if you have confirmed a job definition was actually lost, and understand that blast radius before doing so.'
        $msdbBackupPath = Join-Path $BackupFolder "msdb_$timestamp.bak"
        if ($PSCmdlet.ShouldProcess($msdbBackupPath, 'BACKUP DATABASE [msdb]')) {
            Invoke-BlueTrackBackupDatabase -ConnectionString $ConnectionString -DatabaseName 'msdb' -BackupFilePath $msdbBackupPath
            Test-BlueTrackBackupFile -ConnectionString $ConnectionString -BackupFilePath $msdbBackupPath
            Write-Host "Backed up and verified: $msdbBackupPath"
        }
        $manifest['MsdbBackupFile'] = Split-Path -Leaf $msdbBackupPath
    }

    $manifestPath = Join-Path $BackupFolder "$DatabaseName`_$timestamp.manifest.json"
    if ($PSCmdlet.ShouldProcess($manifestPath, 'Write rollback manifest')) {
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8
    }

    [pscustomobject]@{
        DatabaseBackupPath = $databaseBackupPath
        ManifestPath        = $manifestPath
        MsdbBackupPath      = $msdbBackupPath
    }
}

function Restore-BlueTrackFromRollback {
    <#
    .SYNOPSIS
        Restores BlueTrack (and, optionally, msdb) from a specific backup file.
    .DESCRIPTION
        Deliberately requires an explicit -BackupFilePath -- there is no
        "latest in folder" auto-detection, since silently picking the wrong
        backup is worse than making the operator name it. This only ever
        handles the database side of a rollback: the previously-deployed
        application build (matching the manifest's GitCommit, if one was
        recorded) must be redeployed separately.
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [Parameter(Mandatory)] [string]$BackupFilePath,
        [string]$RestoreMsdbFilePath
    )

    if (-not (Test-Path $BackupFilePath)) {
        throw "Backup file not found: $BackupFilePath"
    }

    Write-Warning "About to RESTORE DATABASE [$DatabaseName] WITH REPLACE from '$BackupFilePath'. This overwrites the database's current contents and cannot be undone."
    if ($PSCmdlet.ShouldProcess("$DatabaseName (from $BackupFilePath)", 'RESTORE DATABASE ... WITH REPLACE')) {
        Invoke-BlueTrackRestoreDatabase -ConnectionString $ConnectionString -DatabaseName $DatabaseName -BackupFilePath $BackupFilePath
        Write-Host "Restored $DatabaseName from $BackupFilePath."
    }

    if ($RestoreMsdbFilePath) {
        if (-not (Test-Path $RestoreMsdbFilePath)) {
            throw "msdb backup file not found: $RestoreMsdbFilePath"
        }
        Write-Warning 'About to RESTORE DATABASE [msdb] WITH REPLACE. This is INSTANCE-WIDE -- it replaces SQL Agent job definitions for every database on this SQL Server instance, not just BlueTrack. Only proceed if you have confirmed a job definition was actually lost as a result of the BlueTrack restore above.'
        if ($PSCmdlet.ShouldProcess("msdb (from $RestoreMsdbFilePath)", 'RESTORE DATABASE [msdb] ... WITH REPLACE (instance-wide)')) {
            Invoke-BlueTrackRestoreDatabase -ConnectionString $ConnectionString -DatabaseName 'msdb' -BackupFilePath $RestoreMsdbFilePath
            Write-Host "Restored msdb from $RestoreMsdbFilePath."
        }
    }

    Write-Host "`nReminder: this only restored the database. Also redeploy the application build that matches this backup's manifest (GitCommit), if one was recorded, so the app and database versions stay in sync."
}

function Invoke-BlueTrackBackupDatabase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [Parameter(Mandatory)] [string]$BackupFilePath
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    $builder['Database'] = 'master'
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        # BACKUP DATABASE doesn't accept a parameterized database name --
        # bracket-quoted directly (same approach as DeploymentRepository.TriggerBackupAsync).
        $command.CommandText = "BACKUP DATABASE [$DatabaseName] TO DISK = @FullPath WITH INIT"
        $command.Parameters.AddWithValue('@FullPath', $BackupFilePath) | Out-Null
        $command.CommandTimeout = 0
        $command.ExecuteNonQuery() | Out-Null
    } finally {
        $connection.Close()
    }
}

function Test-BlueTrackBackupFile {
    <#
    .SYNOPSIS
        RESTORE VERIFYONLY against a just-written backup file -- confirms it's
        actually usable before trusting it as a rollback point.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$BackupFilePath
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    $builder['Database'] = 'master'
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = 'RESTORE VERIFYONLY FROM DISK = @FullPath'
        $command.Parameters.AddWithValue('@FullPath', $BackupFilePath) | Out-Null
        $command.CommandTimeout = 0
        $command.ExecuteNonQuery() | Out-Null
    } finally {
        $connection.Close()
    }
}

function Invoke-BlueTrackRestoreDatabase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$DatabaseName,
        [Parameter(Mandatory)] [string]$BackupFilePath
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    $builder['Database'] = 'master'
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $connection.Open()

        # Restoring requires no other connection be using the target database --
        # force everyone else off first (a rollback restore is inherently
        # disruptive; this makes that explicit rather than failing with a vague
        # "database in use" error partway through).
        $setSingleUser = $connection.CreateCommand()
        $setSingleUser.CommandText = "IF DB_ID(N'$DatabaseName') IS NOT NULL ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE"
        $setSingleUser.ExecuteNonQuery() | Out-Null

        $restore = $connection.CreateCommand()
        $restore.CommandText = "RESTORE DATABASE [$DatabaseName] FROM DISK = @FullPath WITH REPLACE"
        $restore.Parameters.AddWithValue('@FullPath', $BackupFilePath) | Out-Null
        $restore.CommandTimeout = 0
        $restore.ExecuteNonQuery() | Out-Null

        $setMultiUser = $connection.CreateCommand()
        $setMultiUser.CommandText = "ALTER DATABASE [$DatabaseName] SET MULTI_USER"
        $setMultiUser.ExecuteNonQuery() | Out-Null
    } finally {
        $connection.Close()
    }
}

function Test-BlueTrackDatabaseHasExistingSchema {
    <#
    .SYNOPSIS
        True if the target database already exists and has at least one user
        table -- used to decide whether a pre-deployment backup has anything
        worth backing up (a genuinely fresh/empty database does not).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$DatabaseName
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    $builder['Database'] = 'master'
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $connection.Open()
        # Two-step (does the database exist at all? then, separately, does it
        # have any tables?) rather than one cross-database query -- simpler
        # than dynamic SQL, and this check only runs once per deploy.
        $existsCommand = $connection.CreateCommand()
        $existsCommand.CommandText = 'SELECT DB_ID(@DatabaseName)'
        $existsCommand.Parameters.AddWithValue('@DatabaseName', $DatabaseName) | Out-Null
        $dbId = $existsCommand.ExecuteScalar()
        if ($null -eq $dbId -or $dbId -is [System.DBNull]) {
            return $false
        }
    } finally {
        $connection.Close()
    }

    $targetBuilder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    $targetBuilder['Database'] = $DatabaseName
    $targetConnection = New-Object System.Data.SqlClient.SqlConnection $targetBuilder.ConnectionString
    try {
        $targetConnection.Open()
        $tableCountCommand = $targetConnection.CreateCommand()
        $tableCountCommand.CommandText = 'SELECT COUNT(*) FROM sys.tables'
        $tableCount = [int]$tableCountCommand.ExecuteScalar()
        return $tableCount -gt 0
    } finally {
        $targetConnection.Close()
    }
}

Export-ModuleMember -Function Backup-BlueTrackForRollback, Restore-BlueTrackFromRollback, Test-BlueTrackDatabaseHasExistingSchema
