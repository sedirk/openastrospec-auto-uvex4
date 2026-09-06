[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedTelescopeId,
    [switch]$OperatorAuthorizedHoming,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $OperatorAuthorizedHoming) { throw 'Explicit operator homing authorization is required.' }
$bridge = Join-Path $PSScriptRoot 'invoke-observation-automation-bridge.ps1'
$snapshot = (& $bridge -Snapshot -ConnectTimeoutMilliseconds 30000).snapshot
if ($snapshot.runState -notin @('Idle','Cancelled','Completed')) {
    throw 'Finish the visible observation cancellation before homing.'
}
function Read-PhdState {
    $tcp = [Net.Sockets.TcpClient]::new()
    try {
        if (-not $tcp.ConnectAsync('127.0.0.1',4400).Wait(5000)) { throw 'PHD2 connection timed out.' }
        $stream = $tcp.GetStream(); $stream.ReadTimeout = 5000; $stream.WriteTimeout = 5000
        $reader = [IO.StreamReader]::new($stream)
        $writer = [IO.StreamWriter]::new($stream); $writer.AutoFlush = $true
        $writer.WriteLine('{"jsonrpc":"2.0","id":9177,"method":"get_app_state"}')
        do {
            $line = $reader.ReadLine()
            if ($null -eq $line) { throw 'PHD2 closed its read-only status connection.' }
            $response = $line | ConvertFrom-Json
        } while ($null -eq $response.PSObject.Properties['id'] -or $response.id -ne 9177)
        if ($null -eq $response.PSObject.Properties['result']) { throw 'PHD2 did not return an app state.' }
        return [string]$response.result
    } finally { $tcp.Dispose() }
}
function Read-Mount {
    $response = Invoke-RestMethod 'http://127.0.0.1:1888/v2/api/equipment/mount/info' -TimeoutSec 5
    $mount = $response.Response
    if (-not $mount.Connected -or $mount.DeviceId -ne $ExpectedTelescopeId) {
        throw 'Connected NINA mount does not match the explicitly selected telescope identity.'
    }
    return $mount
}
$mount = Read-Mount
$camera = (Invoke-RestMethod 'http://127.0.0.1:1888/v2/api/equipment/camera/info' -TimeoutSec 5).Response
$phd = Read-PhdState
if ($phd -notin @('Stopped','Selected') -or $camera.IsExposing -or
    $mount.Slewing -or $mount.IsPulseGuiding -or $mount.AtPark -or -not $mount.CanFindHome) {
    throw 'Owners are not idle, or the mount is parked/cannot home; no homing command was sent.'
}
$directory = Join-Path $env:LOCALAPPDATA ('UVEX-ADV\maintenance\nina-home-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $directory | Out-Null
function Save-Audit([string]$Name, [object]$Data) {
    [IO.File]::WriteAllText((Join-Path $directory $Name), ($Data | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}
Save-Audit 'intent.json' ([ordered]@{
    timestampUtc = [DateTimeOffset]::UtcNow
    owner = 'NINA telescope mediator'; operatorAuthorized = $true
    previousObservationRunId = $snapshot.observationRunId
    previousRunState = $snapshot.runState; pluginVersion = $snapshot.pluginVersion
    telescopeId = $ExpectedTelescopeId; mountBefore = $mount; phdStateBefore = $phd
    roofCommandAllowed = $false; oldLedgerModified = $false
})
if (-not $mount.AtHome) {
    $result = Invoke-RestMethod 'http://127.0.0.1:1888/v2/api/equipment/mount/home' -TimeoutSec 10
    Save-Audit 'nina-home-response.json' $result
    if (-not $result.Success) { throw 'NINA rejected the homing request; inspect its recorded response.' }
}
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$confirmations = [Collections.Generic.List[object]]::new()
$trackingStopSent = $false
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    $mount = Read-Mount
    if ($mount.AtHome -and -not $mount.Slewing -and -not $mount.IsPulseGuiding) {
        if ($mount.TrackingEnabled -and -not $trackingStopSent) {
            $trackingStopSent = $true
            $result = Invoke-RestMethod 'http://127.0.0.1:1888/v2/api/equipment/mount/tracking?mode=4' -TimeoutSec 10
            Save-Audit 'nina-tracking-stop-response.json' $result
            if (-not $result.Success) { throw 'NINA did not confirm the tracking-stop request.' }
        } elseif (-not $mount.TrackingEnabled) {
            $phd = Read-PhdState
            if ($phd -notin @('Stopped','Selected')) { throw 'PHD2 changed state during home verification.' }
            $confirmations.Add([ordered]@{ timestampUtc = [DateTimeOffset]::UtcNow; mount = $mount; phdState = $phd })
            if ($confirmations.Count -ge 2) {
                Save-Audit 'verified-home.json' ([ordered]@{ confirmed = $true; samples = $confirmations; oldLedgerModified = $false })
                [pscustomobject]@{ Confirmed = $true; AuditDirectory = $directory; AtHome = $mount.AtHome; Tracking = $mount.TrackingEnabled; Slewing = $mount.Slewing }
                return
            }
        }
    } else { $confirmations.Clear() }
    Start-Sleep -Seconds 2
}
Save-Audit 'unconfirmed-home.json' ([ordered]@{ timestampUtc = [DateTimeOffset]::UtcNow; mount = $mount; confirmed = $false })
throw 'Homing was not confirmed within the bounded wait; no automatic second homing command was sent.'
