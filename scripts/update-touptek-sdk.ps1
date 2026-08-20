[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SdkRoot
)

$ErrorActionPreference = 'Stop'

$resolvedSdkRoot = (Resolve-Path -LiteralPath $SdkRoot).Path
$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-SdkRoot', ('"{0}"' -f $resolvedSdkRoot)
    )
    $process = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList $arguments `
        -Verb RunAs `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Elevated SDK update failed with exit code $($process.ExitCode)."
    }
    exit 0
}

$running = Get-Process -Name 'NINA', 'toupsky' -ErrorAction SilentlyContinue
if ($running) {
    $names = ($running | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
    throw "Close camera applications before replacing the SDK: $names"
}

# This updater is deliberately host-scoped. N.I.N.A. and ToupSky are separate
# native hosts and may require different ToupCam SDK generations. Never copy a
# N.I.N.A.-validated DLL into ToupSky; repair/update ToupSky with the matching
# official ToupSky installer so its executable and native DLL stay paired.
$targets = @(
    [pscustomobject]@{
        Host = 'N.I.N.A.'
        Source = Join-Path $resolvedSdkRoot 'x64\toupcam.dll'
        Target = "C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy\External\x64\ToupTek\toupcam.dll"
    }
)

foreach ($item in $targets) {
    if (-not (Test-Path -LiteralPath $item.Source -PathType Leaf)) {
        throw "SDK source DLL does not exist: $($item.Source)"
    }
    if (-not (Test-Path -LiteralPath $item.Target -PathType Leaf)) {
        throw "Installed target DLL does not exist: $($item.Target)"
    }
    Copy-Item -LiteralPath $item.Source -Destination $item.Target -Force
}

$results = foreach ($item in $targets) {
    $sourceHash = (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $item.Target -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) {
        throw "SDK verification failed for $($item.Target)."
    }
    $file = Get-Item -LiteralPath $item.Target
    [pscustomobject]@{
        Host = $item.Host
        Target = $file.FullName
        Version = $file.VersionInfo.FileVersion
        SHA256 = $targetHash
    }
}

$results | Format-List
