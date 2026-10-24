#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [switch]$EnableHardware,
    [switch]$PreflightOnly,
    [string]$HardwareConfigurationPath,
    [string]$VendorSdkDirectory = 'C:\Program Files\QHYCCD\AllInOne\sdk\x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $root 'artifacts\qhy-service'
}

$serviceName = 'UVEX-ADV-QHY'
$executableName = 'UvexAdv.Qhy.Service.exe'
$artifactExecutable = Join-Path $ArtifactDirectory $executableName
if (-not (Test-Path -LiteralPath $artifactExecutable)) {
    throw 'Build the QHY service Release artifacts first with scripts\build.ps1.'
}
if (-not (Test-Path -LiteralPath (Join-Path $ArtifactDirectory 'UvexAdv.Qhy.Core.dll') -PathType Leaf)) {
    throw 'The QHY service artifact is incomplete or stale (missing UvexAdv.Qhy.Core.dll). Run scripts\build.ps1 first.'
}

function Invoke-ServiceControl {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($Arguments[0]) failed with exit code $LASTEXITCODE`n$($output -join [Environment]::NewLine)"
    }

    $output
}

function Wait-QhyOwnerExit {
    param([Parameter(Mandatory)]$Process, [ValidateRange(1, 30000)][int]$TimeoutMilliseconds = 30000)

    # SCM Stopped can precede native/runtime DLL unloading. Never kill an owner.
    if (-not $Process.WaitForExit($TimeoutMilliseconds)) {
        throw 'QHY service process has not exited; installed files and registration are preserved.'
    }
}

function Switch-QhyBinaryDirectory {
    param([string]$InstallRoot, [string]$InstallDirectory, [string]$StagingDirectory, [string]$BackupDirectory)

    $rootPath = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
    $paths = @($InstallDirectory, $StagingDirectory, $BackupDirectory) | ForEach-Object { [IO.Path]::GetFullPath($_) }
    if (@($paths | Select-Object -Unique).Count -ne 3) { throw 'Deployment paths must be distinct.' }
    foreach ($path in $paths) {
        if (-not [IO.Path]::GetDirectoryName($path).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing deployment path outside the direct install root: $path"
        }
        if ((Test-Path -LiteralPath $path) -and
            ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing deployment through a reparse point: $path"
        }
    }
    if (Test-Path -LiteralPath $BackupDirectory) { throw 'Deployment backup already exists.' }
    if (-not (Test-Path -LiteralPath $StagingDirectory -PathType Container)) { throw 'Staged deployment is absent.' }

    # Rename the old tree intact, rather than deleting individual DLLs before commit.
    if (Test-Path -LiteralPath $InstallDirectory) {
        Move-Item -LiteralPath $InstallDirectory -Destination $BackupDirectory -ErrorAction Stop
    }
    try {
        Move-Item -LiteralPath $StagingDirectory -Destination $InstallDirectory -ErrorAction Stop
    }
    catch {
        if (-not (Test-Path -LiteralPath $InstallDirectory) -and (Test-Path -LiteralPath $BackupDirectory)) {
            Move-Item -LiteralPath $BackupDirectory -Destination $InstallDirectory -ErrorAction Stop
        }
        throw
    }
}

$installRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'UVEX-ADV'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $installRoot 'QhyService'))
if (-not $installDirectory.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace QHY service outside the UVEX-ADV install root: $installDirectory"
}
$dataDirectory = Join-Path $env:ProgramData 'UVEX-ADV\qhy'
$configurationPath = Join-Path $dataDirectory 'appsettings.json'
if ($EnableHardware) {
    if ([string]::IsNullOrWhiteSpace($HardwareConfigurationPath)) {
        throw '-EnableHardware requires -HardwareConfigurationPath pointing to an explicit machine-local JSON configuration. The tracked qhy.production.json is an intentionally non-runnable example.'
    }
    $sourceConfiguration = [IO.Path]::GetFullPath($HardwareConfigurationPath)
}
else {
    if (-not [string]::IsNullOrWhiteSpace($HardwareConfigurationPath)) {
        throw '-HardwareConfigurationPath is valid only with -EnableHardware.'
    }
    $sourceConfiguration = Join-Path $root 'config\qhy.simulator.json'
}
if (-not (Test-Path -LiteralPath $sourceConfiguration)) {
    throw "Missing QHY configuration '$sourceConfiguration'."
}

$selectedConfiguration = Get-Content -LiteralPath $sourceConfiguration -Encoding UTF8 -Raw | ConvertFrom-Json
if ($null -eq $selectedConfiguration.Qhy) {
    throw "QHY configuration '$sourceConfiguration' has no Qhy section."
}
$selectedStableId = [string]$selectedConfiguration.Qhy.ExpectedStableId
$selectedSdkSha256 = [string]$selectedConfiguration.Qhy.NativeSdkSha256
if ($EnableHardware) {
    if ([bool]$selectedConfiguration.Qhy.Simulator) {
        throw "Hardware configuration '$sourceConfiguration' still has Qhy:Simulator=true."
    }
    if ([string]::IsNullOrWhiteSpace($selectedStableId) -or
        $selectedStableId.IndexOf('<', [StringComparison]::Ordinal) -ge 0 -or
        $selectedStableId.IndexOf('>', [StringComparison]::Ordinal) -ge 0 -or
        $selectedStableId.IndexOf('placeholder', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Hardware configuration '$sourceConfiguration' contains a missing or placeholder Qhy:ExpectedStableId. Supply the exact commissioned machine-local identity."
    }
    if ($selectedSdkSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "Hardware configuration '$sourceConfiguration' must bind Qhy:NativeSdkSha256 to the commissioned 64-character vendor SDK hash."
    }
    $vendorSdkPath = [IO.Path]::GetFullPath((Join-Path $VendorSdkDirectory 'qhyccd.dll'))
    $configuredSdkPath = [IO.Path]::GetFullPath([string]$selectedConfiguration.Qhy.NativeSdkPath)
    if (-not $configuredSdkPath.Equals($vendorSdkPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Hardware configuration '$sourceConfiguration' must reference the shared official QHY AllInOne SDK '$vendorSdkPath', not a private service copy."
    }
    if (-not (Test-Path -LiteralPath $vendorSdkPath)) {
        throw "The pinned vendor SDK was not found at '$VendorSdkDirectory'."
    }
    $actualSha256 = (Get-FileHash -LiteralPath $vendorSdkPath -Algorithm SHA256).Hash
    if ($actualSha256 -ne $selectedSdkSha256) {
        throw "QHY SDK hash mismatch. Expected $selectedSdkSha256, received $actualSha256. Review and commission the complete official AllInOne installation before enabling hardware."
    }
}
elseif (-not [bool]$selectedConfiguration.Qhy.Simulator) {
    throw "Simulator installation configuration '$sourceConfiguration' unexpectedly enables hardware."
}
if ($PreflightOnly) {
    Write-Host "QHY service preflight passed for $(if ($EnableHardware) { 'hardware' } else { 'simulator' }) mode."
    return
}

# Prepare and verify the entire binary tree before stopping an existing owner.
$deploymentId = [Guid]::NewGuid().ToString('N')
$stagingDirectory = [IO.Path]::GetFullPath((Join-Path $installRoot ('.QhyService.installing-' + $deploymentId)))
$backupDirectory = [IO.Path]::GetFullPath((Join-Path $installRoot ('.QhyService.backup-' + $deploymentId)))
if (-not $stagingDirectory.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage the QHY service outside the UVEX-ADV install root: $stagingDirectory"
}
New-Item -ItemType Directory -Force $stagingDirectory | Out-Null
try {
    Copy-Item -Path (Join-Path $ArtifactDirectory '*') -Destination $stagingDirectory -Recurse -Force
    if ($EnableHardware) {
        $privateSdkPath = Join-Path $stagingDirectory 'native\qhyccd.dll'
        if (Test-Path -LiteralPath $privateSdkPath) {
            throw "QHY service artifacts must not bundle a private native\qhyccd.dll. Install the complete official AllInOne package and reference its shared x64 SDK."
        }
    }
    foreach ($requiredFile in @($executableName, 'UvexAdv.Qhy.Service.dll', 'UvexAdv.Qhy.Core.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $requiredFile) -PathType Leaf)) {
            throw "Staged QHY service deployment is incomplete: $requiredFile"
        }
    }

    $existing = Get-Service $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        $owner = $null
        try {
            $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
            if ($serviceInfo.ProcessId) {
                $owner = Get-Process -Id $serviceInfo.ProcessId -ErrorAction Stop
                if (-not ([IO.Path]::GetFullPath($owner.Path)).Equals(
                    (Join-Path $installDirectory $executableName), [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'QHY service process path does not match the installed owner.'
                }
            }
            if ($existing.Status -ne 'Stopped') {
                Stop-Service $serviceName -ErrorAction Stop
                $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            }
            if ($null -ne $owner) { Wait-QhyOwnerExit -Process $owner }
        }
        finally {
            if ($null -ne $owner) { $owner.Dispose() }
            $existing.Dispose()
        }
    }
    # Also covers an unregistered native owner left by an interrupted old installer.
    if (Get-Process -Name 'UvexAdv.Qhy.Service' -ErrorAction SilentlyContinue) {
        throw 'QHY owner is still running; refusing to replace its files.'
    }
    Switch-QhyBinaryDirectory -InstallRoot $installRoot -InstallDirectory $installDirectory `
        -StagingDirectory $stagingDirectory -BackupDirectory $backupDirectory
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
New-Item -ItemType Directory -Force $dataDirectory | Out-Null

# Installation mode is an explicit operator choice. Replace the machine configuration
# so that a stale simulator/real-hardware setting cannot silently defeat that choice.
# A maintenance update may deliberately edit the installed machine configuration in
# place and pass that same file back through this installer; do not copy a file onto
# itself in that case.
$resolvedConfigurationPath = [IO.Path]::GetFullPath($configurationPath)
if (-not $sourceConfiguration.Equals($resolvedConfigurationPath, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $sourceConfiguration -Destination $configurationPath -Force
}

$aclOutput = & icacls.exe $dataDirectory /grant '*S-1-5-18:(OI)(CI)F' /T 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Failed to grant LocalSystem access to $dataDirectory`n$($aclOutput -join [Environment]::NewLine)"
}

$executable = Join-Path $installDirectory $executableName
$quotedExecutable = '"' + $executable + '"'
Invoke-ServiceControl -Arguments @(
    $(if (Get-Service $serviceName -ErrorAction SilentlyContinue) { 'config' } else { 'create' }), $serviceName,
    'binPath=', $quotedExecutable,
    'start=', 'auto',
    'obj=', 'LocalSystem',
    'DisplayName=', 'OpenAstroSpec Auto — UVEX4 QHY Wide-field Service'
) | Out-Null
Invoke-ServiceControl -Arguments @('description', $serviceName, 'Single-owner QHYminiCam8M acquisition and photometry service; loopback API only.') | Out-Null
Invoke-ServiceControl -Arguments @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') | Out-Null
Start-Service $serviceName
$installed = Get-Service $serviceName
$installed.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

if (Test-Path -LiteralPath $backupDirectory) {
    Write-Host "Previous service files retained at: $backupDirectory"
}
Write-Host "Installed $serviceName in $(if ($EnableHardware) { 'hardware' } else { 'simulator' }) mode with automatic Windows startup."
Write-Host "Configuration: $configurationPath"
Write-Host 'Health endpoint: http://127.0.0.1:47845/api/v1/health'
