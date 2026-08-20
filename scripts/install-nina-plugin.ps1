[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $root 'artifacts\nina-plugin'
}
if (Get-Process NINA -ErrorAction SilentlyContinue) { throw 'Close N.I.N.A. before installing or updating the plugin.' }

$requiredFiles = @(
    'UvexAdv.Nina.Plugin.dll',
    'UvexAdv.Core.dll',
    'UvexAdv.Observatory.dll',
    'UvexAdv.Phd2.dll',
    'UvexAdv.Protocol.dll',
    'UvexAdv.Qhy.Core.dll',
    'UvexAdv.Spectroscopy.dll'
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $ArtifactDirectory $requiredFile) -PathType Leaf)) {
        throw "The N.I.N.A. artifact is incomplete or stale (missing $requiredFile). Run scripts\build.ps1 first."
    }
}

$assemblyInfoPath = Join-Path $root 'src\UvexAdv.Nina.Plugin\Properties\AssemblyInfo.cs'
$assemblyInfo = Get-Content -LiteralPath $assemblyInfoPath -Raw
$declaredVersionMatch = [regex]::Match($assemblyInfo, 'AssemblyVersion\("(?<version>[^"]+)"\)')
if (-not $declaredVersionMatch.Success) {
    throw "Cannot read the declared plugin version from $assemblyInfoPath."
}
$declaredVersion = [Version]$declaredVersionMatch.Groups['version'].Value
$artifactPlugin = Join-Path $ArtifactDirectory 'UvexAdv.Nina.Plugin.dll'
$artifactVersion = [Reflection.AssemblyName]::GetAssemblyName($artifactPlugin).Version
if ($artifactVersion -ne $declaredVersion) {
    throw "The N.I.N.A. artifact version $artifactVersion does not match source version $declaredVersion. Run scripts\build.ps1 first."
}

$pluginRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0'))
$destination = [IO.Path]::GetFullPath((Join-Path $pluginRoot 'UVEX-ADV Spectroscopy'))
if (-not $destination.StartsWith($pluginRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace plugin outside the N.I.N.A. plugin root: $destination"
}
if ($PreflightOnly) {
    Write-Host "N.I.N.A. plugin preflight passed for version $artifactVersion."
    return
}

New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
$deploymentId = [Guid]::NewGuid().ToString('N')
$staging = [IO.Path]::GetFullPath((Join-Path $pluginRoot ".UVEX-ADV-Spectroscopy.installing-$deploymentId"))
$backup = [IO.Path]::GetFullPath((Join-Path $pluginRoot ".UVEX-ADV-Spectroscopy.backup-$deploymentId"))
foreach ($temporaryPath in @($staging, $backup)) {
    if (-not $temporaryPath.StartsWith($pluginRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a plugin deployment path outside the N.I.N.A. plugin root: $temporaryPath"
    }
}

try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -Path (Join-Path $ArtifactDirectory '*') -Destination $staging -Recurse -Force
    foreach ($requiredFile in $requiredFiles) {
        $sourceFile = Join-Path $ArtifactDirectory $requiredFile
        $stagedFile = Join-Path $staging $requiredFile
        if (-not (Test-Path -LiteralPath $stagedFile -PathType Leaf) -or
            (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $stagedFile -Algorithm SHA256).Hash) {
            throw "Staged plugin verification failed for $requiredFile."
        }
    }

    if (Test-Path -LiteralPath $destination) {
        Move-Item -LiteralPath $destination -Destination $backup
    }

    try {
        Move-Item -LiteralPath $staging -Destination $destination
    }
    catch {
        if (-not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $destination
        }
        throw
    }

    if (Test-Path -LiteralPath $backup) {
        Remove-Item -LiteralPath $backup -Recurse -Force
    }
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    if (Test-Path -LiteralPath $backup) {
        Write-Warning "The previous plugin was preserved at $backup because deployment did not complete cleanly."
    }
}

Write-Host "Installed N.I.N.A. plugin $artifactVersion to $destination"
