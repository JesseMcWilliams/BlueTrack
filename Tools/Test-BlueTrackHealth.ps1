<#
.SYNOPSIS
    Checks a running BlueTrack install: services, the site, the health checks
    and the nightly SQL Agent jobs (D-219).
.DESCRIPTION
    Read-only. Run it on the BlueTrack server as an account that can sign in
    to BlueTrack with Windows authentication and holds ViewDeploymentInfo
    (an Admin), and that can read msdb on the SQL Server.

    Checks:
      - Windows services: IIS (W3SVC), SQL Server, SQL Server Agent (running
        and set to start automatically), and a GitHub Actions runner if one
        is installed (development hosts only).
      - The web pages answer at the site root, and the API's About version.
      - Your own sign-in (GET /api/me) and every health check on the
        Deployment page (GET /api/admin/deployment).
      - The nightly SQL Agent jobs for this database: enabled, scheduled, and
        how their last run ended.
.PARAMETER SiteUrl
    The site's address, e.g. https://bluetrack.company.com.
.PARAMETER SqlServerInstance
    The SQL Server holding the database (default: this computer).
.PARAMETER DatabaseName
    The BlueTrack database (default BlueTrack); it names the SQL Agent jobs.
.PARAMETER SqlServiceName
    The SQL Server service (default MSSQLSERVER; a named instance is MSSQL$NAME).
.PARAMETER SkipServices
    Don't check Windows services (for example when SQL Server is on another machine).
.EXAMPLE
    .\Test-BlueTrackHealth.ps1 -SiteUrl https://bluetrack.company.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$SiteUrl,
    [string]$SqlServerInstance = $env:COMPUTERNAME,
    [string]$DatabaseName = 'BlueTrack',
    [string]$SqlServiceName = 'MSSQLSERVER',
    [switch]$SkipServices
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BlueTrackTools.psm1') -Force

$SiteUrl = $SiteUrl.TrimEnd('/')
$api = "$SiteUrl/BlueTrack/api"

Write-Host "BlueTrack health check: $SiteUrl" -ForegroundColor Cyan

if (-not $SkipServices) {
    $agentService = if ($SqlServiceName -eq 'MSSQLSERVER') { 'SQLSERVERAGENT' } else { 'SQLAgent$' + $SqlServiceName.Substring(6) }
    foreach ($name in 'W3SVC', $SqlServiceName, $agentService) {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-ToolResult -Name "Service $name" -Status Fail -Detail 'not installed on this computer'
        } elseif ($service.Status -ne 'Running') {
            Write-ToolResult -Name "Service $name" -Status Fail -Detail "$($service.Status), start type $($service.StartType)"
        } elseif ($service.StartType -ne 'Automatic') {
            Write-ToolResult -Name "Service $name" -Status Warn -Detail "running, but start type is $($service.StartType): it won't start after a reboot"
        } else {
            Write-ToolResult -Name "Service $name" -Status Pass -Detail 'running, starts automatically'
        }
    }
    foreach ($runner in @(Get-Service -Name 'actions.runner.*' -ErrorAction SilentlyContinue)) {
        $status = if ($runner.Status -eq 'Running') { 'Pass' } else { 'Warn' }
        Write-ToolResult -Name "Service $($runner.Name)" -Status $status -Detail "$($runner.Status) (CI runner, development hosts only)"
    }
}

$anonymous = Open-ToolHttpSession
$windows = Open-ToolHttpSession -UseDefaultCredentials

try {
    $root = Invoke-ToolHttp -Session $anonymous -Url "$SiteUrl/"
    if ($root.Status -eq 200 -and $root.Body -match '<div id="app"') {
        Write-ToolResult -Name 'Web pages' -Status Pass -Detail "$SiteUrl/ answers"
    } else {
        Write-ToolResult -Name 'Web pages' -Status Fail -Detail "HTTP $($root.Status) from $SiteUrl/"
    }
} catch {
    Write-ToolResult -Name 'Web pages' -Status Fail -Detail $_.Exception.Message
}

