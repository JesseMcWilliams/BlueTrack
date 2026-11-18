<#
.SYNOPSIS
    Reports what BlueTrack and its SQL Server use of the server's memory and
    CPU, to send back when a site uses more than expected (D-353).
.DESCRIPTION
    Read-only. Run it on the BlueTrack server, elevated, as an account that
    can read SQL Server's dynamic management views (VIEW SERVER STATE; a
    sysadmin can). Everything printed is also saved to -OutFile.

    Reports:
      - The server: logical CPUs, memory in use and available, when it
        started; where the memory in use is (all processes, the kernel's
        pools, the system cache, modified pages, and the rest) and how much
        is committed.
      - The BlueTrack app pool's worker process (w3wp) and SQL Server
        (sqlservr): working set, private memory, peak working set, CPU time,
        threads, handles and when each started; the top 15 processes by memory.
      - The app pool's recycling limits, and the .NET garbage collector mode
        the API runs with (Server or Workstation, from its runtimeconfig.json
        and web.config).
      - Whether IIS keeps the API loaded (D-366): the pool's start mode
        (AlwaysRunning) and idle time-out (0), preload on the API's
        application, and IIS Application Initialization; a warning for each
        one missing. The System log's IIS (WAS) events for the pool over the
        last 7 days: recycles, idle shutdowns, workers that ended.
      - From the database: the latest API starts (web.api_run), the latest
        Self-Hosted Direct pulls and the Import and Load job's latest runs
        with their step message, to show whether a missing night was the
        API not running.
      - With -SampleSeconds, the worker's and SQL Server's CPU and memory
        every -IntervalSeconds for that long: start it, then run the page or
        query that makes the worker grow, and the samples show by how much.
      - SQL Server's memory and parallelism settings, and the memory it uses;
        a warning when it is short of memory (its target far below max server
        memory, or Windows reporting physical memory low).
      - How far back SQL Server's statement statistics reach (a warning when
        they cover under an hour: memory pressure drops them), then the
        BlueTrack database's statements: the heaviest by CPU, and the ones
        returning the most rows (large results are what grow the worker).
        Each statement's first 200 characters are shown; -NoQueryText leaves
        the text out. D-373: each listed statement's query plan is saved as
        a .sqlplan file in the folder <OutFile>-plans (not with -NoPlans or
        -NoQueryText). Statements are parameterized, so values typed into
        BlueTrack rarely appear, but review the file before sending it.

    Nothing is changed: no setting, no process, no table.
.PARAMETER AppPoolName
    The site's app pool (default BlueTrack-AppPool, the installer's name for site BlueTrack).
.PARAMETER SqlServerInstance
    The SQL Server holding the database (default: this computer).
.PARAMETER DatabaseName
    The BlueTrack database (default BlueTrack).
.PARAMETER SampleSeconds
    Sample the worker and SQL Server for this many seconds (0, the default, takes one reading).
.PARAMETER IntervalSeconds
    Seconds between samples (default 5).
.PARAMETER TopQueries
    How many statements to list in each SQL Server list (default 15).
.PARAMETER NoQueryText
    Leave the statements' text out of the report (and save no query plans).
.PARAMETER NoPlans
    Don't save the listed statements' query plans (D-373). By default each
    is saved as a .sqlplan file in a folder beside -OutFile, to open in SQL
    Server Management Studio: a plan shows why a statement is slow (a scan
    per row, a missing index, a large memory grant). A plan holds the
    statement's text and the values it was compiled for.
.PARAMETER SkipSql
    Don't query SQL Server (for example without VIEW SERVER STATE).
.PARAMETER OutFile
    Where to save the report (default: BlueTrack-ResourceUsage-<computer>-<date-time>.txt in the current folder).
.EXAMPLE
    .\Get-BlueTrackResourceUsage.ps1
    One reading, and the SQL Server lists.
.EXAMPLE
    .\Get-BlueTrackResourceUsage.ps1 -SampleSeconds 300
    Five minutes of samples: run the slow page or query while it runs.
