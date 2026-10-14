<#
.SYNOPSIS
    Signs in to BlueTrack through SAML and/or OIDC without a browser, checks the
    session and the user's roles, then signs out (with SAML Single Logout when
    it's set up) and checks the session ended (D-219).
.DESCRIPTION
    Written for an authentik identity provider: it answers authentik's sign-in
    flow through authentik's flow executor API (/api/v3/flows/executor/...),
    the way authentik's own web page does. Stages handled: identification,
    password, consent, auto-submit (the SAML POST back) and redirect. A flow
    that asks for anything else (MFA, a captcha) stops with the stage's name:
    use an account without those, or test in a browser.

    Not checkable without a browser, reported as Info: the identity provider's
    LogoutResponse back to /api/auth/saml/slo, and a logout started at the
    identity provider (both are front-channel: the browser carries them).

    The password comes from -Credential, or from the CyberArk Credential
    Provider (-CpAppId, -CpSafe, -CpObject). It's kept in memory only and never
    printed. Use a test account: the sign-ins are real (they're audited as
    Logon events, and the account's BlueTrack user is created on first use).
.PARAMETER SiteUrl
    The site's address, e.g. https://bluetrack.company.com.
.PARAMETER Provider
    SAML, OIDC or Both (default Both).
.PARAMETER Credential
    The identity provider account. Leave out to use the Credential Provider parameters.
.PARAMETER CpAppId
    Credential Provider AppID, with -CpSafe and -CpObject.
.PARAMETER CpSafe
    Credential Provider Safe.
.PARAMETER CpObject
    Credential Provider Object.
.PARAMETER CredentialProviderCli
    The Credential Provider's CLIPasswordSDK.exe.
.EXAMPLE
    .\Test-BlueTrackSso.ps1 -SiteUrl https://bluetrack.company.com -Credential (Get-Credential)
.EXAMPLE
    .\Test-BlueTrackSso.ps1 -SiteUrl https://bluetrack.company.com -Provider SAML -CpAppId '<app id>' -CpSafe '<safe>' -CpObject '<object>'
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CredentialProviderCli', Justification = 'A path to the Credential Provider CLI, not a password.')]
[CmdletBinding(DefaultParameterSetName = 'Credential')]
param(
    [Parameter(Mandatory)] [string]$SiteUrl,
    [ValidateSet('SAML', 'OIDC', 'Both')] [string]$Provider = 'Both',
    [Parameter(ParameterSetName = 'Credential')] [System.Management.Automation.PSCredential]$Credential,
    [Parameter(Mandatory, ParameterSetName = 'CredentialProvider')] [string]$CpAppId,
    [Parameter(Mandatory, ParameterSetName = 'CredentialProvider')] [string]$CpSafe,
    [Parameter(Mandatory, ParameterSetName = 'CredentialProvider')] [string]$CpObject,
    [Parameter(ParameterSetName = 'CredentialProvider')] [string]$CredentialProviderCli = 'C:\Program Files\CyberArk\ApplicationPasswordSdk\CLIPasswordSDK.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BlueTrackTools.psm1') -Force

$SiteUrl = $SiteUrl.TrimEnd('/')
$api = "$SiteUrl/BlueTrack/api"

if ($PSCmdlet.ParameterSetName -eq 'CredentialProvider') {
    $out = & $CredentialProviderCli GetPassword /p "AppDescs.AppID=$CpAppId" /p "Query=Safe=$CpSafe;Object=$CpObject" /o PassProps.UserName,Password /d '|||'
    if ($LASTEXITCODE -ne 0) { throw "CLIPasswordSDK failed (exit $LASTEXITCODE)." }
    $parts = ($out -join '') -split '\|\|\|', 2
    $UserName = $parts[0]
    $Password = $parts[1]
} else {
    if (-not $Credential) { $Credential = Get-Credential -Message 'Identity provider account to sign in with' }
    $UserName = $Credential.UserName
    $Password = $Credential.GetNetworkCredential().Password
}

function Hide([string]$Text) { $Text -replace '(SAMLRequest|SAMLResponse|Signature|code|state|RelayState)=[^&\s]+', '$1=...' }

function Get-CsrfHeader($Session, [string]$IdpBase) {
    $headers = @{ Referer = $IdpBase }
    $cookie = $Session.Jar.GetCookies([Uri]$IdpBase) | Where-Object { $_.Name -eq 'authentik_csrf' } | Select-Object -First 1
    if ($cookie) { $headers['X-authentik-CSRF'] = $cookie.Value }
    $headers
}

function Invoke-AkFlow($Session, [string]$FlowPageUrl) {
    # Runs an authentik flow page (/if/flow/<slug>/?...) through the executor API.
    # Returns @{ Next = url } to continue at, or @{ Post = url; Form = @{...} } for an auto-submit.
    $page = [Uri]$FlowPageUrl
    $idp = "$($page.Scheme)://$($page.Authority)"
    $slug = ($page.AbsolutePath -split '/')[3]
    $executor = "$idp/api/v3/flows/executor/$slug/?query=$([Uri]::EscapeDataString($page.Query.TrimStart('?')))"
    $response = Invoke-ToolHttp -Session $Session -Url $executor -Headers (Get-CsrfHeader -Session $Session -IdpBase $idp)
    for ($step = 0; $step -lt 15; $step++) {
        for ($hop = 0; $null -ne $response.Location -and $hop -lt 5; $hop++) {
            $response = Invoke-ToolHttp -Session $Session -Url $response.Location -Headers (Get-CsrfHeader -Session $Session -IdpBase $idp)
        }
        if (-not "$($response.Body)".TrimStart().StartsWith('{')) { throw "authentik flow '$slug' answered HTTP $($response.Status), not JSON." }
        $challenge = $response.Body | ConvertFrom-Json
        $component = $challenge.component
        Write-Verbose "flow $slug stage $component"
        $answer = switch ($component) {
            'ak-stage-identification' { @{ component = $component; uid_field = $UserName } }
            'ak-stage-password' { @{ component = $component; password = $Password } }
            'ak-stage-consent' { @{ component = $component; token = $challenge.token } }
            'ak-provider-saml-native-logout' { @{ component = $component } }
            'xak-flow-redirect' { return @{ Next = [Uri]::new([Uri]$idp, [string]$challenge.to).AbsoluteUri } }
            'ak-stage-autosubmit' {
                $form = @{}
                foreach ($attribute in $challenge.attrs.PSObject.Properties) { $form[$attribute.Name] = [string]$attribute.Value }
                return @{ Post = [string]$challenge.url; Form = $form }
            }
            default { throw "authentik flow '$slug' asked for '$component', which this script can't answer (MFA or a captcha?). Use an account without it, or test in a browser." }
        }
        if ($component -eq 'ak-stage-password' -and $challenge.PSObject.Properties.Name -contains 'response_errors' -and $challenge.response_errors) {
            throw "authentik refused the sign-in for $UserName."
        }
        $response = Invoke-ToolHttp -Session $Session -Method POST -Url $executor -Json $answer -Headers (Get-CsrfHeader -Session $Session -IdpBase $idp)
    }
    throw "authentik flow '$slug' didn't finish in 15 steps."
}

function Invoke-Journey($Session, [string]$Url, [scriptblock]$StopWhen) {
    # Follows redirects and authentik flows until a page that is neither, or $StopWhen says stop.
    $method = 'GET'; $form = $null
    for ($hop = 0; $hop -lt 25; $hop++) {
        $response = Invoke-ToolHttp -Session $Session -Method $method -Url $Url -Form $form
        Write-Verbose "$($response.Status) $method $(Hide $Url)"
        $method = 'GET'; $form = $null
        if ($StopWhen -and (& $StopWhen $response)) { return $response }
        if ($response.Location) { $Url = $response.Location; continue }
        if ($response.Url -match '/if/flow/') {
            $result = Invoke-AkFlow -Session $Session -FlowPageUrl $response.Url
            if ($result.ContainsKey('Post')) { $Url = $result.Post; $method = 'POST'; $form = $result.Form } else { $Url = $result.Next }
            continue
        }
        return $response
    }
    throw 'Too many redirects.'
}

function Get-SessionState($Session) {
    (Invoke-ToolApi -Session $Session -Url "$api/auth/session").Data
}

function Test-Provider([string]$Name) {
    $session = Open-ToolHttpSession
    $start = if ($Name -eq 'SAML') { "$api/auth/saml/login" } else { "$api/auth/login/oidc" }
    try {
        $end = Invoke-Journey -Session $session -Url $start
        $state = Get-SessionState -Session $session
        if ($state -and $state.authenticated) {
            $me = (Invoke-ToolApi -Session $session -Url "$api/me").Data
            $roles = if ($me -and @($me.roleNames).Count) { ($me.roleNames -join ', ') } else { 'none (no mapped group)' }
            Write-ToolResult -Name "$Name sign-in" -Status Pass -Detail "as $UserName; roles: $roles"
        } else {
            Write-ToolResult -Name "$Name sign-in" -Status Fail -Detail "no BlueTrack session afterwards (last page: HTTP $($end.Status) $(Hide $end.Url))"
            return
        }
    } catch {
        Write-ToolResult -Name "$Name sign-in" -Status Fail -Detail (Hide -Text $_.Exception.Message)
        return
    }

    try {
        $logout = Invoke-ToolApi -Session $session -Method POST -Url "$api/auth/logout"
        if ($logout.Status -eq 200 -and $logout.Data.singleLogoutUrl) {
            Write-ToolResult -Name "$Name sign-out" -Status Info -Detail 'Single Logout is set up: signing out at the identity provider too'
            # Stop once back at BlueTrack's sign-in page, or at the identity provider's own pages.
            $null = Invoke-Journey -Session $session -Url "$SiteUrl$($logout.Data.singleLogoutUrl)" -StopWhen { param($r) $r.Url -match '/login\?signedOut' -or $r.Url -match '/if/user/' }
            Write-ToolResult -Name "$Name Single Logout" -Status Info -Detail 'not checked without a browser: the IdP''s LogoutResponse to /api/auth/saml/slo, and logout started at the IdP'
        } elseif ($logout.Status -in 200, 204) {
            Write-ToolResult -Name "$Name sign-out" -Status Info -Detail $(if ($Name -eq 'SAML') { 'Single Logout isn''t set up (no IdP Single Logout Destination): BlueTrack''s session only' } else { 'BlueTrack''s session (OIDC has no Single Logout in BlueTrack)' })
        } else {
            Write-ToolResult -Name "$Name sign-out" -Status Fail -Detail "HTTP $($logout.Status) from /api/auth/logout"
            return
        }
        $after = Get-SessionState -Session $session
        if ($after -and -not $after.authenticated) {
            Write-ToolResult -Name "$Name session ended" -Status Pass
        } else {
            Write-ToolResult -Name "$Name session ended" -Status Fail -Detail 'still signed in after Sign out'
        }
    } catch {
        Write-ToolResult -Name "$Name sign-out" -Status Fail -Detail (Hide $_.Exception.Message)
    }
}

Write-Host "BlueTrack SSO check: $SiteUrl, as $UserName" -ForegroundColor Cyan
if ($Provider -in 'SAML', 'Both') { Test-Provider 'SAML' }
if ($Provider -in 'OIDC', 'Both') { Test-Provider 'OIDC' }
$Password = $null
exit (Complete-ToolRun)