try {
    $about = Invoke-ToolApi -Session $windows -Url "$api/about"
    if ($about.Status -eq 200) {
        Write-ToolResult -Name 'Version (About)' -Status Pass -Detail $about.Data.version
    } else {
        Write-ToolResult -Name 'Version (About)' -Status Fail -Detail "HTTP $($about.Status)"
    }

    $me = Invoke-ToolApi -Session $windows -Url "$api/me"
    if ($me.Status -eq 200) {
        Write-ToolResult -Name 'Your sign-in (Windows)' -Status Pass -Detail "$($me.Data.displayName), $(Format-ToolCount @($me.Data.roleNames).Count) role(s)"
    } else {
        Write-ToolResult -Name 'Your sign-in (Windows)' -Status Fail -Detail "HTTP $($me.Status): Windows sign-in failed or isn't allowed for this account"
    }

    $deployment = Invoke-ToolApi -Session $windows -Url "$api/admin/deployment"
    if ($deployment.Status -eq 200) {
        Write-ToolResult -Name 'Environment' -Status Info -Detail "$($deployment.Data.environmentName), version $($deployment.Data.version)"
        foreach ($check in $deployment.Data.healthChecks) {
            $status = switch ($check.status) { 'Healthy' { 'Pass' } 'Degraded' { 'Warn' } default { 'Fail' } }
            Write-ToolResult -Name "Health: $($check.name)" -Status $status -Detail $check.description
        }
    } elseif ($deployment.Status -eq 403) {
        Write-ToolResult -Name 'Health checks' -Status Warn -Detail 'your account lacks ViewDeploymentInfo; run as an Admin to see them'
    } else {
        Write-ToolResult -Name 'Health checks' -Status Fail -Detail "HTTP $($deployment.Status) from /api/admin/deployment"
    }
} catch {
    Write-ToolResult -Name 'API' -Status Fail -Detail $_.Exception.Message
}

# The nightly jobs, by the names the installer gives them (Database\Manual\04 and 05).
try {
    $connection = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServerInstance;Database=msdb;Integrated Security=true;Application Name=BlueTrack Tools"
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = @'
SELECT j.name, j.enabled,
       (SELECT COUNT(*) FROM msdb.dbo.sysjobschedules js JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
         WHERE js.job_id = j.job_id AND s.enabled = 1) AS enabled_schedules,
       h.run_status, h.run_date, h.run_time, LEFT(h.message, 300) AS message
FROM msdb.dbo.sysjobs j
OUTER APPLY (SELECT TOP (1) run_status, run_date, run_time, message FROM msdb.dbo.sysjobhistory
             WHERE job_id = j.job_id AND step_id = 0 ORDER BY instance_id DESC) h
WHERE j.name LIKE @pattern
ORDER BY j.name
'@
    [void]$command.Parameters.AddWithValue('@pattern', "BlueTrack ($DatabaseName) - %")
    $reader = $command.ExecuteReader()
    $found = 0
    while ($reader.Read()) {
        $found++
        $jobName = $reader['name']
        if ([int]$reader['enabled'] -ne 1 -or [int]$reader['enabled_schedules'] -eq 0) {
            Write-ToolResult -Name "Job $jobName" -Status Warn -Detail 'disabled or not scheduled'
            continue
        }
        if ($reader['run_status'] -is [DBNull]) {
            Write-ToolResult -Name "Job $jobName" -Status Info -Detail 'scheduled; hasn''t run yet (or its history was cleared)'
            continue
        }
        # msdb keeps the date as yyyymmdd and the time as hhmmss, both integers.
        $when = [datetime]::ParseExact(('{0}{1:000000}' -f $reader['run_date'], [int]$reader['run_time']), 'yyyyMMddHHmmss', $null).ToString('yyyy-MM-dd HH:mm:ss')
        switch ([int]$reader['run_status']) {
            1 { Write-ToolResult -Name "Job $jobName" -Status Pass -Detail "last run $when succeeded" }
            0 { Write-ToolResult -Name "Job $jobName" -Status Fail -Detail "last run $when failed: $($reader['message'])" }
            default { Write-ToolResult -Name "Job $jobName" -Status Warn -Detail "last run $when ended with status $($reader['run_status'])" }
        }
    }
    $reader.Close()
    $connection.Close()
    if ($found -eq 0) {
        Write-ToolResult -Name 'SQL Agent jobs' -Status Warn -Detail "no job named 'BlueTrack ($DatabaseName) - ...' on $SqlServerInstance"
    }
} catch {
    Write-ToolResult -Name 'SQL Agent jobs' -Status Fail -Detail "can't read msdb on ${SqlServerInstance}: $($_.Exception.Message)"
}

exit (Complete-ToolRun)
