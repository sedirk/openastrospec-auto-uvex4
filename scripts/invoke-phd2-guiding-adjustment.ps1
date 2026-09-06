[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ExpectedProfileId,
    [Parameter(Mandatory)][string]$ExpectedProfileName,
    [hashtable[]]$Changes = @(),
    [switch]$ClearMountCalibration,
    [switch]$OperatorAuthorizedHardwareChange
)

# Owner-native, idle-only maintenance. This does not guide, expose, slew, or
# execute an observation. Any subsequent calibration belongs to the UI runner.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (($Changes.Count -gt 0 -or $ClearMountCalibration) -and -not $OperatorAuthorizedHardwareChange) {
    throw 'Explicit operator authorization is required for PHD2 configuration changes.'
}
$ui = & (Join-Path $PSScriptRoot 'invoke-observation-automation-bridge.ps1') -Snapshot -ConnectTimeoutMilliseconds 30000
if (-not $ui.ok -or $ui.snapshot.runState -notin @('Idle', 'Cancelled', 'Completed')) {
    throw 'Finish the visible observation boundary before changing PHD2 settings.'
}
$tcp = [Net.Sockets.TcpClient]::new('127.0.0.1', 4400)
$tcp.ReceiveTimeout = 5000
$tcp.SendTimeout = 5000
$reader = [IO.StreamReader]::new($tcp.GetStream())
$writer = [IO.StreamWriter]::new($tcp.GetStream())
$writer.NewLine = "`r`n"
$writer.AutoFlush = $true
$script:rpcId = 0
function Invoke-PhdRpc([string]$Method, [object[]]$Arguments = @()) {
    $script:rpcId++
    $request = @{ jsonrpc = '2.0'; id = $script:rpcId; method = $Method }
    if ($Arguments.Count -gt 0) { $request.params = $Arguments }
    $writer.WriteLine(($request | ConvertTo-Json -Depth 8 -Compress))
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "PHD2 $Method response deadline expired." }
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw 'PHD2 closed the event connection.' }
        $response = $line | ConvertFrom-Json
        $id = $response.PSObject.Properties['id']
    } while ($null -eq $id -or $id.Value -ne $script:rpcId)
    if ($null -ne $response.PSObject.Properties['error']) {
        throw "PHD2 $Method failed: $($response.error | ConvertTo-Json -Compress)"
    }
    $response.result
}
function Assert-IdleIdentity {
    $profile = Invoke-PhdRpc 'get_profile'
    if ($profile.id -ne $ExpectedProfileId -or $profile.name -cne $ExpectedProfileName) {
        throw 'The active PHD2 profile changed or does not match the authorized profile.'
    }
    if ((Invoke-PhdRpc 'get_app_state') -notin @('Stopped', 'Selected')) {
        throw 'PHD2 must be idle; this tool never stops an active capture or guiding session.'
    }
}
try {
    Assert-IdleIdentity
    $parameters = @()
    foreach ($axis in @('ra', 'dec')) {
        foreach ($name in @(Invoke-PhdRpc 'get_algo_param_names' @($axis))) {
            $parameters += [ordered]@{ axis = $axis; name = $name; value = (Invoke-PhdRpc 'get_algo_param' @($axis, $name)) }
        }
    }
    # Do not expose arbitrary parameter writes or silently switch algorithms.
    foreach ($change in $Changes) {
        if ($change.axis -notin @('ra', 'dec') -or $change.name -notin @('reactiveWeight', 'predictiveWeight', 'aggression', 'minMove', 'fastSwitch')) {
            throw 'Unsupported adjustment; algorithm and equipment selection are never changed.'
        }
        if (@($parameters | Where-Object { $_.axis -eq $change.axis -and $_.name -ceq $change.name }).Count -ne 1) {
            throw 'The requested parameter is not exposed by the active native algorithm.'
        }
        $value = [double]$change.value
        if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0 -or $value -gt 1) {
            throw 'This conservative maintenance tool accepts parameter values only in [0, 1].'
        }
    }
    $root = Join-Path $env:LOCALAPPDATA ('UVEX-ADV\maintenance\phd2-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $root | Out-Null
    $backup = [ordered]@{
        capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        profile = (Invoke-PhdRpc 'get_profile')
        equipment = (Invoke-PhdRpc 'get_current_equipment')
        calibration = (Invoke-PhdRpc 'get_calibration_data' @('Mount'))
        exposure = (Invoke-PhdRpc 'get_exposure')
        parameters = $parameters
        previousObservationRunId = $ui.snapshot.observationRunId
        requestedChanges = $Changes
        clearMountCalibrationRequested = [bool]$ClearMountCalibration
        explicitOperatorAuthorization = [bool]$OperatorAuthorizedHardwareChange
    }
    $registryPath = Join-Path $root 'phd2-settings-before.reg'
    & reg.exe export 'HKCU\Software\StarkLabs\PHDGuidingV2' $registryPath /y | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $registryPath)) { throw 'PHD2 registry backup failed; no settings changed.' }
    $backup.registryBackupSha256 = (Get-FileHash -LiteralPath $registryPath -Algorithm SHA256).Hash
    $backup | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'before.json') -Encoding UTF8
    $events = @()
    try {
        foreach ($change in $Changes) {
            Assert-IdleIdentity
            $old = Invoke-PhdRpc 'get_algo_param' @($change.axis, $change.name)
            $result = Invoke-PhdRpc 'set_algo_param' @($change.axis, $change.name, [double]$change.value)
            $actual = Invoke-PhdRpc 'get_algo_param' @($change.axis, $change.name)
            $events += [ordered]@{ utc = [DateTimeOffset]::UtcNow.ToString('O'); axis = $change.axis; name = $change.name; before = $old; requested = $change.value; actual = $actual; nativeResult = $result }
            if ([Math]::Abs([double]$actual - [double]$change.value) -gt 0.000001) { throw 'PHD2 parameter readback did not match the requested value.' }
        }
        if ($ClearMountCalibration) {
            Assert-IdleIdentity
            $result = Invoke-PhdRpc 'clear_calibration' @('mount')
            $actual = Invoke-PhdRpc 'get_calibration_data' @('Mount')
            $events += [ordered]@{ utc = [DateTimeOffset]::UtcNow.ToString('O'); operation = 'clear_calibration'; nativeResult = $result; readback = $actual; nextCalibrationOwner = 'Visible NINA production runner' }
            if ($actual.calibrated) { throw 'Native mount calibration is still active after the clear request.' }
        }
    } finally {
        $events | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'adjustments.json') -Encoding UTF8
    }
    [pscustomobject]@{ backupDirectory = $root; before = $parameters; adjustments = $events; state = (Invoke-PhdRpc 'get_app_state') }
} finally {
    $writer.Dispose()
    $reader.Dispose()
    $tcp.Dispose()
}
