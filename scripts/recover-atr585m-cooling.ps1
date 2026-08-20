#Requires -Version 5.1

<#
.SYNOPSIS
    Performs a bounded, exact-owner recovery of the ATR585M cooling session through N.I.N.A.

.DESCRIPTION
    The default invocation is read-only. It verifies the frozen design baseline, the exact
    active N.I.N.A. profile and camera identity, Advanced API version, sequence/exposure idle
    state, and the absence of another process loading the ToupTek SDK. It also records three
    telemetry samples.

    Hardware-affecting recovery is enabled only when all three controls are supplied:

      -Execute
      -ConfirmFocusScanComplete
      -AuthorizationPhrase FOCUS-SCAN-COMPLETE-ATR585M-EXACT-OWNER

    A normal executing run performs exactly one N.I.N.A. camera disconnect, exactly one
    reconnect to the full stable ATR585M DeviceId, and exactly one direct -10 C cooling
    request. A ResumeAfterDisconnectedRun continuation cryptographically verifies an exact
    failed 1/0/0 run, inherits its disconnect count, never disconnects again, and performs
    only the remaining single reconnect and cooling request. There are no mutation retries.
    Success requires the real TemperatureSetPoint, a plausible temperature/power response,
    and three final samples within +/-0.5 C. AtTargetTemp is recorded for diagnosis but is
    never used as an acceptance signal.

    Every run writes append-only JSONL evidence and a final JSON summary under the ignored
    output/ tree. A non-zero exit means science acquisition remains blocked.

    ExpectedProfileId and ExpectedCameraId intentionally have no machine-specific defaults.
    Supply the exact active N.I.N.A. Profile GUID and complete stable ATR585M camera ID on
    every invocation, including read-only preflight. Machine-local identities must not be
    committed to this repository.
#>

