<#
.SYNOPSIS
    Checks that a host can bind to a domain controller over LDAPS the way
    BlueTrack does (D-219), before configuring the domain in BlueTrack.
.DESCRIPTION
    Read-only; needs nothing from BlueTrack, so it can run on any Windows
    host, for example a server in another domain. Checks in order, stopping
    at the first that fails:
      1. The domain controller's name resolves (DNS).
      2. Its LDAPS port answers (TCP, default 636).
      3. Its LDAPS certificate is trusted by this host, names the domain
         controller, and hasn't expired.
      4. A bind succeeds the way BlueTrack binds with "Use SSL" on
         (PrincipalContext, SecureSocketLayer + Negotiate), as -Credential
         or, without it, as the account running the script ("trusted
         connection").
      5. A group can be read (default "Domain Admins") and its members counted.
    The password is only used in memory for the bind.
.PARAMETER DomainController
    The domain controller's name, as BlueTrack's Domain Controller setting will hold it.
.PARAMETER SearchBase
    Optional, as BlueTrack's Search Base setting, e.g. DC=Company,DC=com.
.PARAMETER Credential
    The bind account (DOMAIN\name). Leave out to bind as the current account.
.PARAMETER GroupName
    A group to look up after binding (default Domain Admins).
.PARAMETER Port
    The LDAPS port (default 636).
.EXAMPLE
    .\Test-BlueTrackLdaps.ps1 -DomainController WIN-K5POLANERI5.Company.com -SearchBase 'DC=Company,DC=com' -Credential (Get-Credential COMPANY\svc-bluetrack-ldap)
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'SearchBase', Justification = 'Used inside the bind job through $using:, which the rule does not see.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$DomainController,
    [string]$SearchBase,
    [System.Management.Automation.PSCredential]$Credential,
    [string]$GroupName = 'Domain Admins',
    [int]$Port = 636
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BlueTrackTools.psm1') -Force
Add-Type -AssemblyName System.DirectoryServices.AccountManagement

Write-Host "LDAPS check: $DomainController`:$Port, from $env:COMPUTERNAME" -ForegroundColor Cyan

function Exit-ToolRun { exit (Complete-ToolRun) }

# 1. DNS
try {
    $addresses = @([System.Net.Dns]::GetHostAddresses($DomainController) | ForEach-Object { $_.IPAddressToString })
    Write-ToolResult -Name 'DNS' -Status Pass -Detail ($addresses -join ', ')
} catch {
    Write-ToolResult -Name 'DNS' -Status Fail -Detail "$DomainController doesn't resolve here: add a conditional forwarder or a hosts entry"
    Exit-ToolRun
}

# 2 and 3. TCP, then the TLS handshake and the certificate it presents.
$tcp = New-Object System.Net.Sockets.TcpClient
try {
    $connect = $tcp.BeginConnect($DomainController, $Port, $null, $null)
    if (-not $connect.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds(10)) -or -not $tcp.Connected) { throw 'no answer within 10 seconds' }
    $tcp.EndConnect($connect)
    Write-ToolResult -Name "Port $Port" -Status Pass -Detail 'answers'
} catch {
    Write-ToolResult -Name "Port $Port" -Status Fail -Detail "$($_.Exception.Message): check firewalls between this host and the domain controller"
    $tcp.Dispose()
    Exit-ToolRun
}

