<#
.SYNOPSIS
    Shared helpers for the Tools\Test-BlueTrack*.ps1 checks (D-219): result
    lines with a summary and exit code, and an HTTP session that follows
    redirects by hand and keeps its own cookies.
.DESCRIPTION
    Runs on Windows PowerShell 5.1 and PowerShell 7. Nothing here writes a
    file or keeps a secret beyond the current process.
#>
Set-StrictMode -Version Latest

$script:Results = New-Object System.Collections.Generic.List[object]

function Write-ToolResult {
    <#
    .SYNOPSIS
        Records and prints one check: Pass, Fail, Warn (worth a look, not a
        failure) or Info (not checkable here, or just a fact).
    #>
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [ValidateSet('Pass', 'Fail', 'Warn', 'Info')] [string]$Status,
        [string]$Detail = ''
    )
    $script:Results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    $color = @{ Pass = 'Green'; Fail = 'Red'; Warn = 'Yellow'; Info = 'Gray' }[$Status]
    Write-Host ('{0,-5} {1}' -f $Status.ToUpperInvariant(), $Name) -ForegroundColor $color -NoNewline
    if ($Detail) { Write-Host " - $Detail" } else { Write-Host '' }
}

function Complete-ToolRun {
    <#
    .SYNOPSIS
        Prints the totals and returns the exit code: 1 if any check failed, else 0.
    #>
    $counts = foreach ($status in 'Pass', 'Fail', 'Warn', 'Info') {
        '{0} {1}' -f @($script:Results | Where-Object Status -eq $status).Count, $status.ToLowerInvariant()
    }
    Write-Host ''
    Write-Host ($counts -join ', ')
    if (@($script:Results | Where-Object Status -eq 'Fail').Count -gt 0) { return 1 }
    return 0
}

function Open-ToolHttpSession {
    <#
    .SYNOPSIS
        An HttpClient that never follows redirects on its own and keeps cookies
        in its own jar. .NET Framework's automatic cookie handling drops a cookie
        whose Path isn't a parent of the URL that set it (ASP.NET's OIDC
        correlation cookie), so Set-Cookie is applied here instead.
    #>
    param([switch]$UseDefaultCredentials)
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = [bool]$UseDefaultCredentials
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    [pscustomobject]@{ Client = $client; Jar = (New-Object System.Net.CookieContainer) }
}

function Invoke-ToolHttp {
    <#
    .SYNOPSIS
        One request, no redirect following. -Form sends a URL-encoded form,
        -Json a JSON body. Returns Status, Location (absolute), Body, Url.
    #>
    param(
        [Parameter(Mandatory)] $Session,
        [ValidateSet('GET', 'POST', 'PUT', 'DELETE')] [string]$Method = 'GET',
        [Parameter(Mandatory)] [string]$Url,
        [hashtable]$Form,
        $Json,
        [hashtable]$Headers = @{}
    )
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $Url)
    foreach ($key in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key, [string]$Headers[$key]) }
    $cookieHeader = $Session.Jar.GetCookieHeader([Uri]$Url)
    if ($cookieHeader) { [void]$request.Headers.TryAddWithoutValidation('Cookie', $cookieHeader) }
    if ($null -ne $Form) {
        $pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
        foreach ($key in $Form.Keys) { $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($key, [string]$Form[$key])) }
        $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
    }
    if ($null -ne $Json) {
        $request.Content = [System.Net.Http.StringContent]::new(($Json | ConvertTo-Json -Depth 6 -Compress), [Text.Encoding]::UTF8, 'application/json')
    }

    $response = $Session.Client.SendAsync($request).GetAwaiter().GetResult()
    $setCookies = $null
    if ($response.Headers.TryGetValues('Set-Cookie', [ref]$setCookies)) {
        foreach ($raw in $setCookies) { Add-ToolCookie -Jar $Session.Jar -Raw $raw -Url $Url }
    }
    $location = $null
    if ($null -ne $response.Headers.Location) {
        $location = if ($response.Headers.Location.IsAbsoluteUri) { $response.Headers.Location.AbsoluteUri } else { [Uri]::new([Uri]$Url, $response.Headers.Location).AbsoluteUri }
    }
    [pscustomobject]@{
        Status   = [int]$response.StatusCode
        Location = $location
        Body     = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Url      = $Url
    }
}

function Add-ToolCookie {
    param([Parameter(Mandatory)] $Jar, [Parameter(Mandatory)] [string]$Raw, [Parameter(Mandatory)] [string]$Url)
    $parts = $Raw -split ';'
    $nameValue = $parts[0] -split '=', 2
    $pathPart = $parts | Where-Object { $_.Trim() -like 'path=*' } | Select-Object -First 1
    $path = if ($pathPart) { ($pathPart -split '=', 2)[1].Trim() } else { '/' }
    $value = if ($nameValue.Count -gt 1) { $nameValue[1] } else { '' }
    $cookie = New-Object System.Net.Cookie($nameValue[0].Trim(), $value, $path, ([Uri]$Url).Host)
    $expiresPart = $parts | Where-Object { $_.Trim() -like 'expires=*' } | Select-Object -First 1
    if ($expiresPart) {
        $expires = [datetime]::MinValue
        if ([datetime]::TryParse(($expiresPart -split '=', 2)[1], [ref]$expires) -and $expires -lt (Get-Date)) { $cookie.Expired = $true }
    }
    $Jar.Add($cookie)
}

function Invoke-ToolApi {
    <#
    .SYNOPSIS
        A BlueTrack API call: adds X-Requested-With (required on writes, D-197)
        and parses a JSON answer. Returns Status and Data (null when not JSON).
    #>
    param(
        [Parameter(Mandatory)] $Session,
        [ValidateSet('GET', 'POST', 'PUT', 'DELETE')] [string]$Method = 'GET',
        [Parameter(Mandatory)] [string]$Url,
        $Json
    )
    $response = Invoke-ToolHttp -Session $Session -Method $Method -Url $Url -Json $Json -Headers @{ 'X-Requested-With' = 'XMLHttpRequest'; Accept = 'application/json' }
    $data = $null
    $text = "$($response.Body)".TrimStart()
    if ($text.StartsWith('{') -or $text.StartsWith('[')) { $data = $text | ConvertFrom-Json }
    [pscustomobject]@{ Status = $response.Status; Data = $data; Body = $response.Body }
}

function Format-ToolCount {
    <#
    .SYNOPSIS
        A count in the current culture's number format (D-218), e.g. 1,234.
    #>
    param([Parameter(Mandatory)] [AllowNull()] $Value)
    if ($null -eq $Value) { return '' }
    ([double]$Value).ToString('N0', [Globalization.CultureInfo]::CurrentCulture)
}

Export-ModuleMember -Function Write-ToolResult, Complete-ToolRun, Open-ToolHttpSession, Invoke-ToolHttp, Invoke-ToolApi, Format-ToolCount
