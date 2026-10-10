<#
.SYNOPSIS
    Saved answers and step progress for Install-BlueTrack.ps1 (D-168), so a
    failed or partial install can be resumed or have single steps re-run.

.DESCRIPTION
    Both files live in Deploy/State/ (git-ignored), one pair per IIS site name:
      answers.<SiteName>.json   -- same shape as an -ConfigFile answer file,
                                   so it can be passed back in as one. Never
                                   holds a credential.
      progress.<SiteName>.json  -- last status of each install step.
#>

Set-StrictMode -Version Latest

# The answer names written to (and accepted from) an answers file. Run-time
# behavior switches (-Force, -Skip*, -Step, -Resume...) and SqlCredential are
# deliberately not here: the first are per-run choices, the second a secret.
$script:AnswerNames = @(
    'Environment', 'SqlServerInstance', 'DatabaseName', 'UseWindowsAuth', 'SeedTestData',
    'InstallNightlyJob', 'ImportSources', 'ExportFolderPath', 'EvdDatabaseName', 'BackupFolder',
    'GrantAppPoolSqlAccess', 'AppPoolSqlLogin', 'ResetIis',
    'SiteName', 'ApiInstallPath', 'WebInstallPath', 'Hostname', 'HttpPort', 'HttpsPort',
    'CertificateThumbprint', 'GenerateSelfSignedCert',
    'PrerequisiteSource', 'InstallerSourcePath'
)

function Get-BlueTrackAnswerName {
    <#
    .SYNOPSIS
        The answer names an answers file may hold.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param()
    return $script:AnswerNames
}

function Get-BlueTrackStatePath {
    <#
    .SYNOPSIS
        Path of the answers or progress file for one site.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string]$DeployRoot,
        [Parameter(Mandatory)] [string]$SiteName,
        [Parameter(Mandatory)] [ValidateSet('Answers', 'Progress')] [string]$Kind
    )
    $stateDir = Join-Path $DeployRoot 'State'
    return (Join-Path $stateDir "$($Kind.ToLowerInvariant()).$SiteName.json")
}

function Read-BlueTrackAnswerFile {
    <#
    .SYNOPSIS
        Reads an answer file (a -ConfigFile or a saved answers.<SiteName>.json)
        into a hashtable of known answer names. Properties starting with '_'
        are comments and ignored; any other unknown property is a warning.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)] [string]$Path
    )

    if (-not (Test-Path $Path)) { throw "Answer file '$Path' not found." }
    $json = Get-Content $Path -Raw | ConvertFrom-Json
    $answers = @{}
    foreach ($property in $json.PSObject.Properties) {
        if ($property.Name.StartsWith('_')) { continue }
        if ($property.Name -notin $script:AnswerNames) {
            Write-Warning "Answer file '$Path': unknown setting '$($property.Name)' ignored. Valid names: $($script:AnswerNames -join ', ')."
            continue
        }
        # A null/empty value means "not answered" -- leave it to the prompt.
        if ($null -eq $property.Value -or "$($property.Value)" -eq '') { continue }
        $answers[$property.Name] = $property.Value
    }
    return $answers
}

function Save-BlueTrackAnswerFile {
    <#
    .SYNOPSIS
        Writes the current answers to Deploy/State/answers.<SiteName>.json.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [hashtable]$Answers
    )

    $ordered = [ordered]@{
        _comment = 'Written by Install-BlueTrack.ps1. Reusable as -ConfigFile; -Resume and -Step read it automatically. Holds no credentials.'
    }
    foreach ($name in $script:AnswerNames) {
        if ($Answers.ContainsKey($name) -and $null -ne $Answers[$name] -and "$($Answers[$name])" -ne '') {
            $value = $Answers[$name]
            # [switch] values serialize as an object, not true/false.
            if ($value -is [System.Management.Automation.SwitchParameter]) { $value = $value.IsPresent }
            $ordered[$name] = $value
        }
    }

    if ($PSCmdlet.ShouldProcess($Path, 'Save install answers')) {
        $dir = Split-Path -Parent $Path
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $ordered | ConvertTo-Json | Set-Content -Path $Path -Encoding UTF8
    }
}

function Read-BlueTrackProgress {
    <#
    .SYNOPSIS
        Reads progress.<SiteName>.json into a hashtable of step name ->
        @{ Status; Time; Message }. Returns an empty table if there's no file.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)] [string]$Path
    )

    $progress = @{}
    if (-not (Test-Path $Path)) { return $progress }
    $json = Get-Content $Path -Raw | ConvertFrom-Json
    if ($json.PSObject.Properties['Steps']) {
        foreach ($step in $json.Steps.PSObject.Properties) {
            $progress[$step.Name] = @{
                Status  = $step.Value.Status
                Time    = $step.Value.Time
                Message = $step.Value.Message
            }
        }
    }
    return $progress
}

function Save-BlueTrackStepStatus {
    <#
    .SYNOPSIS
        Records one step's outcome in progress.<SiteName>.json.
    .PARAMETER Status
        Completed, Failed, Declined (a prerequisite the operator chose not to
        install) or NotApplicable (the step's condition was off this run).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [hashtable]$Progress,
        [Parameter(Mandatory)] [string]$Step,
        [Parameter(Mandatory)] [ValidateSet('Completed', 'Failed', 'Declined', 'NotApplicable')] [string]$Status,
        [string]$Message
    )

    $Progress[$Step] = @{ Status = $Status; Time = (Get-Date).ToString('s'); Message = $Message }

    if ($PSCmdlet.ShouldProcess($Path, "Record step $Step as $Status")) {
        $steps = [ordered]@{}
        foreach ($name in ($Progress.Keys | Sort-Object)) {
            $steps[$name] = [ordered]@{
                Status  = $Progress[$name].Status
                Time    = $Progress[$name].Time
                Message = $Progress[$name].Message
            }
        }
        $dir = Split-Path -Parent $Path
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        [ordered]@{ Steps = $steps } | ConvertTo-Json -Depth 4 | Set-Content -Path $Path -Encoding UTF8
    }
}

Export-ModuleMember -Function Get-BlueTrackAnswerName, Get-BlueTrackStatePath, Read-BlueTrackAnswerFile, Save-BlueTrackAnswerFile, Read-BlueTrackProgress, Save-BlueTrackStepStatus