$script:PolicyErrors = [System.Net.Security.SslPolicyErrors]::None
$validation = [System.Net.Security.RemoteCertificateValidationCallback] {
    param($sslSender, $certificate, $chain, $errors)
    $null = $sslSender, $certificate, $chain
    $script:PolicyErrors = $errors
    return $true   # only to read the certificate; the bind below validates for real
}
try {
    $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream(), $false, $validation)
    $ssl.AuthenticateAsClient($DomainController)
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
    $summary = "$($cert.Subject), expires $($cert.NotAfter.ToString('yyyy-MM-dd')), thumbprint $($cert.Thumbprint)"
    if ($script:PolicyErrors -eq [System.Net.Security.SslPolicyErrors]::None) {
        Write-ToolResult -Name 'LDAPS certificate' -Status Pass -Detail $summary
    } else {
        $why = @()
        if ($script:PolicyErrors -band [System.Net.Security.SslPolicyErrors]::RemoteCertificateChainErrors) { $why += 'not trusted here (import it, or its CA, into Cert:\LocalMachine\Root)' }
        if ($script:PolicyErrors -band [System.Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch) { $why += "doesn't name $DomainController" }
        Write-ToolResult -Name 'LDAPS certificate' -Status Fail -Detail "$($why -join '; '): $summary"
    }
    $ssl.Dispose()
} catch {
    Write-ToolResult -Name 'LDAPS certificate' -Status Fail -Detail "TLS handshake failed: $($_.Exception.Message)"
} finally {
    $tcp.Dispose()
}

# 4 and 5. The bind, as BlueTrack makes it (LdapContextFactory with Use SSL on). It runs in a
# background job so a bind that never answers (seen when a DC binds to itself over LDAPS) is
# reported after BindTimeoutSeconds instead of hanging the script.
$who = if ($Credential) { $Credential.UserName } else { "$env:USERDOMAIN\$env:USERNAME (trusted connection)" }
$bindJob = Start-Job -ScriptBlock {
    $dc = $using:DomainController
    $ldapPort = $using:Port
    $base = $using:SearchBase
    $group = $using:GroupName
    $cred = $using:Credential
    Add-Type -AssemblyName System.DirectoryServices.AccountManagement
    $ns = 'System.DirectoryServices.AccountManagement'
    $options = [System.DirectoryServices.AccountManagement.ContextOptions]::SecureSocketLayer -bor [System.DirectoryServices.AccountManagement.ContextOptions]::Negotiate
    $container = if ($base) { $base } else { $null }
    try {
        $context = if ($cred) {
            New-Object "$ns.PrincipalContext"([System.DirectoryServices.AccountManagement.ContextType]::Domain, "${dc}:$ldapPort", $container, $options, $cred.UserName, $cred.GetNetworkCredential().Password)
        } else {
            New-Object "$ns.PrincipalContext"([System.DirectoryServices.AccountManagement.ContextType]::Domain, "${dc}:$ldapPort", $container, $options)
        }
        $found = [System.DirectoryServices.AccountManagement.GroupPrincipal]::FindByIdentity($context, $group)
        $count = if ($found) { @($found.GetMembers($false)).Count } else { -1 }
        $context.Dispose()
        [pscustomobject]@{ Ok = $true; MemberCount = $count; Error = $null }
    } catch {
        [pscustomobject]@{ Ok = $false; MemberCount = -1; Error = $_.Exception.GetBaseException().Message }
    }
}
$BindTimeoutSeconds = 30
if (Wait-Job $bindJob -Timeout $BindTimeoutSeconds) {
    $bind = Receive-Job $bindJob
    if ($bind.Ok) {
        Write-ToolResult -Name 'Bind' -Status Pass -Detail "as $who"
        if ($bind.MemberCount -lt 0) {
            Write-ToolResult -Name "Group '$GroupName'" -Status Warn -Detail 'not found (the bind worked; check the name or the search base)'
        } else {
            Write-ToolResult -Name "Group '$GroupName'" -Status Pass -Detail "$(Format-ToolCount $bind.MemberCount) member(s)"
        }
    } else {
        Write-ToolResult -Name 'Bind' -Status Fail -Detail "as ${who}: $($bind.Error)"
    }
} else {
    Write-ToolResult -Name 'Bind' -Status Fail -Detail "as ${who}: no answer within $BindTimeoutSeconds seconds (a domain controller binding to itself over LDAPS does this; run from another host)"
    Stop-Job $bindJob
}
Remove-Job $bindJob -Force

Exit-ToolRun