[CmdletBinding()]
param(
    [uri]$ApiBaseUri = 'http://127.0.0.1:1888/v2/api',

    [string]$ExpectedProfileId = '',

    [string]$ExpectedCameraId = '',

    [string]$ExpectedNinaVersion = '3.2.0.9001',

    [string]$ExpectedAdvancedApiVersion = '2.2.15.2',

    [ValidateRange(1, 15)]
    [int]$TelemetryIntervalSeconds = 3,

    [ValidateRange(2, 30)]
    [int]$CoolingPollSeconds = 5,

    [ValidateRange(3, 20)]
    [int]$CoolingTimeoutMinutes = 15,

    [string]$AuditRoot = '',

    [switch]$Execute,

    [switch]$ConfirmFocusScanComplete,

    [string]$AuthorizationPhrase = '',

    [string]$ResumeAfterDisconnectedRun = '',

    [string]$ExpectedPriorSummarySha256 = '',

    [string]$ExpectedPriorAuditSha256 = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TargetTemperatureC = -10.0
$FinalToleranceC = 0.5
$SetPointToleranceC = 0.2
$MinimumCoolingProgressC = 0.2
$MinimumPowerEvidencePercent = 0.1
$RequiredFinalSamples = 3
$RequiredTelemetrySamples = 3
$RequiredAuthorizationPhrase = 'FOCUS-SCAN-COMPLETE-ATR585M-EXACT-OWNER'
$AuthorizedApiBaseUri = 'http://127.0.0.1:1888/v2/api'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
$IsResume = -not [string]::IsNullOrWhiteSpace($ResumeAfterDisconnectedRun)
$RunMode = $(if ($IsResume) { 'execute-resume-after-disconnect' } elseif ($Execute) { 'execute' } else { 'read-only-preflight' })
if ([string]::IsNullOrWhiteSpace($AuditRoot)) {
    $AuditRoot = Join-Path $RepositoryRoot 'output\commissioning\atr585m-cooling-recovery'
}
$RunId = ([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss.fffffffZ')) + '-' + ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$RunDirectory = Join-Path ([IO.Path]::GetFullPath($AuditRoot)) $RunId
$AuditPath = Join-Path $RunDirectory 'audit.jsonl'
$SummaryPath = Join-Path $RunDirectory 'summary.json'
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$StartedUtc = [DateTimeOffset]::UtcNow
$script:MutationCounts = [ordered]@{
    disconnect = 0
    connect = 0
    cool = 0
}
$script:LastPhase = 'initializing'
$script:ContinuationEvidence = $null

New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null

function ConvertTo-AuditJson {
    param([Parameter(Mandatory)]$InputObject)

    return ($InputObject | ConvertTo-Json -Depth 30 -Compress)
}

function Write-AuditEvent {
    param(
        [Parameter(Mandatory)][string]$Event,
        [Parameter(Mandatory)][ValidateSet('info', 'pass', 'warning', 'fail')][string]$Outcome,
        [object]$Data = $null
    )

    $entry = [ordered]@{
        schemaVersion = 1
        runId = $RunId
        timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        phase = $script:LastPhase
        event = $Event
        outcome = $Outcome
        data = $Data
    }
    $bytes = $Utf8NoBom.GetBytes((ConvertTo-AuditJson $entry) + [Environment]::NewLine)
    $stream = [IO.File]::Open($AuditPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Write-FinalSummary {
    param(
        [Parameter(Mandatory)][bool]$Completed,
        [Parameter(Mandatory)][bool]$ScienceAllowed,
        [Parameter(Mandatory)][string]$Result,
        [string]$Failure = '',
        [object]$FinalTelemetry = $null,
        [object]$FailureState = $null
    )

    $summary = [ordered]@{
        schemaVersion = 1
        runId = $RunId
        startedUtc = $StartedUtc.ToString('o')
        finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        mode = $RunMode
        completed = $Completed
        scienceAllowed = $ScienceAllowed
        result = $Result
        failure = $Failure
        exactProfileId = $ExpectedProfileId
        exactCameraId = $ExpectedCameraId
        targetTemperatureC = $TargetTemperatureC
        finalToleranceC = $FinalToleranceC
        atTargetTempWasUsedForAcceptance = $false
        mutationCounts = $script:MutationCounts
        finalTelemetry = $FinalTelemetry
        failureState = $FailureState
        continuation = $script:ContinuationEvidence
        auditPath = $AuditPath
    }
    $temporaryPath = $SummaryPath + '.tmp'
    $summaryBytes = $Utf8NoBom.GetBytes(($summary | ConvertTo-Json -Depth 30))
    $summaryStream = [IO.File]::Open($temporaryPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $summaryStream.Write($summaryBytes, 0, $summaryBytes.Length)
        $summaryStream.Flush($true)
    }
    finally {
        $summaryStream.Dispose()
    }
    if (Test-Path -LiteralPath $SummaryPath) {
        throw "Refusing to replace an existing immutable run summary: $SummaryPath"
    }
    Move-Item -LiteralPath $temporaryPath -Destination $SummaryPath
}

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ConfiguredExactIdentity {
    Assert-True (-not [string]::IsNullOrWhiteSpace($ExpectedProfileId)) '-ExpectedProfileId is required and must come from the active machine-local N.I.N.A. profile.'
    $parsedProfileId = [Guid]::Empty
    Assert-True ([Guid]::TryParse($ExpectedProfileId, [ref]$parsedProfileId) -and $parsedProfileId -ne [Guid]::Empty) '-ExpectedProfileId must be a non-empty GUID.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($ExpectedCameraId)) '-ExpectedCameraId is required and must be the complete stable ATR585M ID from the active machine-local N.I.N.A. profile.'
    Assert-True ($ExpectedCameraId.StartsWith('ToupTek_', [StringComparison]::Ordinal)) '-ExpectedCameraId must select N.I.N.A. ToupTek ownership, not an alias or ASCOM wrapper.'
    Assert-True ($ExpectedCameraId.IndexOf('usb#vid_0547&pid_157c#', [StringComparison]::OrdinalIgnoreCase) -ge 0) '-ExpectedCameraId must identify the ATR585M USB VID/PID 0547:157C.'
    Assert-True ($ExpectedCameraId -notmatch '\s') '-ExpectedCameraId may not contain whitespace.'
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory)]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Advanced API response is missing required property '$Name'."
    }
    return $property.Value
}

function Get-OptionalProperty {
    param(
        [Parameter(Mandatory)]$InputObject,
        [Parameter(Mandatory)][string]$Name,
        [object]$DefaultValue = $null
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $DefaultValue }
    return $property.Value
}

function Get-ApiUri {
    param([Parameter(Mandatory)][string]$RelativePath)

    return ($ApiBaseUri.AbsoluteUri.TrimEnd('/') + '/' + $RelativePath.TrimStart('/'))
}

function Invoke-AdvancedApiRaw {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Purpose,
        [ValidateSet('', 'disconnect', 'connect', 'cool')][string]$MutationKind = ''
    )

    $uri = Get-ApiUri $RelativePath
    if (-not [string]::IsNullOrWhiteSpace($MutationKind)) {
        if (-not $Execute -or -not $ConfirmFocusScanComplete -or $AuthorizationPhrase -cne $RequiredAuthorizationPhrase) {
            throw "Mutation '$MutationKind' is locked. Supply all three explicit execution controls after the focus scan is complete."
        }
        $script:MutationCounts[$MutationKind] = [int]$script:MutationCounts[$MutationKind] + 1
        if ([int]$script:MutationCounts[$MutationKind] -gt 1) {
            throw "Bound violated: mutation '$MutationKind' was requested more than once. No retry is permitted."
        }
    }

    Write-AuditEvent -Event 'advanced-api.request' -Outcome 'info' -Data ([ordered]@{
        purpose = $Purpose
        uri = $uri
        mutationKind = $MutationKind
        mutationAttempt = $(if ($MutationKind) { [int]$script:MutationCounts[$MutationKind] } else { 0 })
    })

    $request = [Net.HttpWebRequest]::Create($uri)
    $request.Method = 'GET'
    $request.Timeout = 15000
    $request.ReadWriteTimeout = 15000
    $request.KeepAlive = $false
    $request.AllowAutoRedirect = $false
    $request.Proxy = $null

    $response = $null
    try {
        $response = [Net.HttpWebResponse]$request.GetResponse()
    }
    catch [Net.WebException] {
        if ($null -eq $_.Exception.Response) {
            throw "Advanced API request failed without an HTTP response ($Purpose): $($_.Exception.Message)"
        }
        $response = [Net.HttpWebResponse]$_.Exception.Response
    }

    try {
        $reader = New-Object IO.StreamReader($response.GetResponseStream())
        try {
            $body = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
        $statusCode = [int]$response.StatusCode
    }
    finally {
        $response.Dispose()
    }

    $envelope = $null
    if (-not [string]::IsNullOrWhiteSpace($body)) {
        try {
            $envelope = $body | ConvertFrom-Json
        }
        catch {
            throw "Advanced API returned non-JSON content for $Purpose (HTTP $statusCode)."
        }
    }

    $success = $false
    $apiStatusCode = $null
    $apiError = ''
    if ($null -ne $envelope) {
        if ($null -ne $envelope.PSObject.Properties['Success']) { $success = [bool]$envelope.Success }
        if ($null -ne $envelope.PSObject.Properties['StatusCode']) { $apiStatusCode = [int]$envelope.StatusCode }
        if ($null -ne $envelope.PSObject.Properties['Error']) { $apiError = [string]$envelope.Error }
    }

    Write-AuditEvent -Event 'advanced-api.response' -Outcome $(if ($statusCode -ge 200 -and $statusCode -lt 300 -and $success) { 'pass' } else { 'warning' }) -Data ([ordered]@{
        purpose = $Purpose
        httpStatusCode = $statusCode
        apiStatusCode = $apiStatusCode
        success = $success
        error = $apiError
    })

    return [pscustomobject]@{
        HttpStatusCode = $statusCode
        Success = $success
        Error = $apiError
        Envelope = $envelope
        Response = $(if ($null -ne $envelope -and $null -ne $envelope.PSObject.Properties['Response']) { $envelope.Response } else { $null })
    }
}

function Assert-ApiSuccess {
    param(
        [Parameter(Mandatory)]$ApiResult,
        [Parameter(Mandatory)][string]$Purpose
    )

    if ($ApiResult.HttpStatusCode -lt 200 -or $ApiResult.HttpStatusCode -ge 300 -or -not $ApiResult.Success) {
        throw "Advanced API failed for $Purpose (HTTP $($ApiResult.HttpStatusCode), error '$($ApiResult.Error)')."
    }
    return $ApiResult.Response
}

function Get-ExactCameraInfo {
    param(
        [bool]$RequireConnected = $true,
        [bool]$RequireExactIdentity = $true
    )

    $result = Invoke-AdvancedApiRaw -RelativePath 'equipment/camera/info' -Purpose 'camera telemetry'
    $info = Assert-ApiSuccess -ApiResult $result -Purpose 'camera telemetry'
    $connected = [bool](Get-RequiredProperty $info 'Connected')
    if ($RequireConnected) {
        Assert-True $connected 'The N.I.N.A. camera is not connected.'
    }
    if ($RequireExactIdentity) {
        $deviceId = [string](Get-RequiredProperty $info 'DeviceId')
        Assert-True ($deviceId -ceq $ExpectedCameraId) "Connected camera DeviceId '$deviceId' does not exactly match the bound ATR585M DeviceId."
    }
    return $info
}

function ConvertTo-TelemetrySample {
    param(
        [Parameter(Mandatory)]$CameraInfo,
        [Parameter(Mandatory)][string]$Label
    )

    $temperature = [double](Get-RequiredProperty $CameraInfo 'Temperature')
    $setPoint = [double](Get-RequiredProperty $CameraInfo 'TemperatureSetPoint')
    $power = [double](Get-RequiredProperty $CameraInfo 'CoolerPower')
    if ([double]::IsNaN($temperature) -or [double]::IsInfinity($temperature) -or
        [double]::IsNaN($setPoint) -or [double]::IsInfinity($setPoint) -or
        [double]::IsNaN($power) -or [double]::IsInfinity($power)) {
        throw 'ATR585M telemetry contains a non-finite temperature, setpoint, or cooler-power value.'
    }

    return [pscustomobject][ordered]@{
        timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        label = $Label
        connected = [bool](Get-RequiredProperty $CameraInfo 'Connected')
        deviceId = [string](Get-RequiredProperty $CameraInfo 'DeviceId')
        cameraState = [string](Get-RequiredProperty $CameraInfo 'CameraState')
        isExposing = [bool](Get-RequiredProperty $CameraInfo 'IsExposing')
        liveViewEnabled = [bool](Get-RequiredProperty $CameraInfo 'LiveViewEnabled')
        temperatureC = $temperature
        temperatureSetPointC = $setPoint
        targetTempCacheC = [double](Get-RequiredProperty $CameraInfo 'TargetTemp')
        coolerOn = [bool](Get-RequiredProperty $CameraInfo 'CoolerOn')
        coolerPowerPercent = $power
        atTargetTempApiFlag = [bool](Get-RequiredProperty $CameraInfo 'AtTargetTemp')
        atTargetTempAccepted = $false
        gain = [int](Get-RequiredProperty $CameraInfo 'Gain')
        offset = [int](Get-RequiredProperty $CameraInfo 'Offset')
        binX = [int](Get-RequiredProperty $CameraInfo 'BinX')
        binY = [int](Get-RequiredProperty $CameraInfo 'BinY')
        readoutMode = [int](Get-RequiredProperty $CameraInfo 'ReadoutMode')
    }
}

function Get-SequenceStatusEvidence {
    $result = Invoke-AdvancedApiRaw -RelativePath 'sequence/state' -Purpose 'advanced sequence state'
    if ($result.HttpStatusCode -eq 409 -and $result.Error -match 'not initialized') {
        return [pscustomobject]@{
            initialized = $false
            statuses = @()
            activeStatuses = @()
        }
    }
    $state = Assert-ApiSuccess -ApiResult $result -Purpose 'advanced sequence state'
    $json = $state | ConvertTo-Json -Depth 30 -Compress
    $matches = [regex]::Matches($json, '"Status"\s*:\s*"([^"]+)"')
    $statuses = @($matches | ForEach-Object { $_.Groups[1].Value })
    $activeStatuses = @($statuses | Where-Object { $_ -match '^(RUNNING|PAUSED|SUSPENDED|WAITING)$' })
    return [pscustomobject]@{
        initialized = $true
        statuses = $statuses
        activeStatuses = $activeStatuses
    }
}

function Assert-NinaIdle {
    param([Parameter(Mandatory)][string]$Label)

    $sequence = Get-SequenceStatusEvidence
    Assert-True (@($sequence.activeStatuses).Count -eq 0) "N.I.N.A. advanced sequence is active ($(@($sequence.activeStatuses) -join ', '))."

    $camera = Get-ExactCameraInfo
    $state = [string](Get-RequiredProperty $camera 'CameraState')
    Assert-True (-not [bool](Get-RequiredProperty $camera 'IsExposing')) 'N.I.N.A. reports an active camera exposure.'
    Assert-True (-not [bool](Get-RequiredProperty $camera 'LiveViewEnabled')) 'N.I.N.A. camera live view is active.'
    Assert-True ($state -notmatch '(?i)expos|download|read|wait|capture') "N.I.N.A. camera state '$state' is not idle."

    Write-AuditEvent -Event 'nina.idle-gate' -Outcome 'pass' -Data ([ordered]@{
        label = $Label
        sequenceInitialized = $sequence.initialized
        sequenceStatuses = @($sequence.statuses)
        cameraState = $state
        isExposing = [bool]$camera.IsExposing
        liveViewEnabled = [bool]$camera.LiveViewEnabled
    })
    return $camera
}

function Assert-ActiveProfile {
    $result = Invoke-AdvancedApiRaw -RelativePath 'profile/show?active=true' -Purpose 'active N.I.N.A. profile'
    $profile = Assert-ApiSuccess -ApiResult $result -Purpose 'active N.I.N.A. profile'
    $profileId = [string](Get-RequiredProperty $profile 'Id')
    $cameraSettings = Get-RequiredProperty $profile 'CameraSettings'
    $profileCameraId = [string](Get-RequiredProperty $cameraSettings 'Id')

    Assert-True ($profileId -ceq $ExpectedProfileId) "Active N.I.N.A. profile '$profileId' is not the explicitly authorized profile '$ExpectedProfileId'."
    Assert-True ($profileCameraId -ceq $ExpectedCameraId) "Active profile camera '$profileCameraId' is not the exact ATR585M binding."
    Assert-True ([math]::Abs([double](Get-RequiredProperty $cameraSettings 'Temperature') - $TargetTemperatureC) -lt 0.001) 'Active profile cooling target is not -10 C.'

    Write-AuditEvent -Event 'nina.active-profile-gate' -Outcome 'pass' -Data ([ordered]@{
        profileId = $profileId
        profileName = [string](Get-RequiredProperty $profile 'Name')
        lastUsed = [string](Get-RequiredProperty $profile 'LastUsed')
        cameraId = $profileCameraId
        cameraName = [string](Get-RequiredProperty $cameraSettings 'LastDeviceName')
        targetTemperatureC = [double](Get-RequiredProperty $cameraSettings 'Temperature')
        coolingDurationMinutes = [double](Get-RequiredProperty $cameraSettings 'CoolingDuration')
        gain = [int](Get-RequiredProperty $cameraSettings 'Gain')
        offset = [int](Get-RequiredProperty $cameraSettings 'Offset')
        normalReadoutMode = [int](Get-RequiredProperty $cameraSettings 'ReadoutModeForNormalImages')
        snapshotReadoutMode = [int](Get-RequiredProperty $cameraSettings 'ReadoutModeForSnapImages')
    })
    return $profile
}

function Get-FileEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    return [pscustomobject][ordered]@{
        path = $item.FullName
        version = $item.VersionInfo.FileVersion
        length = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}

function Get-LiveFileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($stream)
        return -join ($hash | ForEach-Object { $_.ToString('X2') })
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Assert-SoftwareAndOwnerIdentity {
    $ninaProcesses = @(Get-Process -Name 'NINA' -ErrorAction SilentlyContinue)
    Assert-True ($ninaProcesses.Count -eq 1) "Expected exactly one N.I.N.A. process, found $($ninaProcesses.Count)."
    $nina = $ninaProcesses[0]
    $ninaExecutable = Get-FileEvidence -Path $nina.Path
    Assert-True ([string]$ninaExecutable.version -eq $ExpectedNinaVersion) "N.I.N.A. version '$($ninaExecutable.version)' does not match audited version '$ExpectedNinaVersion'."

    $advancedApiCandidates = @(Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'NINA\Plugins') -Filter 'ninaAPI.dll' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.VersionInfo.FileVersion -eq $ExpectedAdvancedApiVersion })
    Assert-True ($advancedApiCandidates.Count -eq 1) "Expected exactly one Advanced API $ExpectedAdvancedApiVersion assembly, found $($advancedApiCandidates.Count)."
    $advancedApi = Get-FileEvidence -Path $advancedApiCandidates[0].FullName

    $knownCameraHostPattern = '^(ToupSky|ToupView|SharpCap|ASICap|AltairCapture|RisingSky|MallinCam|MaxIm|SGPro|SequenceGeneratorPro|APT|AstroPhotographyTool)$'
    $knownOtherHosts = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Id -ne $nina.Id -and $_.ProcessName -match $knownCameraHostPattern
    } | ForEach-Object {
        [pscustomobject]@{ pid = $_.Id; name = $_.ProcessName; path = $(try { $_.Path } catch { '' }) }
    })
    $knownOtherHostNames = @($knownOtherHosts | ForEach-Object { $_.name }) -join ', '
    Assert-True ($knownOtherHosts.Count -eq 0) "Another known camera host is running: $knownOtherHostNames."

    $sdkOwners = New-Object System.Collections.Generic.List[object]
    $moduleInspectionDenied = 0
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        try {
            foreach ($module in @($process.Modules)) {
                if ([IO.Path]::GetFileName($module.FileName) -ieq 'toupcam.dll') {
                    $sdkOwners.Add([pscustomobject][ordered]@{
                        pid = $process.Id
                        name = $process.ProcessName
                        module = $module.FileName
                    })
                }
            }
        }
        catch {
            $moduleInspectionDenied++
        }
    }
    $uniqueSdkOwners = @($sdkOwners | Sort-Object pid, module -Unique)
    $otherSdkOwners = @($uniqueSdkOwners | Where-Object { $_.pid -ne $nina.Id })
    $ninaSdkOwners = @($uniqueSdkOwners | Where-Object { $_.pid -eq $nina.Id })
    $otherSdkOwnerNames = @($otherSdkOwners | ForEach-Object { $_.name }) -join ', '
    Assert-True ($otherSdkOwners.Count -eq 0) "A non-N.I.N.A. process has loaded toupcam.dll: $otherSdkOwnerNames."
    Assert-True ($ninaSdkOwners.Count -eq 1) "Could not prove that the sole N.I.N.A. process owns exactly one loaded toupcam.dll (found $($ninaSdkOwners.Count))."
    $sdk = Get-FileEvidence -Path $ninaSdkOwners[0].module

    Write-AuditEvent -Event 'software-and-owner-gate' -Outcome 'pass' -Data ([ordered]@{
        nina = [ordered]@{
            pid = $nina.Id
            startTimeUtc = $nina.StartTime.ToUniversalTime().ToString('o')
            executable = $ninaExecutable
        }
        advancedApi = $advancedApi
        toupcamSdk = $sdk
        loadedToupcamOwners = $uniqueSdkOwners
        knownOtherCameraHosts = $knownOtherHosts
        inaccessibleProcessModuleLists = $moduleInspectionDenied
        interpretation = 'Protected process module lists may be inaccessible; all visible toupcam.dll loaders and known user camera hosts were checked.'
    })
    return [pscustomobject]@{
        Nina = $nina
        NinaExecutable = $ninaExecutable
        AdvancedApi = $advancedApi
        ToupCamSdk = $sdk
    }
}