#>
[CmdletBinding()]
param(
    [string]$AppPoolName = 'BlueTrack-AppPool',
    [string]$SqlServerInstance = $env:COMPUTERNAME,
    [string]$DatabaseName = 'BlueTrack',
    [ValidateRange(0, 86400)] [int]$SampleSeconds = 0,
    [ValidateRange(1, 3600)] [int]$IntervalSeconds = 5,
    [ValidateRange(1, 100)] [int]$TopQueries = 15,
    [switch]$NoQueryText,
    [switch]$NoPlans,
    [switch]$SkipSql,
    [string]$OutFile = (Join-Path (Get-Location) ("BlueTrack-ResourceUsage-{0}-{1}.txt" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyyMMdd-HHmm')))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BlueTrackTools.psm1') -Force

$MB = 1MB
$cpuCount = [Environment]::ProcessorCount

function Get-AppPoolProcess {
    # The worker for this app pool: w3wp's command line names it (-ap "<pool>").
    $match = @(Get-CimInstance Win32_Process -Filter "Name = 'w3wp.exe'" | Where-Object { $_.CommandLine -match ('-ap\s+"' + [regex]::Escape($AppPoolName) + '"') })
    if ($match.Count -eq 0) { return $null }
    return Get-Process -Id $match[0].ProcessId -ErrorAction SilentlyContinue
}

function Format-ProcessLine {
    param([Parameter(Mandatory)] [System.Diagnostics.Process]$Process)
    $Process.Refresh()
    $age = (Get-Date) - $Process.StartTime
    $averageCpu = if ($age.TotalSeconds -gt 0) { 100 * $Process.TotalProcessorTime.TotalSeconds / ($age.TotalSeconds * $cpuCount) } else { 0 }
    'PID {0}: working set {1:N0} MB, private {2:N0} MB, peak working set {3:N0} MB, CPU {4:N0} s ({5:N1}% of the server since it started {6:g}), {7:N0} threads, {8:N0} handles' -f `
        $Process.Id, ($Process.WorkingSet64 / $MB), ($Process.PrivateMemorySize64 / $MB), ($Process.PeakWorkingSet64 / $MB),
        $Process.TotalProcessorTime.TotalSeconds, $averageCpu, $Process.StartTime, $Process.Threads.Count, $Process.HandleCount
}

Start-Transcript -Path $OutFile | Out-Null
try {
    Write-Host "BlueTrack resource usage: $env:COMPUTERNAME, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan

    #region The server
    $os = Get-CimInstance Win32_OperatingSystem
    $totalMb = $os.TotalVisibleMemorySize / 1KB
    $freeMb = $os.FreePhysicalMemory / 1KB
    $inUseMb = $totalMb - $freeMb
    $boot = $os.LastBootUpTime
    Write-ToolResult -Name 'Server' -Status Info -Detail ('{0}; {1} logical CPUs; memory {2:N0} MB, {3:N0} MB in use, {4:N0} MB available; started {5:g} ({6:N1} days ago)' -f `
        $os.Caption, $cpuCount, $totalMb, $inUseMb, $freeMb, $boot, ((Get-Date) - $boot).TotalDays)

    # Where the memory in use is: processes' working sets, the kernel's pools, the system file cache, modified pages
    # waiting to be written, and what's left (drivers, locked pages such as SQL Server's with Lock Pages in Memory,
    # or a virtual machine's balloon driver).
    $allProcesses = @(Get-Process)
    $processWsMb = ($allProcesses | Measure-Object WorkingSet64 -Sum).Sum / $MB
    $processPrivateMb = ($allProcesses | Measure-Object PrivateMemorySize64 -Sum).Sum / $MB
    try {
        $perf = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory -ErrorAction Stop
        $pagedMb = $perf.PoolPagedBytes / $MB
        $nonPagedMb = $perf.PoolNonpagedBytes / $MB
        $cacheMb = $perf.CacheBytes / $MB
        $modifiedMb = $perf.ModifiedPageListBytes / $MB
        $otherMb = [math]::Max(0, $inUseMb - $processWsMb - $pagedMb - $nonPagedMb - $cacheMb - $modifiedMb)
        Write-ToolResult -Name 'Memory in use' -Status Info -Detail ('processes {0:N0} MB ({1:N0} processes; shared pages count once per process); kernel pools {2:N0} MB paged, {3:N0} MB nonpaged; system cache {4:N0} MB; modified {5:N0} MB; other {6:N0} MB (drivers, locked pages, a VM balloon driver)' -f `
            $processWsMb, $allProcesses.Count, $pagedMb, $nonPagedMb, $cacheMb, $modifiedMb, $otherMb)
        Write-ToolResult -Name 'Memory committed' -Status Info -Detail ('{0:N0} MB of a {1:N0} MB limit (memory plus page files); processes'' private memory {2:N0} MB' -f `
            ($perf.CommittedBytes / $MB), ($perf.CommitLimit / $MB), $processPrivateMb)
    } catch {
        Write-ToolResult -Name 'Memory in use' -Status Info -Detail ("processes {0:N0} MB ({1:N0} processes); couldn't read the memory counters: {2}" -f $processWsMb, $allProcesses.Count, $_.Exception.Message)
    }
    #endregion

    #region Processes
    $worker = Get-AppPoolProcess
    if ($worker) {
        Write-ToolResult -Name "Worker ($AppPoolName)" -Status Info -Detail (Format-ProcessLine -Process $worker)
    } else {
        Write-ToolResult -Name "Worker ($AppPoolName)" -Status Warn -Detail 'not running (IIS starts it on the first request), or the app pool has another name: see -AppPoolName'
    }
    $sqlProcesses = @(Get-Process -Name sqlservr -ErrorAction SilentlyContinue)
    foreach ($sql in $sqlProcesses) {
        Write-ToolResult -Name 'SQL Server (sqlservr)' -Status Info -Detail (Format-ProcessLine -Process $sql)
    }
    if ($sqlProcesses.Count -eq 0) {
        Write-ToolResult -Name 'SQL Server (sqlservr)' -Status Info -Detail 'not on this computer'
    }
    $top = $allProcesses | Sort-Object WorkingSet64 -Descending | Select-Object -First 15
    Write-Host ''
    Write-Host 'Top processes by working set:'
    $top | Format-Table Id, ProcessName,
        @{ n = 'WorkingSet_MB'; e = { [int]($_.WorkingSet64 / $MB) } },
        @{ n = 'Private_MB'; e = { [int]($_.PrivateMemorySize64 / $MB) } },
        @{ n = 'CPU_s'; e = { [int]$_.TotalProcessorTime.TotalSeconds } } -AutoSize | Out-String -Width 200 | Write-Host
    #endregion

    #region The app pool and the API's garbage collector
    try {
        Import-Module WebAdministration -ErrorAction Stop
        $pool = Get-Item "IIS:\AppPools\$AppPoolName" -ErrorAction Stop
        $recycling = $pool.recycling.periodicRestart
        $privateLimitMb = [long]$recycling.privateMemory / 1KB
        $limitText = if ($privateLimitMb -gt 0) { '{0:N0} MB' -f $privateLimitMb } else { 'none' }
        Write-ToolResult -Name 'App pool' -Status Info -Detail ("identity {0}; recycles at private memory {1}, every {2} (0 = never); idle timeout {3}" -f `
            $pool.processModel.identityType, $limitText, $recycling.time, $pool.processModel.idleTimeout)

        $apps = @(Get-WebApplication | Where-Object { $_.applicationPool -eq $AppPoolName })
        foreach ($app in $apps) {
            $folder = [Environment]::ExpandEnvironmentVariables($app.PhysicalPath)
            $gc = 'Server (the ASP.NET Core default)'
            $runtimeConfig = Join-Path $folder 'BlueTrack.Api.runtimeconfig.json'
            if (Test-Path $runtimeConfig) {
                $config = Get-Content $runtimeConfig -Raw | ConvertFrom-Json
                $options = if ($config.runtimeOptions.PSObject.Properties['configProperties']) { $config.runtimeOptions.configProperties } else { $null }
                if ($options -and $options.PSObject.Properties['System.GC.Server']) {
                    $gc = if ($options.'System.GC.Server') { 'Server (runtimeconfig.json)' } else { 'Workstation (runtimeconfig.json)' }
                }
                if ($options -and $options.PSObject.Properties['System.GC.Concurrent']) { $gc += "; concurrent $($options.'System.GC.Concurrent')" }
                if ($options -and $options.PSObject.Properties['System.GC.ConserveMemory']) { $gc += "; conserve memory $($options.'System.GC.ConserveMemory')" }
            }
            $webConfig = Join-Path $folder 'web.config'
            if ((Test-Path $webConfig) -and (Select-String -Path $webConfig -Pattern 'DOTNET_gcServer|DOTNET_GCgen0size|DOTNET_GCHeapHardLimit|DOTNET_GCConserveMemory' -Quiet)) {
                $gc += '; web.config sets DOTNET_GC environment variables (they take precedence)'
            }
            Write-ToolResult -Name "API garbage collector ($($app.path))" -Status Info -Detail $gc
        }
    } catch {
        Write-ToolResult -Name 'App pool' -Status Info -Detail "couldn't read IIS's settings: $($_.Exception.Message)"
    }
    #endregion

    #region Keeping the API loaded (D-366)
    # The nightly pull and everything else the API schedules run only while IIS has the API loaded (D-332): the pool
    # AlwaysRunning with no idle time-out, preload on the API's application, and IIS Application Initialization.
    try {
        Import-Module WebAdministration -ErrorAction Stop
        $pool = Get-Item "IIS:\AppPools\$AppPoolName" -ErrorAction Stop
        $problems = @()
        if ($pool.startMode -ne 'AlwaysRunning') { $problems += "start mode is $($pool.startMode), not AlwaysRunning" }
        if ($pool.processModel.idleTimeout -ne [TimeSpan]::Zero) { $problems += "idle time-out is $($pool.processModel.idleTimeout), not 0" }
        $apiApps = @(Get-WebApplication | Where-Object {
                $_.applicationPool -eq $AppPoolName -and (Test-Path (Join-Path ([Environment]::ExpandEnvironmentVariables($_.PhysicalPath)) 'BlueTrack.Api.dll'))
            })
        foreach ($app in $apiApps) {
            if (-not $app.preloadEnabled) { $problems += "preload is off on $($app.path)" }
        }
        if ($apiApps.Count -eq 0) { $problems += "no application in the pool holds BlueTrack.Api.dll" }
        try {
            $appInit = Get-WindowsFeature -Name Web-AppInit -ErrorAction Stop
            if ($appInit.InstallState -ne 'Installed') { $problems += 'IIS Application Initialization (Web-AppInit) is not installed' }
        } catch {
            $problems += "couldn't check IIS Application Initialization: $($_.Exception.Message)"
        }
        $detail = 'start mode {0}; idle time-out {1}; preload {2}; Application Initialization checked' -f $pool.startMode, $pool.processModel.idleTimeout,
            (($apiApps | ForEach-Object { "$($_.path) $($_.preloadEnabled)" }) -join ', ')
        if ($problems.Count -gt 0) {
            $detail = ($problems -join '; ') + '. Until fixed, IIS can stop the API and not start it again until someone opens the site, so the nightly pull and the other scheduled work don''t run. Re-run the installer''s -Step Iis.AppPool and see the Operations Guide, "A Nightly Pull Didn''t Run".'
        }
        Write-ToolResult -Name 'API kept loaded' -Status $(if ($problems.Count -gt 0) { 'Warn' } else { 'Pass' }) -Detail $detail
    } catch {
        Write-ToolResult -Name 'API kept loaded' -Status Info -Detail "couldn't read IIS's settings: $($_.Exception.Message)"
    }

    # What IIS did to the pool's worker processes in the last 7 days (the System log's WAS events).
    $wasEvents = @()
    $wasError = $null
    try {
        $wasEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WAS'; StartTime = (Get-Date).AddDays(-7) } -ErrorAction Stop |
                Where-Object { $_.Message -match [regex]::Escape("'$AppPoolName'") })
    } catch {
        # Get-WinEvent throws "No events were found" when nothing matches; anything else is worth saying.
        if ($_.FullyQualifiedErrorId -notmatch 'NoMatchingEventsFound') { $wasError = $_.Exception.Message }
    }
    # Only the IDs seen on BlueTrack servers are named; others are listed by number.
    $meaning = @{ 5074 = 'regular recycle'; 5186 = 'idle shutdown'; 5079 = 'recycle asked for'; 5078 = 'an ISAPI reported an unhealthy condition'; 5009 = 'worker ended unexpectedly'; 5011 = 'worker lost contact with IIS' }
    $bad = @($wasEvents | Where-Object { $_.Id -in 5186, 5009, 5011 })
    $summary = if ($wasError) { "couldn't read the System log: $wasError" } elseif ($wasEvents.Count -eq 0) { 'none' } else { (($wasEvents | Group-Object Id | ForEach-Object { '{0} x {1}{2}' -f $_.Count, $_.Name, $(if ($meaning.ContainsKey([int]$_.Name)) { " ($($meaning[[int]$_.Name]))" } else { '' }) }) -join '; ') }
    Write-ToolResult -Name 'IIS events for the pool (7 days)' -Status $(if ($bad.Count -gt 0) { 'Warn' } else { 'Info' }) -Detail $summary
    if ($wasEvents.Count -gt 0) {
        $wasEvents | Select-Object -First 20 | Format-Table TimeCreated, Id, @{ n = 'Message'; e = { $text = $_.Message -replace '\s+', ' '; $text.Substring(0, [Math]::Min(140, $text.Length)) } } -AutoSize |
            Out-String -Width 220 | Write-Host
    }
    #endregion

    #region Samples
    if ($SampleSeconds -gt 0) {
        Write-Host ''
        Write-Host "Sampling every $IntervalSeconds s for $SampleSeconds s. Run the page or query that makes BlueTrack grow now." -ForegroundColor Cyan
        $samples = New-Object System.Collections.Generic.List[object]
        $previous = @{}
        $end = (Get-Date).AddSeconds($SampleSeconds)
        do {
            $now = Get-Date
            $watched = @()
            $current = Get-AppPoolProcess
            if ($current) { $watched += [pscustomobject]@{ Name = 'w3wp'; Process = $current } }
            foreach ($sql in @(Get-Process -Name sqlservr -ErrorAction SilentlyContinue)) { $watched += [pscustomobject]@{ Name = 'sqlservr'; Process = $sql } }
            foreach ($item in $watched) {
                $item.Process.Refresh()
                $key = "$($item.Name):$($item.Process.Id)"
                $cpu = $item.Process.TotalProcessorTime.TotalSeconds
                $percent = $null
                if ($previous.ContainsKey($key)) {
                    $seconds = ($now - $previous[$key].Time).TotalSeconds
                    if ($seconds -gt 0) { $percent = [math]::Round(100 * ($cpu - $previous[$key].Cpu) / ($seconds * $cpuCount), 1) }
                }
                $previous[$key] = @{ Time = $now; Cpu = $cpu }
                $samples.Add([pscustomobject]@{
                        Time          = $now.ToString('HH:mm:ss')
                        Process       = $item.Name
                        PID           = $item.Process.Id
                        CpuPercent    = $percent
                        WorkingSet_MB = [int]($item.Process.WorkingSet64 / $MB)
                        Private_MB    = [int]($item.Process.PrivateMemorySize64 / $MB)
                    })
            }
            if ((Get-Date) -lt $end) { Start-Sleep -Seconds $IntervalSeconds }
        } while ((Get-Date) -lt $end)
        $samples | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
        foreach ($group in ($samples | Group-Object Process, PID)) {
            $rows = @($group.Group)
            $cpuValues = @($rows | Where-Object { $null -ne $_.CpuPercent } | ForEach-Object { $_.CpuPercent })
            $peakCpu = if ($cpuValues.Count -gt 0) { ($cpuValues | Measure-Object -Maximum).Maximum } else { 0 }
            $ws = $rows | Measure-Object WorkingSet_MB -Minimum -Maximum
            $private = $rows | Measure-Object Private_MB -Minimum -Maximum
            Write-ToolResult -Name "Samples $($group.Name)" -Status Info -Detail ('CPU peak {0}% of the server; working set {1:N0} to {2:N0} MB; private {3:N0} to {4:N0} MB' -f `
                $peakCpu, $ws.Minimum, $ws.Maximum, $private.Minimum, $private.Maximum)
        }
    } else {
        Write-ToolResult -Name 'Samples' -Status Info -Detail 'one reading only. To see what a page does to the worker, run again with -SampleSeconds 300 and use that page while it runs.'
    }
    #endregion

    #region SQL Server
    if (-not $SkipSql) {
        Write-Host ''
        $connection = $null
        try {
            $connection = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServerInstance;Database=master;Integrated Security=true;Application Name=BlueTrack Tools"
            $connection.Open()

            function Invoke-ToolQuery {
                param([Parameter(Mandatory)] [string]$Sql, [hashtable]$Parameters = @{})
                $command = $connection.CreateCommand()
                $command.CommandText = $Sql
                $command.CommandTimeout = 120
                foreach ($name in $Parameters.Keys) { [void]$command.Parameters.AddWithValue($name, $Parameters[$name]) }
                $table = New-Object System.Data.DataTable
                $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
                [void]$adapter.Fill($table)
                return , $table
            }

            $settings = Invoke-ToolQuery -Sql "SELECT name, CAST(value_in_use AS bigint) AS value FROM sys.configurations WHERE name IN ('max server memory (MB)', 'min server memory (MB)', 'cost threshold for parallelism', 'max degree of parallelism')"
            $setting = @{}
            foreach ($row in $settings.Rows) { $setting[[string]$row.name] = [long]$row.value }
            $maxMemory = $setting['max server memory (MB)']
            $local = $sqlProcesses.Count -gt 0
            $maxText = if ($maxMemory -ge 2147483647) { 'not limited (the default)' } else { '{0:N0} MB' -f $maxMemory }
            $status = if ($maxMemory -ge 2147483647 -and $local) { 'Warn' } else { 'Info' }
            $detail = "max server memory $maxText; min server memory {0:N0} MB" -f $setting['min server memory (MB)']
            if ($status -eq 'Warn') { $detail += '. SQL Server is on this server: unlimited, it keeps growing into the memory IIS and Windows need. Ask the DBA to set a limit.' }
            Write-ToolResult -Name 'SQL Server memory settings' -Status $status -Detail $detail
            Write-ToolResult -Name 'SQL Server parallelism' -Status Info -Detail ('cost threshold for parallelism {0}; max degree of parallelism {1} (0 = all CPUs)' -f $setting['cost threshold for parallelism'], $setting['max degree of parallelism'])

            $memory = Invoke-ToolQuery -Sql 'SELECT physical_memory_in_use_kb / 1024 AS in_use_mb, locked_page_allocations_kb / 1024 AS locked_mb, process_physical_memory_low AS low FROM sys.dm_os_process_memory'
            $info = Invoke-ToolQuery -Sql 'SELECT committed_kb / 1024 AS committed_mb, committed_target_kb / 1024 AS target_mb, sqlserver_start_time FROM sys.dm_os_sys_info'
            $system = Invoke-ToolQuery -Sql 'SELECT system_memory_state_desc AS state FROM sys.dm_os_sys_memory'
            $targetMb = [long]$info.Rows[0].target_mb
            $physicalLow = [bool]$memory.Rows[0].low
            $detail = 'in use {0:N0} MB ({1:N0} MB in locked pages); committed {2:N0} MB, target {3:N0} MB; Windows reports "{4}"; started {5:g}' -f `
                $memory.Rows[0].in_use_mb, $memory.Rows[0].locked_mb, $info.Rows[0].committed_mb, $targetMb, $system.Rows[0].state, $info.Rows[0].sqlserver_start_time
            $squeezed = $maxMemory -lt 2147483647 -and $targetMb -lt $maxMemory / 2
            if ($squeezed -or $physicalLow) {
                $detail += '. SQL Server is short of memory: its target is far below its max server memory, or Windows has told it physical memory is low, so it gives up its cache and reads more from disk. See "Memory in use" for what else holds the server''s memory.'
            }
            Write-ToolResult -Name 'SQL Server memory' -Status $(if ($squeezed -or $physicalLow) { 'Warn' } else { 'Info' }) -Detail $detail

            $buffer = Invoke-ToolQuery -Sql 'SELECT COUNT_BIG(*) * 8 / 1024 AS mb FROM sys.dm_os_buffer_descriptors WHERE database_id = DB_ID(@db)' -Parameters @{ '@db' = $DatabaseName }
            Write-ToolResult -Name "SQL Server cache for $DatabaseName" -Status Info -Detail ('{0:N0} MB of data pages held in memory' -f $buffer.Rows[0].mb)

            # Statements whose plan belongs to the database (ad hoc and parameterized statements have no dbid in their text).
            $statements = @'
SELECT TOP (@top)
    CAST(qs.total_worker_time / 1000 AS bigint) AS total_cpu_ms,
    qs.execution_count AS runs,
    CAST(qs.total_worker_time / 1000.0 / qs.execution_count AS decimal(14, 1)) AS avg_cpu_ms,
    CAST(qs.total_elapsed_time / 1000.0 / qs.execution_count AS decimal(14, 1)) AS avg_ms,
    qs.total_logical_reads / qs.execution_count AS avg_reads,
    qs.total_rows / qs.execution_count AS avg_rows,
    qs.max_rows,
    qs.max_grant_kb / 1024 AS max_grant_mb,
    qs.last_execution_time,
    qs.plan_handle, qs.statement_start_offset, qs.statement_end_offset,
    OBJECT_NAME(st.objectid, st.dbid) AS [procedure],
    LEFT(REPLACE(REPLACE(REPLACE(SUBSTRING(st.text, qs.statement_start_offset / 2 + 1,
        (CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2 + 1),
        CHAR(13), ' '), CHAR(10), ' '), CHAR(9), ' '), 200) AS statement_text
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
CROSS APPLY (SELECT CONVERT(int, pa.value) AS dbid FROM sys.dm_exec_plan_attributes(qs.plan_handle) pa WHERE pa.attribute = N'dbid') d
WHERE d.dbid = DB_ID(@db)
ORDER BY {0} DESC
'@
            # How far back the statistics reach: SQL Server drops a statement's statistics with its cached plan,
            # which happens early when memory is short.
            $coverage = Invoke-ToolQuery -Parameters @{ '@db' = $DatabaseName } -Sql @'
SELECT COUNT(*) AS statements,
       MIN(qs.creation_time) AS oldest,
       DATEDIFF(MINUTE, MIN(qs.creation_time), SYSDATETIME()) AS oldest_minutes,
       DATEDIFF(MINUTE, (SELECT sqlserver_start_time FROM sys.dm_os_sys_info), SYSDATETIME()) AS started_minutes
FROM sys.dm_exec_query_stats qs
CROSS APPLY (SELECT CONVERT(int, pa.value) AS dbid FROM sys.dm_exec_plan_attributes(qs.plan_handle) pa WHERE pa.attribute = N'dbid') d
WHERE d.dbid = DB_ID(@db)
'@
            $cover = $coverage.Rows[0]
            if ([int]$cover.statements -eq 0) {
                Write-ToolResult -Name 'Statement statistics' -Status Warn -Detail "none held for $DatabaseName. SQL Server has dropped them (memory is short) or BlueTrack hasn't run a query since it started."
            } else {
                $oldestMinutes = [int]$cover.oldest_minutes
                $detail = '{0:N0} statements, the oldest cached {1:g} ({2:N0} minutes ago)' -f [int]$cover.statements, $cover.oldest, $oldestMinutes
                $short = $oldestMinutes -lt 60 -and [int]$cover.started_minutes -ge 60
                if ($short) {
                    $detail += ". SQL Server dropped older statistics, usually because memory is short, so the lists below cover only that time. Run with -SampleSeconds and use the slow page while it runs, so its statements are still there."
                }
                Write-ToolResult -Name 'Statement statistics' -Status $(if ($short) { 'Warn' } else { 'Info' }) -Detail $detail
            }

            # D-373: the listed statements' plans, as .sqlplan files beside the report.
            $savePlans = -not ($NoPlans -or $NoQueryText)
            $planFolder = [IO.Path]::ChangeExtension($OutFile, $null).TrimEnd('.') + '-plans'
            $plansSaved = 0
            foreach ($list in @(
                    @{ Title = "Heaviest $DatabaseName statements by total CPU"; Order = 'qs.total_worker_time'; Prefix = 'cpu' },
                    @{ Title = "$DatabaseName statements returning the most rows in one run (large results grow the worker)"; Order = 'qs.max_rows'; Prefix = 'rows' })) {
                $rows = Invoke-ToolQuery -Sql ($statements -f $list.Order) -Parameters @{ '@db' = $DatabaseName; '@top' = $TopQueries }
                Write-Host ''
                Write-Host "$($list.Title):"
                $rank = 0
                foreach ($row in $rows.Rows) {
                    $rank++
                    $procedure = if ($row.procedure -is [DBNull]) { '' } else { " in $($row.procedure)" }
                    Write-Host ('{0,3}. CPU {1:N0} ms over {2:N0} runs (avg {3} ms CPU, {4} ms elapsed, {5:N0} reads, {6:N0} rows; max {7:N0} rows, max grant {8:N0} MB), last {9:g}{10}' -f `
                        $rank, $row.total_cpu_ms, $row.runs, $row.avg_cpu_ms, $row.avg_ms, $row.avg_reads, $row.avg_rows, $row.max_rows, $row.max_grant_mb, $row.last_execution_time, $procedure)
                    if (-not $NoQueryText) { Write-Host "     $($row.statement_text)" }
                    if ($savePlans) {
                        try {
                            $plan = Invoke-ToolQuery -Sql 'SELECT query_plan FROM sys.dm_exec_text_query_plan(@handle, @start, @end)' `
                                -Parameters @{ '@handle' = [byte[]]$row.plan_handle; '@start' = [int]$row.statement_start_offset; '@end' = [int]$row.statement_end_offset }
                            if ($plan.Rows.Count -gt 0 -and -not ($plan.Rows[0].query_plan -is [DBNull])) {
                                if (-not (Test-Path $planFolder)) { New-Item -ItemType Directory -Path $planFolder | Out-Null }
                                $planFile = Join-Path $planFolder ('{0}-{1:D2}.sqlplan' -f $list.Prefix, $rank)
                                Set-Content -Path $planFile -Value ([string]$plan.Rows[0].query_plan) -Encoding UTF8
                                $plansSaved++
                            }
                        } catch {
                            Write-Host "     (its query plan couldn't be read: $($_.Exception.Message))"
                        }
                    }
                }
            }
            if ($savePlans) {
                Write-ToolResult -Name 'Query plans' -Status Info -Detail $(if ($plansSaved -gt 0) { "$plansSaved saved in $planFolder (cpu-NN and rows-NN match the numbers above; open them in SQL Server Management Studio)" } else { 'none saved (SQL Server no longer holds them)' })
            }

            # D-366: when the API ran, the latest pulls and the Import and Load job's latest runs, so one report shows
            # whether a missing night was the API not running (Operations Guide, "A Nightly Pull Didn't Run").
            # Times in the BlueTrack tables are UTC. A table missing (an older version) or no msdb access is noted, not fatal.
            $db = '[' + $DatabaseName.Replace(']', ']]') + ']'
            foreach ($history in @(
                    @{ Name = 'API starts (UTC)'; Sql = "SELECT TOP (10) StartedAt, StoppedAt, ProcessId, Version, PullSchedulerCheckedAt FROM $db.web.api_run ORDER BY ApiRunKey DESC" },
                    @{ Name = 'Self-Hosted Direct pulls (UTC)'; Sql = "SELECT TOP (7) StartedAt, CompletedAt, Status, TriggeredBy FROM $db.dbo.cyberark_direct_pull ORDER BY PullKey DESC" },
                    @{ Name = 'Import and Load job runs (server time)'; Sql = @"
SELECT TOP (7) CONVERT(datetime, CONVERT(char(8), h.run_date) + ' ' + STUFF(STUFF(RIGHT('000000' + CONVERT(varchar(6), h.run_time), 6), 5, 0, ':'), 3, 0, ':')) AS started,
       CASE h.run_status WHEN 0 THEN 'Failed' WHEN 1 THEN 'Succeeded' WHEN 3 THEN 'Cancelled' ELSE CONVERT(varchar(10), h.run_status) END AS outcome,
       LEFT(REPLACE(REPLACE(s.message, CHAR(13), ' '), CHAR(10), ' '), 300) AS step_message
FROM msdb.dbo.sysjobhistory h
JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
OUTER APPLY (SELECT TOP (1) message FROM msdb.dbo.sysjobhistory x
             WHERE x.job_id = h.job_id AND x.step_id = 1 AND x.instance_id < h.instance_id ORDER BY x.instance_id DESC) s
WHERE j.name = 'BlueTrack (' + @db + ') - Import and Load' AND h.step_id = 0
ORDER BY h.instance_id DESC
"@ })) {
                try {
                    $table = Invoke-ToolQuery -Sql $history.Sql -Parameters @{ '@db' = $DatabaseName }
                    Write-Host ''
                    Write-Host "$($history.Name):"
                    if ($table.Rows.Count -eq 0) { Write-Host '  none' } else { $table | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host }
                } catch {
                    Write-ToolResult -Name $history.Name -Status Info -Detail "couldn't read it: $($_.Exception.Message)"
                }
            }
        } catch {
            Write-ToolResult -Name 'SQL Server' -Status Warn -Detail "couldn't read SQL Server's views (VIEW SERVER STATE is needed; run as a sysadmin, or use -SkipSql): $($_.Exception.Message)"
        } finally {
            if ($connection) { $connection.Dispose() }
        }
    }
    #endregion

    $exitCode = Complete-ToolRun
    Write-Host "Saved to $OutFile. Review it before sending: it names this server, its processes and (unless -NoQueryText) the start of BlueTrack's SQL statements; the query plans (unless -NoPlans) hold the statements' text and the values they were compiled for."
} finally {
    Stop-Transcript | Out-Null
}
exit $exitCode
