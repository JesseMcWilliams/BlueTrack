<#
.SYNOPSIS
    Runs BlueTrack's own Test buttons from the command line (D-219): the
    Secrets Store lookup, the CyberArk sources, every data feed, and the
    break-glass alert setup.
.DESCRIPTION
    Signs in to BlueTrack with Windows authentication as the account running
    the script, which needs ManageSecretsStore, ManageDataSources,
    ManageIdentityProviders, ManageNotifications and ViewAuditLog (an
    Admin has them). A check whose permission is missing is reported as a
    warning, not a failure.

    Changes nothing, with one exception: a Secrets Store lookup (-SecretObject)
    retrieves a secret for real, so BlueTrack records it in the Audit Log as a
    ConnectionTest event (D-210). The secret itself is never shown.

    Checks:
      - Secrets Store: which backend is active; with -SecretObject, a lookup
        through it (user name and address, never the secret).
      - CyberArk sources: whether they're set up, and the page's Test (EVD
        database, export folder and today's files as the App Pool sees them).
      - Data feeds: each feed's Test (folder, today's file, required columns).
      - Break-glass: the accounts listed, whether the BreakGlassLogon
        notification has recipients, and the latest BreakGlassLogon events.
.PARAMETER SiteUrl
    The site's address, e.g. https://bluetrack.company.com.
.PARAMETER SecretSafe
    Secrets Store test lookup: the Safe (CyberArk CP/CCP; ignored by other backends).
.PARAMETER SecretFolder
    Secrets Store test lookup: the Folder (default Root).