function Assert-CameraIsSelectable {
    $result = Invoke-AdvancedApiRaw -RelativePath 'equipment/camera/list-devices' -Purpose 'N.I.N.A. camera chooser inventory'
    $devices = @(Assert-ApiSuccess -ApiResult $result -Purpose 'N.I.N.A. camera chooser inventory')
    $matches = @($devices | Where-Object { [string]$_.Id -ceq $ExpectedCameraId })
    Assert-True ($matches.Count -eq 1) "The N.I.N.A. chooser does not contain exactly one exact ATR585M DeviceId (found $($matches.Count))."
    Write-AuditEvent -Event 'camera-chooser-gate' -Outcome 'pass' -Data ([ordered]@{
        exactMatchCount = $matches.Count
        exactDeviceId = $ExpectedCameraId
        displayName = [string]$matches[0].DisplayName
    })
}

function Get-TelemetrySeries {
    param(
        [Parameter(Mandatory)][string]$Label,
        [int]$Count = $RequiredTelemetrySamples,
        [int]$IntervalSeconds = $TelemetryIntervalSeconds,
        [switch]$EnforceIdle
    )

    $samples = New-Object System.Collections.Generic.List[object]
    for ($index = 1; $index -le $Count; $index++) {
        if ($EnforceIdle) {
            $info = Assert-NinaIdle -Label "$Label-$index"
        }
        else {
            $info = Get-ExactCameraInfo
        }
        $sample = ConvertTo-TelemetrySample -CameraInfo $info -Label "$Label-$index"
        $samples.Add($sample)
        Write-AuditEvent -Event 'camera.telemetry' -Outcome 'info' -Data $sample
        if ($index -lt $Count) {
            Start-Sleep -Seconds $IntervalSeconds
        }
    }
    return @($samples | ForEach-Object { $_ })
}

