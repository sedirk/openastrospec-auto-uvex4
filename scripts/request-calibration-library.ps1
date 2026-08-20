[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$ConfirmDarkness,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CameraId,
    [string]$LibraryRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'UVEX-ADV Calibration Library'),
    [int]$Gain = 100,
    [int]$Offset = 256,
    [ValidateRange(1, 4)]
    [int]$Binning = 1,
    [double]$TemperatureC = -10,
    [ValidateRange(0.1, 10)]
    [double]$TemperatureToleranceC = 0.5,
    [ValidateRange(0.000001, 10)]
    [double]$BiasExposureSeconds = 0.000276,
    [ValidateRange(0, 20)]
    [int]$WarmupFrameCount = 2,
    [ValidateRange(0, 1000)]
    [int]$BiasFrameCount = 16,
    [double[]]$DarkExposureSeconds = @(300, 600),
    [ValidateRange(0, 1000)]
    [int]$DarkFrameCountEach = 1,
    [bool]$BuildMasters = $true,
    [ValidateRange(1, 24)]
    [int]$ExpiresInHours = 4
)

$ErrorActionPreference = 'Stop'
if (-not $ConfirmDarkness) {
    throw 'Refusing to queue a shutterless-camera calibration job without -ConfirmDarkness.'
}
if ([string]::IsNullOrWhiteSpace($CameraId) -or $CameraId -notmatch 'ATR|pid_157c') {
    throw 'CameraId must be the stable ATR585M identifier; the guide camera is never inferred by list order.'
}
if ($BiasFrameCount -eq 0 -and $DarkFrameCountEach -eq 0) {
    throw 'The request contains no frames.'
}
if ($DarkExposureSeconds | Where-Object { [double]::IsNaN($_) -or [double]::IsInfinity($_) -or $_ -le 0 }) {
    throw 'Every dark exposure must be a finite positive number of seconds.'
}

$jobDirectory = Join-Path $env:LOCALAPPDATA 'UVEX-ADV\calibration-jobs'
$pendingPath = Join-Path $jobDirectory 'pending.json'
$temporaryPath = Join-Path $jobDirectory 'pending.json.tmp'
New-Item -ItemType Directory -Force -Path $jobDirectory | Out-Null
if (Test-Path -LiteralPath $pendingPath) {
    throw "A pending calibration job already exists: $pendingPath"
}

$now = [DateTimeOffset]::UtcNow
$request = [ordered]@{
    schemaVersion = 1
    createdUtc = $now.ToString('o')
    expiresUtc = $now.AddHours($ExpiresInHours).ToString('o')
    darknessConfirmed = $true
    cameraId = $CameraId
    libraryRoot = [IO.Path]::GetFullPath($LibraryRoot)
    gain = $Gain
    offset = $Offset
    binning = $Binning
    temperatureC = $TemperatureC
    temperatureToleranceC = $TemperatureToleranceC
    biasExposureSeconds = $BiasExposureSeconds
    warmupFrameCount = $WarmupFrameCount
    biasFrameCount = $BiasFrameCount
    darkExposureSeconds = @($DarkExposureSeconds)
    darkFrameCountEach = $DarkFrameCountEach
    buildMasters = $BuildMasters
}

$request | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
Move-Item -LiteralPath $temporaryPath -Destination $pendingPath
Write-Host "Queued OpenAstroSpec Auto — UVEX4 calibration request: $pendingPath"
Write-Host "Camera: $CameraId"
Write-Host "Plan: $BiasFrameCount bias; $DarkFrameCountEach each at $($DarkExposureSeconds -join ', ') seconds; T=$TemperatureC C; gain=$Gain; offset=$Offset; bin=$($Binning)x$Binning"
Write-Host 'The UVEX calibration dock will claim this request only after that exact camera is connected and idle in N.I.N.A.'