.PARAMETER SecretObject
    Secrets Store test lookup: the Object (or the backend's secret name). Leave out to skip the lookup.
.EXAMPLE
    .\Test-BlueTrackIntegrations.ps1 -SiteUrl https://bluetrack.company.com -SecretSafe 'BlueTrack' -SecretObject 'BlueTrack_SMTP'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$SiteUrl,
    [string]$SecretSafe = '',
    [string]$SecretFolder = 'Root',
    [string]$SecretObject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BlueTrackTools.psm1') -Force

$api = "$($SiteUrl.TrimEnd('/'))/BlueTrack/api"
$session = Open-ToolHttpSession -UseDefaultCredentials
Write-Host "BlueTrack integrations check: $SiteUrl" -ForegroundColor Cyan

# A GET that reports a missing permission once, as a warning, instead of failing.
function Get-ToolData([string]$Path, [string]$What, [string]$Permission) {
    $response = Invoke-ToolApi -Session $session -Url "$api/$Path"
    if ($response.Status -eq 200) { return , $response.Data }
    if ($response.Status -eq 403) {
        Write-ToolResult -Name $What -Status Warn -Detail "skipped: your account lacks $Permission"
    } else {
        Write-ToolResult -Name $What -Status Fail -Detail "HTTP $($response.Status) from /api/$Path"
    }
    return $null
}

# Secrets Store
$backends = Get-ToolData -Path 'admin/secrets-store' -What 'Secrets Store' -Permission 'ManageSecretsStore'
if ($null -ne $backends) {
    $active = @($backends | Where-Object { $_.isActive }) | Select-Object -First 1
    if ($null -eq $active) {
        Write-ToolResult -Name 'Secrets Store' -Status Fail -Detail 'no backend is active'
    } else {
        Write-ToolResult -Name 'Secrets Store' -Status Info -Detail "active backend: $($active.backendType)"
        if ($SecretObject) {
            $test = Invoke-ToolApi -Session $session -Method POST -Url "$api/admin/secrets-store/test" -Json @{ safe = $SecretSafe; folder = $SecretFolder; object = $SecretObject }
            if ($test.Status -ne 200) {
                Write-ToolResult -Name "Secrets lookup '$SecretObject'" -Status Fail -Detail "HTTP $($test.Status)"
            } elseif ($test.Data.success) {
                $cache = if ($test.Data.fromFallbackCache) { ' (from the fallback cache: the backend itself didn''t answer)' } else { '' }
                Write-ToolResult -Name "Secrets lookup '$SecretObject'" -Status $(if ($test.Data.fromFallbackCache) { 'Warn' } else { 'Pass' }) -Detail "user name '$($test.Data.userName)', address '$($test.Data.address)'$cache"
            } else {
                Write-ToolResult -Name "Secrets lookup '$SecretObject'" -Status Fail -Detail "$($test.Data.errorCategory): $($test.Data.error)"
            }
        } else {
            Write-ToolResult -Name 'Secrets lookup' -Status Info -Detail 'skipped (no -SecretObject)'
        }
    }
}

# CyberArk sources
$sources = Get-ToolData -Path 'admin/cyberark-sources' -What 'CyberArk sources' -Permission 'ManageDataSources'
if ($null -ne $sources) {
    if (-not $sources.isConfigured) {
        Write-ToolResult -Name 'CyberArk sources' -Status Fail -Detail 'not set up: the nightly import fails until they''re saved on Admin > Data Sources'
    } else {
        $settings = $sources.settings
        $which = @(); if ($settings.importPrivilegeCloud) { $which += 'Privilege Cloud' }; if ($settings.importSelfHosted) { $which += 'Self-Hosted' }
        Write-ToolResult -Name 'CyberArk sources' -Status Info -Detail "importing $($which -join ' and ')"
        $test = Invoke-ToolApi -Session $session -Method POST -Url "$api/admin/cyberark-sources/test" -Json $settings
        if ($test.Status -ne 200) {
            Write-ToolResult -Name 'CyberArk sources Test' -Status Fail -Detail "HTTP $($test.Status): $($test.Body)"
        } else {
            foreach ($check in $test.Data) {
                Write-ToolResult -Name "CyberArk: $($check.item)" -Status $(if ($check.ok) { 'Pass' } else { 'Fail' }) -Detail $check.message
            }
            if ($settings.importPrivilegeCloud) {
                Write-ToolResult -Name 'CyberArk: file access' -Status Info -Detail 'files were checked as the App Pool''s account; the import reads them as SQL Server''s service account'
            }
        }
    }
}

# Data feeds
$feeds = Get-ToolData -Path 'admin/data-feeds' -What 'Data feeds' -Permission 'ManageDataSources'
if ($null -ne $feeds) {
    if (@($feeds).Count -eq 0) { Write-ToolResult -Name 'Data feeds' -Status Info -Detail 'none set up' }
    foreach ($feed in @($feeds)) {
        $name = "Feed '$($feed.displayName)'" + $(if ($feed.isEnabled) { '' } else { ' (disabled)' })
        $test = Invoke-ToolApi -Session $session -Method POST -Url "$api/admin/data-feeds/test" -Json @{
            folderPath = $feed.folderPath; fileNamePattern = $feed.fileNamePattern; feedType = $feed.feedType; importMappingProfileKey = $feed.importMappingProfileKey
        }
        if ($test.Status -ne 200) {
            Write-ToolResult -Name $name -Status Fail -Detail "HTTP $($test.Status)"
        } elseif ($test.Data.matchedFile -and @($test.Data.missingColumns).Count -eq 0) {
            Write-ToolResult -Name $name -Status Pass -Detail $test.Data.message
        } else {
            Write-ToolResult -Name $name -Status $(if ($feed.isEnabled) { 'Fail' } else { 'Warn' }) -Detail $test.Data.message
        }
        if ($feed.lastRun) {
            $status = switch ($feed.lastRun.outcome) { 'Succeeded' { 'Pass' } 'CompletedWithErrors' { 'Warn' } default { 'Fail' } }
            Write-ToolResult -Name "$name last run" -Status $status -Detail "$($feed.lastRun.outcome), $($feed.lastRun.startedAt) UTC: $($feed.lastRun.summary)$($feed.lastRun.errorMessage)"
        }
    }
}

# Break-glass alerts (D-216)
$accounts = Get-ToolData -Path 'admin/break-glass-accounts' -What 'Break-glass accounts' -Permission 'ManageIdentityProviders'
if ($null -ne $accounts) {
    $count = @($accounts).Count
    Write-ToolResult -Name 'Break-glass accounts' -Status $(if ($count -gt 0) { 'Pass' } else { 'Warn' }) -Detail $(if ($count -gt 0) { "$(Format-ToolCount $count) listed: $((@($accounts) | ForEach-Object displayName) -join ', ')" } else { 'none listed on Identity Providers: no sign-in raises an alert' })
}
$types = Get-ToolData -Path 'admin/notifications/types' -What 'BreakGlassLogon notification' -Permission 'ManageNotifications'
$recipients = Get-ToolData -Path 'admin/notifications/recipients' -What 'Notification recipients' -Permission 'ManageNotifications'
if ($null -ne $types -and $null -ne $recipients) {
    $type = @($types) | Where-Object notificationTypeName -eq 'BreakGlassLogon' | Select-Object -First 1
    $active = @(@($recipients) | Where-Object isActive).Count
    if ($null -eq $type) {
        Write-ToolResult -Name 'BreakGlassLogon notification' -Status Fail -Detail 'the notification type is missing: Database script 16 hasn''t run'
    } elseif ($active -eq 0 -and -not $type.targetRoleName) {
        Write-ToolResult -Name 'BreakGlassLogon notification' -Status Warn -Detail 'no active recipients and no target role: an alert would go to nobody'
    } else {
        Write-ToolResult -Name 'BreakGlassLogon notification' -Status Pass -Detail "$(Format-ToolCount $active) active recipient(s), target role $(if ($type.targetRoleName) { $type.targetRoleName } else { '(none)' })"
    }
}
$events = Get-ToolData -Path 'audit-log?eventType=BreakGlassLogon&page=1&pageSize=5' -What 'BreakGlassLogon events' -Permission 'ViewAuditLog'
if ($null -ne $events) {
    if (@($events).Count -eq 0) {
        Write-ToolResult -Name 'BreakGlassLogon events' -Status Info -Detail 'none recorded'
    } else {
        foreach ($e in @($events)) { Write-ToolResult -Name 'BreakGlassLogon event' -Status Info -Detail "$($e.occurredAt) UTC, $($e.performedByName): $($e.detail)" }
    }
}

exit (Complete-ToolRun)