function Test-PersistentZeroTelemetry {
    param([Parameter(Mandatory)][object[]]$Samples)

    if ($Samples.Count -lt $RequiredTelemetrySamples) { return $false }
    $tail = @($Samples | Select-Object -Last $RequiredTelemetrySamples)
    return (@($tail | Where-Object {
        [math]::Abs([double]$_.temperatureC) -lt 0.000001 -and
        [math]::Abs([double]$_.coolerPowerPercent) -lt 0.000001
    }).Count -eq $RequiredTelemetrySamples)
}

function Wait-ForConnectionState {
    param(
        [Parameter(Mandatory)][bool]$Connected,
        [ValidateRange(5, 60)][int]$TimeoutSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $info = Get-ExactCameraInfo -RequireConnected:$false -RequireExactIdentity:$Connected
        $actual = [bool](Get-RequiredProperty $info 'Connected')
        Write-AuditEvent -Event 'camera.connection-state' -Outcome 'info' -Data ([ordered]@{
            expectedConnected = $Connected
            actualConnected = $actual
            deviceId = [string](Get-OptionalProperty -InputObject $info -Name 'DeviceId' -DefaultValue '')
            deviceIdWasPresent = ($null -ne $info.PSObject.Properties['DeviceId'])
        })
        if ($actual -eq $Connected) { return $info }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "N.I.N.A. camera did not reach Connected=$Connected within $TimeoutSeconds seconds. No mutation retry will be made."
}

function Save-NinaLogSnapshot {
    param([Parameter(Mandatory)][string]$Label)

    $logDirectory = Join-Path $env:LOCALAPPDATA 'NINA\Logs'
    $latest = Get-ChildItem -LiteralPath $logDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $latest) {
        Write-AuditEvent -Event 'nina.log-snapshot' -Outcome 'warning' -Data ([ordered]@{ label = $Label; reason = 'No N.I.N.A. log found.' })
        return
    }
    $destination = Join-Path $RunDirectory ("nina-log-$Label-tail.txt")
    $tail = @(Get-Content -LiteralPath $latest.FullName -Tail 600 -ErrorAction Stop)
    [IO.File]::WriteAllLines($destination, [string[]]$tail, $Utf8NoBom)
    Write-AuditEvent -Event 'nina.log-snapshot' -Outcome 'info' -Data ([ordered]@{
        label = $Label
        sourcePath = $latest.FullName
        sourceSha256 = Get-LiveFileSha256 -Path $latest.FullName
        sourceLastWriteUtc = $latest.LastWriteTimeUtc.ToString('o')
        tailPath = $destination
        tailLineCount = $tail.Count
    })
}

function Assert-Sha256Text {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Name
    )

    Assert-True ($Value -match '^[0-9A-Fa-f]{64}$') "$Name must be an explicit 64-character SHA-256 value."
}

