#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [string]$AdminArtifactDirectory,
    [switch]$EnableHardware,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $root 'artifacts\service'
}
if ([string]::IsNullOrWhiteSpace($AdminArtifactDirectory)) {
    $AdminArtifactDirectory = Join-Path $root 'artifacts\admin'
}
foreach ($requiredFile in @('UvexAdv.Service.exe', 'UvexAdv.Service.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ArtifactDirectory $requiredFile) -PathType Leaf)) {
        throw "The UVEX service artifact is incomplete or stale (missing $requiredFile). Run scripts\build.ps1 first."
    }
}
foreach ($requiredFile in @('UvexAdv.Admin.exe', 'UvexAdv.Admin.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $AdminArtifactDirectory $requiredFile) -PathType Leaf)) {
        throw "The manager artifact is incomplete or stale (missing $requiredFile). Run scripts\build.ps1 first."
    }
}

function Invoke-ServiceControl {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($Arguments[0]) failed with exit code $LASTEXITCODE`n$($output -join [Environment]::NewLine)"
    }

    $output
}

$serviceName = 'UVEX-ADV'
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'UVEX-ADV'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $installRoot 'Service'))
$adminInstallDirectory = [IO.Path]::GetFullPath((Join-Path $installRoot 'Admin'))
foreach ($directory in @($installDirectory, $adminInstallDirectory)) {
    if (-not $directory.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace UVEX-ADV binaries outside the install root: $directory"
    }
}

$dataDirectory = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'UVEX-ADV'))
$configName = if ($EnableHardware) { 'config.production.json' } else { 'config.simulator.json' }
$sourceConfig = Join-Path $root "config\$configName"
if (-not (Test-Path -LiteralPath $sourceConfig -PathType Leaf)) {
    throw "Missing UVEX service configuration template '$sourceConfig'."
}
$artifactConfigurationPath = Join-Path $ArtifactDirectory 'appsettings.json'
if (-not (Test-Path -LiteralPath $artifactConfigurationPath -PathType Leaf)) {
    throw "The UVEX service artifact is incomplete or stale (missing appsettings.json). Run scripts\build.ps1 first."
}
$artifactConfiguration = Get-Content -LiteralPath $artifactConfigurationPath -Raw | ConvertFrom-Json
$modeConfiguration = Get-Content -LiteralPath $sourceConfig -Raw | ConvertFrom-Json
$configurationPath = Join-Path $dataDirectory 'config.json'
$effectiveConfigurationPath = if (Test-Path -LiteralPath $configurationPath) { $configurationPath } else { $sourceConfig }
$machineConfiguration = Get-Content -LiteralPath $effectiveConfigurationPath -Raw | ConvertFrom-Json

function Get-EffectiveUvexSetting {
    param(
        [Parameter(Mandatory)][object]$MachineConfiguration,
        [Parameter(Mandatory)][object]$ArtifactConfiguration,
        [Parameter(Mandatory)][string]$Name
    )

    $machineProperty = $MachineConfiguration.Uvex.PSObject.Properties[$Name]
    if ($null -ne $machineProperty) {
        return $machineProperty.Value
    }

    $artifactProperty = $ArtifactConfiguration.Uvex.PSObject.Properties[$Name]
    if ($null -eq $artifactProperty) {
        throw "Neither machine configuration nor artifact appsettings.json defines Uvex:$Name."
    }

    $artifactProperty.Value
}

# ASP.NET configuration providers merge individual keys. A missing machine key
# therefore inherits the published appsettings value rather than the C# default.
# Reproduce that precedence in preflight so a legacy config cannot inherit an
# incompatible simulator calibration into real mode.
$effectiveSimulator = [bool](Get-EffectiveUvexSetting $machineConfiguration $artifactConfiguration 'Simulator')
$effectiveGratingLinesPerMm = [int](Get-EffectiveUvexSetting $machineConfiguration $artifactConfiguration 'ExpectedGratingLinesPerMm')
$modeGratingLinesPerMm = [int]$modeConfiguration.Uvex.ExpectedGratingLinesPerMm
if ($EnableHardware -and $effectiveSimulator) {
    throw "$configurationPath is preserved and still has Simulator=true. Change it explicitly before installing with -EnableHardware."
}
if (-not $EnableHardware -and -not $effectiveSimulator) {
    throw "$configurationPath already enables real hardware. Re-run with -EnableHardware or change Simulator=true explicitly."
}
if ($effectiveGratingLinesPerMm -ne $modeGratingLinesPerMm) {
    throw "Effective ExpectedGratingLinesPerMm is $effectiveGratingLinesPerMm, but $configName requires $modeGratingLinesPerMm. Add the explicit value to $configurationPath or rebuild with the safe published default."
}
if ($PreflightOnly) {
    Write-Host "UVEX service preflight passed for $(if ($EnableHardware) { 'hardware' } else { 'simulator' }) mode with $effectiveGratingLinesPerMm lines/mm."
    return
}

# Stage and verify a complete copy before the installed service is stopped. This
# prevents a missing/corrupt artifact from taking the existing service offline.
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $installRoot ('.installing-' + [Guid]::NewGuid().ToString('N'))))
if (-not $stagingRoot.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage UVEX-ADV outside the install root: $stagingRoot"
}
$stagedService = Join-Path $stagingRoot 'Service'
$stagedAdmin = Join-Path $stagingRoot 'Admin'
New-Item -ItemType Directory -Force $stagedService, $stagedAdmin | Out-Null
try {
    Copy-Item -Path (Join-Path $ArtifactDirectory '*') -Destination $stagedService -Recurse -Force
    Copy-Item -Path (Join-Path $AdminArtifactDirectory '*') -Destination $stagedAdmin -Recurse -Force
    foreach ($requiredFile in @(
        (Join-Path $stagedService 'UvexAdv.Service.exe'),
        (Join-Path $stagedService 'UvexAdv.Service.dll'),
        (Join-Path $stagedAdmin 'UvexAdv.Admin.exe'),
        (Join-Path $stagedAdmin 'UvexAdv.Admin.dll')
    )) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Staged UVEX-ADV deployment is incomplete: $requiredFile"
        }
    }

    $existing = Get-Service $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service $serviceName -Force; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
        Invoke-ServiceControl -Arguments @('delete', $serviceName) | Out-Null
        $existing.Dispose()
        $existing = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ((Get-Service $serviceName -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
        }
        if (Get-Service $serviceName -ErrorAction SilentlyContinue) { throw "Timed out waiting for $serviceName deletion." }
    }

    foreach ($directory in @($installDirectory, $adminInstallDirectory)) {
        if (Test-Path -LiteralPath $directory) {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
    }
    Move-Item -LiteralPath $stagedService -Destination $installDirectory
    Move-Item -LiteralPath $stagedAdmin -Destination $adminInstallDirectory

    New-Item -ItemType Directory -Force $dataDirectory, (Join-Path $dataDirectory 'logs') | Out-Null
    if (-not (Test-Path -LiteralPath $configurationPath)) { Copy-Item -LiteralPath $sourceConfig -Destination $configurationPath }
    $aclOutput = & icacls.exe $dataDirectory /grant '*S-1-5-19:(OI)(CI)M' /T 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant LocalService access to $dataDirectory`n$($aclOutput -join [Environment]::NewLine)" }

    $exe = Join-Path $installDirectory 'UvexAdv.Service.exe'
    $quotedExe = '"' + $exe + '"'
    Invoke-ServiceControl -Arguments @(
        'create', $serviceName,
        'binPath=', $quotedExe,
        'start=', 'auto',
        'obj=', 'NT AUTHORITY\LocalService',
        'DisplayName=', 'OpenAstroSpec Auto — UVEX4 Spectrograph Service'
    ) | Out-Null
    Invoke-ServiceControl -Arguments @('description', $serviceName, 'Single-owner UVEX4 COM5 control and loopback API.') | Out-Null
    Invoke-ServiceControl -Arguments @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') | Out-Null
    Start-Service $serviceName
    $installed = Get-Service $serviceName
    $installed.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

$adminExe = Join-Path $adminInstallDirectory 'UvexAdv.Admin.exe'
$shortcutTargets = @(
    (Join-Path $env:PUBLIC 'Desktop\OpenAstroSpec Auto - UVEX4 Manager.lnk'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\OpenAstroSpec Auto - UVEX4 Manager.lnk')
)
. (Join-Path $PSScriptRoot 'shortcut-utils.ps1')
foreach ($shortcutPath in $shortcutTargets) {
    Set-UvexShortcut `
        -Path $shortcutPath `
        -TargetPath $adminExe `
        -WorkingDirectory $adminInstallDirectory `
        -IconLocation "$adminExe,0" `
        -Description 'OpenAstroSpec Auto — UVEX4 spectrograph manager'
}

foreach ($legacyShortcut in @(
    (Join-Path $env:PUBLIC 'Desktop\UVEX-ADV Manager.lnk'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\UVEX-ADV Manager.lnk')
)) {
    if (Test-Path -LiteralPath $legacyShortcut -PathType Leaf) {
        Remove-Item -LiteralPath $legacyShortcut -Force
    }
}

Write-Host "Installed $serviceName in $(if ($EnableHardware) { 'hardware' } else { 'simulator' }) mode with automatic Windows startup."
Write-Host "Manager shortcut: $($shortcutTargets[0])"
Write-Host "Configuration: $configurationPath"