function Assert-PriorDisconnectedRun {
    Assert-True $IsResume 'Internal error: prior-run validation was called outside resume mode.'
    Assert-True ([IO.Path]::IsPathRooted($ResumeAfterDisconnectedRun)) '-ResumeAfterDisconnectedRun must be an absolute run directory or absolute summary.json path.'
    Assert-Sha256Text -Value $ExpectedPriorSummarySha256 -Name 'ExpectedPriorSummarySha256'
    Assert-Sha256Text -Value $ExpectedPriorAuditSha256 -Name 'ExpectedPriorAuditSha256'

    $inputPath = [IO.Path]::GetFullPath($ResumeAfterDisconnectedRun)
    if (Test-Path -LiteralPath $inputPath -PathType Container) {
        $priorDirectory = $inputPath.TrimEnd('\', '/')
        $priorSummaryPath = Join-Path $priorDirectory 'summary.json'
    }
    elseif (Test-Path -LiteralPath $inputPath -PathType Leaf) {
        Assert-True ([IO.Path]::GetFileName($inputPath) -ceq 'summary.json') 'A prior-run file argument must be the immutable summary.json.'
        $priorSummaryPath = $inputPath
        $priorDirectory = Split-Path $priorSummaryPath -Parent
    }
    else {
        throw "Prior recovery run does not exist: $inputPath"
    }

    $priorAuditPath = Join-Path $priorDirectory 'audit.jsonl'
    Assert-True (Test-Path -LiteralPath $priorSummaryPath -PathType Leaf) "Prior summary is missing: $priorSummaryPath"
    Assert-True (Test-Path -LiteralPath $priorAuditPath -PathType Leaf) "Prior audit is missing: $priorAuditPath"
    foreach ($evidencePath in @($priorDirectory, $priorSummaryPath, $priorAuditPath)) {
        $item = Get-Item -LiteralPath $evidencePath -Force
        Assert-True (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Prior evidence may not be a reparse point: $evidencePath"
    }

    $summaryItemBefore = Get-Item -LiteralPath $priorSummaryPath
    $auditItemBefore = Get-Item -LiteralPath $priorAuditPath
    $summaryHash = (Get-FileHash -LiteralPath $priorSummaryPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $auditHash = (Get-FileHash -LiteralPath $priorAuditPath -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-True ($summaryHash -ceq $ExpectedPriorSummarySha256.ToUpperInvariant()) "Prior summary SHA-256 mismatch. Actual: $summaryHash"
    Assert-True ($auditHash -ceq $ExpectedPriorAuditSha256.ToUpperInvariant()) "Prior audit SHA-256 mismatch. Actual: $auditHash"

    $summary = Get-Content -LiteralPath $priorSummaryPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $events = @()
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $priorAuditPath) {
        $lineNumber++
        try { $events += ($line | ConvertFrom-Json -ErrorAction Stop) }
        catch { throw "Prior audit JSONL line $lineNumber is invalid: $($_.Exception.Message)" }
    }
    Assert-True ($events.Count -gt 0) 'Prior audit contains no events.'

    $priorRunId = [string](Get-RequiredProperty $summary 'runId')
    Assert-True ($priorRunId -ceq [IO.Path]::GetFileName($priorDirectory)) 'Prior summary runId does not match its immutable run directory.'
    Assert-True ([string](Get-RequiredProperty $summary 'mode') -ceq 'execute') 'Prior run was not a normal executing recovery.'
    Assert-True (-not [bool](Get-RequiredProperty $summary 'completed')) 'Prior run is already completed and cannot be resumed.'
    Assert-True (-not [bool](Get-RequiredProperty $summary 'scienceAllowed')) 'Prior run already allowed science and cannot be resumed.'
    Assert-True ([string](Get-RequiredProperty $summary 'result') -ceq 'SCIENCE_BLOCKED') 'Prior run did not fail closed.'
    Assert-True ([string](Get-RequiredProperty $summary 'exactProfileId') -ceq $ExpectedProfileId) 'Prior run profile identity differs from this exact recovery.'
    Assert-True ([string](Get-RequiredProperty $summary 'exactCameraId') -ceq $ExpectedCameraId) 'Prior run camera identity differs from this exact recovery.'
    Assert-True ([string](Get-RequiredProperty $summary 'failure') -ceq "Advanced API response is missing required property 'DeviceId'.") 'Prior run did not stop at the audited disconnected-info compatibility failure.'
    $priorCounts = Get-RequiredProperty $summary 'mutationCounts'
    Assert-True ([int](Get-RequiredProperty $priorCounts 'disconnect') -eq 1 -and [int](Get-RequiredProperty $priorCounts 'connect') -eq 0 -and [int](Get-RequiredProperty $priorCounts 'cool') -eq 0) 'Prior summary mutation counts are not exactly disconnect=1/connect=0/cool=0.'
    Assert-True ([IO.Path]::GetFullPath([string](Get-RequiredProperty $summary 'auditPath')) -ceq [IO.Path]::GetFullPath($priorAuditPath)) 'Prior summary auditPath does not identify the audited file.'

    Assert-True (@($events | Where-Object { [string]$_.runId -cne $priorRunId }).Count -eq 0) 'Prior audit contains an event from a different runId.'
    Assert-True ([string]$events[0].event -ceq 'run.started') 'Prior audit does not start with run.started.'
    Assert-True ([string]$events[$events.Count - 1].event -ceq 'run.failed') 'Prior audit does not end with run.failed.'
    $authorizationEvents = @($events | Where-Object { [string]$_.event -ceq 'execution-authorization' -and [string]$_.outcome -ceq 'pass' })
    Assert-True ($authorizationEvents.Count -eq 1) 'Prior audit does not contain exactly one passed execution authorization.'

    $mutationRequests = @($events | Where-Object {
        [string]$_.event -ceq 'advanced-api.request' -and
        -not [string]::IsNullOrWhiteSpace([string](Get-OptionalProperty -InputObject $_.data -Name 'mutationKind' -DefaultValue ''))
    })
    $priorDisconnectRequests = @($mutationRequests | Where-Object { [string]$_.data.mutationKind -ceq 'disconnect' })
    $priorConnectRequests = @($mutationRequests | Where-Object { [string]$_.data.mutationKind -ceq 'connect' })
    $priorCoolRequests = @($mutationRequests | Where-Object { [string]$_.data.mutationKind -ceq 'cool' })
    Assert-True ($priorDisconnectRequests.Count -eq 1 -and $priorConnectRequests.Count -eq 0 -and $priorCoolRequests.Count -eq 0) 'Prior audit mutation requests are not exactly disconnect=1/connect=0/cool=0.'
    Assert-True ([int]$priorDisconnectRequests[0].data.mutationAttempt -eq 1) 'Prior disconnect was not mutation attempt 1.'
    Assert-True ([string]$priorDisconnectRequests[0].data.uri -ceq ($AuthorizedApiBaseUri + '/equipment/camera/disconnect')) 'Prior disconnect used an unexpected endpoint.'
    $disconnectResponses = @($events | Where-Object {
        [string]$_.event -ceq 'advanced-api.response' -and
        [string](Get-OptionalProperty -InputObject $_.data -Name 'purpose' -DefaultValue '') -ceq 'single exact-owner ATR585M disconnect'
    })
    Assert-True ($disconnectResponses.Count -eq 1) 'Prior audit does not contain exactly one disconnect response.'
    Assert-True ([string]$disconnectResponses[0].outcome -ceq 'pass' -and [int]$disconnectResponses[0].data.httpStatusCode -eq 200 -and [int]$disconnectResponses[0].data.apiStatusCode -eq 200 -and [bool]$disconnectResponses[0].data.success) 'Prior disconnect did not complete successfully.'
    Assert-True (@($events | Where-Object { [string]$_.event -ceq 'run.completed' -or [string]$_.event -ceq 'cooling.acceptance' }).Count -eq 0) 'Prior audit contains a completion/acceptance event and cannot be resumed.'

    $priorOwnerEvents = @($events | Where-Object { [string]$_.event -ceq 'software-and-owner-gate' -and [string]$_.outcome -ceq 'pass' })
    Assert-True ($priorOwnerEvents.Count -ge 1) 'Prior audit has no passed exact-owner gate.'
    $priorNina = $priorOwnerEvents[$priorOwnerEvents.Count - 1].data.nina

    $summaryItemAfter = Get-Item -LiteralPath $priorSummaryPath
    $auditItemAfter = Get-Item -LiteralPath $priorAuditPath
    $summaryHashAfter = (Get-FileHash -LiteralPath $priorSummaryPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $auditHashAfter = (Get-FileHash -LiteralPath $priorAuditPath -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-True ($summaryHashAfter -ceq $summaryHash -and $summaryItemAfter.Length -eq $summaryItemBefore.Length -and $summaryItemAfter.LastWriteTimeUtc -eq $summaryItemBefore.LastWriteTimeUtc) 'Prior summary changed while it was being verified.'
    Assert-True ($auditHashAfter -ceq $auditHash -and $auditItemAfter.Length -eq $auditItemBefore.Length -and $auditItemAfter.LastWriteTimeUtc -eq $auditItemBefore.LastWriteTimeUtc) 'Prior audit changed while it was being verified.'

    return [pscustomobject][ordered]@{
        runId = $priorRunId
        directory = $priorDirectory
        summaryPath = $priorSummaryPath
        summarySha256 = $summaryHash
        summaryLength = $summaryItemAfter.Length
        auditPath = $priorAuditPath
        auditSha256 = $auditHash
        auditLength = $auditItemAfter.Length
        disconnectCompletedUtc = [string]$disconnectResponses[0].timestampUtc
        priorNinaPid = [int]$priorNina.pid
        priorNinaStartTimeUtc = [string]$priorNina.startTimeUtc
        inheritedMutationCounts = [ordered]@{ disconnect = 1; connect = 0; cool = 0 }
    }
}

function Assert-PriorRunStillImmutable {
    param([Parameter(Mandatory)]$PriorRun)

    Assert-True ((Get-FileHash -LiteralPath $PriorRun.summaryPath -Algorithm SHA256).Hash -ceq $PriorRun.summarySha256) 'Prior summary changed after continuation validation.'
    Assert-True ((Get-FileHash -LiteralPath $PriorRun.auditPath -Algorithm SHA256).Hash -ceq $PriorRun.auditSha256) 'Prior audit changed after continuation validation.'
}

function Assert-ExecutionAuthorization {
    Assert-ConfiguredExactIdentity
    if ($IsResume) {
        Assert-True $Execute 'Resume mode requires -Execute.'
    }
    if (-not $Execute) { return }
    Assert-True $ConfirmFocusScanComplete 'Execution refused: -ConfirmFocusScanComplete was not supplied.'
    Assert-True ($AuthorizationPhrase -ceq $RequiredAuthorizationPhrase) "Execution refused: authorization phrase must exactly equal '$RequiredAuthorizationPhrase'."
    Assert-True ($ApiBaseUri.AbsoluteUri.TrimEnd('/') -ceq $AuthorizedApiBaseUri) "Execution refused: Advanced API must be the audited loopback endpoint '$AuthorizedApiBaseUri'."
    if ($IsResume) {
        Assert-Sha256Text -Value $ExpectedPriorSummarySha256 -Name 'ExpectedPriorSummarySha256'
        Assert-Sha256Text -Value $ExpectedPriorAuditSha256 -Name 'ExpectedPriorAuditSha256'
    }
    Write-AuditEvent -Event 'execution-authorization' -Outcome 'pass' -Data ([ordered]@{
        execute = $true
        focusScanCompleteConfirmed = $true
        exactPhraseMatched = $true
    })
}

function Assert-ResumeDisconnectedState {
    param([Parameter(Mandatory)][string]$Label)

    $sequence = Get-SequenceStatusEvidence
    Assert-True (@($sequence.activeStatuses).Count -eq 0) "N.I.N.A. advanced sequence is active during disconnected continuation ($(@($sequence.activeStatuses) -join ', '))."
    $camera = Get-ExactCameraInfo -RequireConnected:$false -RequireExactIdentity:$false
    Assert-True (-not [bool](Get-RequiredProperty $camera 'Connected')) 'Resume mode requires the ATR camera to remain disconnected in N.I.N.A.'
    Assert-True (-not [bool](Get-RequiredProperty $camera 'IsExposing')) 'Disconnected N.I.N.A. camera state unexpectedly reports an exposure.'
    Assert-True (-not [bool](Get-RequiredProperty $camera 'LiveViewEnabled')) 'Disconnected N.I.N.A. camera state unexpectedly reports Live View.'
    $reportedId = [string](Get-OptionalProperty -InputObject $camera -Name 'DeviceId' -DefaultValue '')
    Assert-True ([string]::IsNullOrWhiteSpace($reportedId) -or $reportedId -ceq $ExpectedCameraId) "Disconnected camera info reported a different DeviceId '$reportedId'."
    Write-AuditEvent -Event 'resume.disconnected-camera-gate' -Outcome 'pass' -Data ([ordered]@{
        label = $Label
        connected = $false
        deviceIdPresent = -not [string]::IsNullOrWhiteSpace($reportedId)
        deviceId = $reportedId
        isExposing = [bool]$camera.IsExposing
        liveViewEnabled = [bool]$camera.LiveViewEnabled
        cameraState = [string]$camera.CameraState
        sequenceInitialized = $sequence.initialized
        sequenceStatuses = @($sequence.statuses)
    })
    return $camera
}

$finalTelemetry = $null
$scienceAllowed = $false
try {
    Write-AuditEvent -Event 'run.started' -Outcome 'info' -Data ([ordered]@{
        mode = $RunMode
        apiBaseUri = $ApiBaseUri.AbsoluteUri
        exactProfileId = $ExpectedProfileId
        exactCameraId = $ExpectedCameraId
        targetTemperatureC = $TargetTemperatureC
        finalToleranceC = $FinalToleranceC
        routeSemantics = [ordered]@{
            source = 'Installed Advanced API 2.2.15.2 matched to local source checkout.'
            profile = 'GET /profile/show?active=true returns ProfileService.ActiveProfile.'
            connect = 'GET /equipment/camera/connect?to=... uses chooser.Devices.First(x => x.Id == to) and awaits N.I.N.A. Connect.'
            disconnect = 'GET /equipment/camera/disconnect awaits N.I.N.A. Disconnect.'
            cool = 'GET /equipment/camera/cool starts cam.CoolCamera asynchronously and returns immediately.'
            atTargetTemp = 'API computes Temperature == TemperatureSetPoint; it is deliberately excluded from acceptance.'
        }
    })

    $script:LastPhase = 'baseline-integrity'
    $verifyScript = Join-Path $PSScriptRoot 'verify-design-baseline.ps1'
    & $verifyScript -Quiet
    Write-AuditEvent -Event 'frozen-design-baseline' -Outcome 'pass' -Data ([ordered]@{ output = 'Frozen design hash manifest verified.' })

    $script:LastPhase = 'preflight'
    Assert-ExecutionAuthorization
    $priorRun = $null
    if ($IsResume) {
        $script:LastPhase = 'resume-prior-evidence'
        $priorRun = Assert-PriorDisconnectedRun
        $script:MutationCounts.disconnect = 1
        $script:MutationCounts.connect = 0
        $script:MutationCounts.cool = 0
        $script:ContinuationEvidence = [ordered]@{
            priorRunId = $priorRun.runId
            priorSummaryPath = $priorRun.summaryPath
            priorSummarySha256 = $priorRun.summarySha256
            priorAuditPath = $priorRun.auditPath
            priorAuditSha256 = $priorRun.auditSha256
            inheritedMutationCounts = $priorRun.inheritedMutationCounts
            resumedWithoutSecondDisconnect = $true
        }
        Write-AuditEvent -Event 'continuation.prior-run-verified' -Outcome 'pass' -Data $script:ContinuationEvidence

        $script:LastPhase = 'resume-disconnected-preflight'
        $software = Assert-SoftwareAndOwnerIdentity
        Assert-True ([int]$software.Nina.Id -eq [int]$priorRun.priorNinaPid) 'N.I.N.A. process ID changed after the interrupted disconnect; exact-session continuation is refused.'
        Assert-True ($software.Nina.StartTime.ToUniversalTime().ToString('o') -ceq [string]$priorRun.priorNinaStartTimeUtc) 'N.I.N.A. process start time changed after the interrupted disconnect; exact-session continuation is refused.'
        $profile = Assert-ActiveProfile
        Assert-CameraIsSelectable
        for ($resumeGateIndex = 1; $resumeGateIndex -le 3; $resumeGateIndex++) {
            $null = Assert-ResumeDisconnectedState -Label "resume-pre-connect-$resumeGateIndex"
            if ($resumeGateIndex -lt 3) { Start-Sleep -Seconds 1 }
        }
        $profile = Assert-ActiveProfile
        Assert-CameraIsSelectable
        Assert-PriorRunStillImmutable -PriorRun $priorRun
        Save-NinaLogSnapshot -Label 'resume-before'
    }
    else {
        $software = Assert-SoftwareAndOwnerIdentity
        $profile = Assert-ActiveProfile
        Assert-CameraIsSelectable
        $preflightSamples = Get-TelemetrySeries -Label 'preflight' -EnforceIdle
        $preflightZero = Test-PersistentZeroTelemetry -Samples $preflightSamples
        Write-AuditEvent -Event 'preflight.telemetry-assessment' -Outcome $(if ($preflightZero) { 'warning' } else { 'pass' }) -Data ([ordered]@{
            persistentZeroTemperatureAndPower = $preflightZero
            note = $(if ($preflightZero) { 'Known 0 C / 0% stuck-session signature. Execution may attempt the single bounded reconnect; science is not accepted from these values.' } else { 'Telemetry is finite and is not the persistent 0 C / 0% signature.' })
        })
        Save-NinaLogSnapshot -Label 'before'

        if (-not $Execute) {
            $script:LastPhase = 'read-only-complete'
            Write-AuditEvent -Event 'run.completed' -Outcome 'pass' -Data ([ordered]@{
                result = 'Read-only preflight completed. No camera disconnect, reconnect, cooling request, exposure, or other hardware action was issued.'
                recoveryEligible = $true
                mutationCounts = $script:MutationCounts
                persistentZeroTemperatureAndPower = $preflightZero
            })
            Write-FinalSummary -Completed $true -ScienceAllowed $false -Result 'READ_ONLY_PREFLIGHT_COMPLETE'
            Write-Host 'ATR585M read-only recovery preflight completed.' -ForegroundColor Green
            Write-Host 'No hardware mutation was issued. Science remains unapproved until an executing recovery passes.' -ForegroundColor Yellow
            Write-Host "Audit: $AuditPath"
            Write-Host "Summary: $SummaryPath"
            return
        }

        $script:LastPhase = 'final-pre-mutation-gates'
        $software = Assert-SoftwareAndOwnerIdentity
        $profile = Assert-ActiveProfile
        Assert-CameraIsSelectable
        for ($idleIndex = 1; $idleIndex -le 3; $idleIndex++) {
            $null = Assert-NinaIdle -Label "immediate-pre-disconnect-$idleIndex"
            if ($idleIndex -lt 3) { Start-Sleep -Seconds 1 }
        }
        $profile = Assert-ActiveProfile

        $script:LastPhase = 'single-disconnect'
        $disconnectResult = Invoke-AdvancedApiRaw -RelativePath 'equipment/camera/disconnect' -Purpose 'single exact-owner ATR585M disconnect' -MutationKind 'disconnect'
        $null = Assert-ApiSuccess -ApiResult $disconnectResult -Purpose 'single exact-owner ATR585M disconnect'
        $null = Wait-ForConnectionState -Connected:$false -TimeoutSeconds 30
    }

    $script:LastPhase = 'single-reconnect'
    $software = Assert-SoftwareAndOwnerIdentity
    $profile = Assert-ActiveProfile
    Assert-CameraIsSelectable
    $preConnectSequence = Get-SequenceStatusEvidence
    Assert-True (@($preConnectSequence.activeStatuses).Count -eq 0) "N.I.N.A. advanced sequence became active while the camera was disconnected ($(@($preConnectSequence.activeStatuses) -join ', '))."
    Write-AuditEvent -Event 'nina.pre-connect-sequence-gate' -Outcome 'pass' -Data ([ordered]@{
        sequenceInitialized = $preConnectSequence.initialized
        sequenceStatuses = @($preConnectSequence.statuses)
        activeStatuses = @($preConnectSequence.activeStatuses)
    })
    if ($IsResume) {
        $null = Assert-ResumeDisconnectedState -Label 'resume-immediate-pre-connect'
        Assert-PriorRunStillImmutable -PriorRun $priorRun
        Assert-True ($script:MutationCounts.disconnect -eq 1 -and $script:MutationCounts.connect -eq 0 -and $script:MutationCounts.cool -eq 0) 'Continuation inherited mutation counts changed before reconnect.'
    }
    $encodedCameraId = [Uri]::EscapeDataString($ExpectedCameraId)
    $connectResult = Invoke-AdvancedApiRaw -RelativePath ("equipment/camera/connect?to=$encodedCameraId") -Purpose 'single exact-DeviceId ATR585M reconnect' -MutationKind 'connect'
    $null = Assert-ApiSuccess -ApiResult $connectResult -Purpose 'single exact-DeviceId ATR585M reconnect'
    $null = Wait-ForConnectionState -Connected:$true -TimeoutSeconds 30

    $script:LastPhase = 'post-reconnect-validation'
    $software = Assert-SoftwareAndOwnerIdentity
    $profile = Assert-ActiveProfile
    $postReconnectSamples = Get-TelemetrySeries -Label 'post-reconnect' -EnforceIdle
    $postReconnectZero = Test-PersistentZeroTelemetry -Samples $postReconnectSamples
    Write-AuditEvent -Event 'post-reconnect.telemetry-assessment' -Outcome $(if ($postReconnectZero) { 'warning' } else { 'pass' }) -Data ([ordered]@{
        persistentZeroTemperatureAndPower = $postReconnectZero
        nextBoundedAction = 'One direct -10 C request remains permitted. Persistent 0 C / 0% after that request blocks science.'
    })

    $script:LastPhase = 'single-direct-minus-ten'
    $software = Assert-SoftwareAndOwnerIdentity
    $profile = Assert-ActiveProfile
    for ($idleIndex = 1; $idleIndex -le 3; $idleIndex++) {
        $null = Assert-NinaIdle -Label "immediate-pre-cool-$idleIndex"
        if ($idleIndex -lt 3) { Start-Sleep -Seconds 1 }
    }
    $profile = Assert-ActiveProfile
    $coolResult = Invoke-AdvancedApiRaw -RelativePath 'equipment/camera/cool?temperature=-10&cancel=false&minutes=0' -Purpose 'single direct -10 C cooling request' -MutationKind 'cool'
    $coolResponse = Assert-ApiSuccess -ApiResult $coolResult -Purpose 'single direct -10 C cooling request'
    Assert-True ([string]$coolResponse -match 'Cooling started') "Advanced API did not acknowledge the single cooling request as started ('$coolResponse')."

    $script:LastPhase = 'cooling-validation'
    $coolingSamples = New-Object System.Collections.Generic.List[object]
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($CoolingTimeoutMinutes)
    $powerObserved = $false
    $trendObserved = $false
    $targetSetPointSamples = 0
    $consecutiveFinalSamples = 0
    $baselineTemperatures = @($postReconnectSamples | Where-Object { [math]::Abs([double]$_.temperatureC) -gt 0.000001 } | ForEach-Object { [double]$_.temperatureC })
    $baselineTemperature = $null
    if ($baselineTemperatures.Count -gt 0) {
        $baselineTemperature = ($baselineTemperatures | Measure-Object -Average).Average
    }
    $initiallyAtTarget = ($postReconnectSamples.Count -eq $RequiredTelemetrySamples -and
        @($postReconnectSamples | Where-Object { [math]::Abs([double]$_.temperatureC - $TargetTemperatureC) -le $FinalToleranceC }).Count -eq $RequiredTelemetrySamples -and
        -not $postReconnectZero)

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $info = Assert-NinaIdle -Label 'cooling-validation'
        $sample = ConvertTo-TelemetrySample -CameraInfo $info -Label 'cooling-validation'
        $coolingSamples.Add($sample)
        Write-AuditEvent -Event 'camera.telemetry' -Outcome 'info' -Data $sample

        if ([math]::Abs([double]$sample.temperatureSetPointC - $TargetTemperatureC) -le $SetPointToleranceC) {
            $targetSetPointSamples++
        }
        if ([bool]$sample.coolerOn -and [double]$sample.coolerPowerPercent -ge $MinimumPowerEvidencePercent) {
            $powerObserved = $true
        }
        if ($null -eq $baselineTemperature -and [math]::Abs([double]$sample.temperatureC) -gt 0.000001) {
            $baselineTemperature = [double]$sample.temperatureC
        }
        if ($null -ne $baselineTemperature -and ([double]$baselineTemperature - [double]$sample.temperatureC) -ge $MinimumCoolingProgressC) {
            $trendObserved = $true
        }

        if ([math]::Abs([double]$sample.temperatureSetPointC - $TargetTemperatureC) -le $SetPointToleranceC -and
            [math]::Abs([double]$sample.temperatureC - $TargetTemperatureC) -le $FinalToleranceC) {
            $consecutiveFinalSamples++
        }
        else {
            $consecutiveFinalSamples = 0
        }

        $coolingSampleArray = @($coolingSamples | ForEach-Object { $_ })
        if ($coolingSamples.Count -ge $RequiredTelemetrySamples -and (Test-PersistentZeroTelemetry -Samples $coolingSampleArray)) {
            throw 'Persistent Temperature=0 C and CoolerPower=0% remained after the one allowed direct -10 C request. Science acquisition is blocked regardless of cached TargetTemp, real setpoint, or AtTargetTemp.'
        }

        $responseEvidenceSatisfied = $initiallyAtTarget -or ($powerObserved -and $trendObserved)
        if ($targetSetPointSamples -ge 2 -and $consecutiveFinalSamples -ge $RequiredFinalSamples -and $responseEvidenceSatisfied) {
            $finalTelemetry = $sample
            break
        }

        Start-Sleep -Seconds $CoolingPollSeconds
    }

    if ($null -eq $finalTelemetry) {
        $last = $(if ($coolingSamples.Count -gt 0) { $coolingSamples[$coolingSamples.Count - 1] } else { $null })
        throw "ATR585M did not satisfy bounded cooling acceptance within $CoolingTimeoutMinutes minutes. setpointSamples=$targetSetPointSamples, powerObserved=$powerObserved, trendObserved=$trendObserved, finalStableSamples=$consecutiveFinalSamples, last=$($last | ConvertTo-Json -Compress)."
    }

    Assert-True ([math]::Abs([double]$finalTelemetry.temperatureSetPointC - $TargetTemperatureC) -le $SetPointToleranceC) 'Final real TemperatureSetPoint is not -10 C.'
    Assert-True ([math]::Abs([double]$finalTelemetry.temperatureC - $TargetTemperatureC) -le $FinalToleranceC) 'Final sensor temperature is outside +/-0.5 C.'
    Assert-True ($script:MutationCounts.disconnect -eq 1 -and $script:MutationCounts.connect -eq 1 -and $script:MutationCounts.cool -eq 1) 'The exact mutation-count invariant was not met.'

    $script:LastPhase = 'completed'
    Save-NinaLogSnapshot -Label 'after'
    Write-AuditEvent -Event 'cooling.acceptance' -Outcome 'pass' -Data ([ordered]@{
        realSetPointConfirmed = $true
        finalTemperatureWithinHalfDegree = $true
        coolerPowerObserved = $powerObserved
        coolingTrendObserved = $trendObserved
        initiallyAtTarget = $initiallyAtTarget
        consecutiveFinalSamples = $consecutiveFinalSamples
        atTargetTempWasUsedForAcceptance = $false
        mutationCounts = $script:MutationCounts
        finalTelemetry = $finalTelemetry
    })
    $scienceAllowed = $true
    Write-FinalSummary -Completed $true -ScienceAllowed $true -Result 'ATR585M_COOLING_RECOVERED' -FinalTelemetry $finalTelemetry
    Write-Host 'ATR585M exact-owner cooling recovery passed.' -ForegroundColor Green
    Write-Host 'Real setpoint and temperature acceptance passed; AtTargetTemp was not trusted.' -ForegroundColor Green
    Write-Host "Audit: $AuditPath"
    Write-Host "Summary: $SummaryPath"
}
catch {
    $failure = $_.Exception.Message
    $script:LastPhase = 'failed'
    try { Save-NinaLogSnapshot -Label 'failure' } catch { }
    $failureState = $null
    try {
        $failureInfo = Get-ExactCameraInfo -RequireConnected:$false -RequireExactIdentity:$false
        $failureState = [ordered]@{
            sampledUtc = [DateTimeOffset]::UtcNow.ToString('o')
            connected = [bool](Get-RequiredProperty $failureInfo 'Connected')
            deviceId = [string](Get-OptionalProperty -InputObject $failureInfo -Name 'DeviceId' -DefaultValue '')
            deviceIdWasPresent = ($null -ne $failureInfo.PSObject.Properties['DeviceId'])
            cameraState = [string](Get-RequiredProperty $failureInfo 'CameraState')
            isExposing = [bool](Get-RequiredProperty $failureInfo 'IsExposing')
            temperatureC = [string](Get-RequiredProperty $failureInfo 'Temperature')
            temperatureSetPointC = [string](Get-RequiredProperty $failureInfo 'TemperatureSetPoint')
            coolerOn = [string](Get-RequiredProperty $failureInfo 'CoolerOn')
            coolerPowerPercent = [string](Get-RequiredProperty $failureInfo 'CoolerPower')
            atTargetTempApiFlagIgnored = [string](Get-RequiredProperty $failureInfo 'AtTargetTemp')
        }
        Write-AuditEvent -Event 'failure.read-only-camera-state' -Outcome 'info' -Data $failureState
    }
    catch {
        $failureState = [ordered]@{
            sampledUtc = [DateTimeOffset]::UtcNow.ToString('o')
            available = $false
            error = $_.Exception.Message
        }
    }
    try {
        Write-AuditEvent -Event 'run.failed' -Outcome 'fail' -Data ([ordered]@{
            failure = $failure
            scienceAllowed = $false
            noAutomaticMutationRetry = $true
            failureCleanup = [ordered]@{
                disconnectOrReconnectRetryIssued = $false
                coolingCancelIssued = $false
                warmingIssued = $false
                directSdkOrUsbActionIssued = $false
                rationale = 'Fail closed and preserve the last N.I.N.A.-owned state for diagnosis. The N.I.N.A. 3.2 cancellation path can rewrite the setpoint from invalid telemetry, so this recovery never auto-cancels on failure.'
            }
            mutationCounts = $script:MutationCounts
            finalTelemetry = $finalTelemetry
            failureState = $failureState
        })
        Write-FinalSummary -Completed $false -ScienceAllowed $false -Result 'SCIENCE_BLOCKED' -Failure $failure -FinalTelemetry $finalTelemetry -FailureState $failureState
    }
    catch {
        Write-Warning "Could not finish audit summary: $($_.Exception.Message)"
    }
    [Console]::Error.WriteLine("ATR585M recovery failed; science remains blocked. $failure")
    [Console]::Error.WriteLine("Audit: $AuditPath")
    [Console]::Error.WriteLine("Summary: $SummaryPath")
    exit 1
}
