[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& (Join-Path $PSScriptRoot 'verify-design-baseline.ps1') -Quiet
& (Join-Path $PSScriptRoot 'verify-product-layout.ps1') -Quiet
& (Join-Path $PSScriptRoot 'verify-public-branding.ps1') -Quiet
& (Join-Path $PSScriptRoot 'test-coordinate-command-safety.ps1') -Quiet

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Invoke-DotNet -Arguments @('restore', (Join-Path $root 'UVEX-ADV.sln'))
Invoke-DotNet -Arguments @('build', (Join-Path $root 'UVEX-ADV.sln'), '-c', $Configuration, '--no-restore')
Invoke-DotNet -Arguments @('test', (Join-Path $root 'UVEX-ADV.sln'), '-c', $Configuration, '--no-build')

$artifacts = Join-Path $root 'artifacts'
$serviceArtifactDirectory = Join-Path $artifacts 'service'
$qhyServiceArtifactDirectory = Join-Path $artifacts 'qhy-service'
$phd2WatchdogArtifactDirectory = Join-Path $artifacts 'phd2-watchdog'
$lockingService = Get-Process UvexAdv.Service -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($serviceArtifactDirectory, [StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1
if ($lockingService) {
    throw "UvexAdv.Service PID $($lockingService.Id) is running from artifacts\service and locks publish output. Stop that console instance; installed Windows services do not have this problem."
}
$lockingQhyService = Get-Process UvexAdv.Qhy.Service -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($qhyServiceArtifactDirectory, [StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1
if ($lockingQhyService) {
    throw "UvexAdv.Qhy.Service PID $($lockingQhyService.Id) is running from artifacts\qhy-service and locks publish output. Stop that console instance; installed Windows services do not have this problem."
}
$lockingPhd2Watchdog = Get-Process UvexAdv.Phd2.Watchdog -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($phd2WatchdogArtifactDirectory, [StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1
if ($lockingPhd2Watchdog) {
    throw "UvexAdv.Phd2.Watchdog PID $($lockingPhd2Watchdog.Id) is running from artifacts\phd2-watchdog and locks publish output. Stop that console instance; installed Windows services do not have this problem."
}

function Reset-ArtifactDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $artifactRoot = [IO.Path]::GetFullPath($artifacts)
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear publish output outside the artifacts directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$adminArtifactDirectory = Join-Path $artifacts 'admin'
$commissioningToolArtifactDirectory = Join-Path $artifacts 'commissioning-tool'
$reductionLauncherArtifactDirectory = Join-Path $artifacts 'reduction-launcher'
$ninaPluginArtifactDirectory = Join-Path $artifacts 'nina-plugin'
@(
    $serviceArtifactDirectory,
    $qhyServiceArtifactDirectory,
    $phd2WatchdogArtifactDirectory,
    $adminArtifactDirectory,
    $commissioningToolArtifactDirectory,
    $reductionLauncherArtifactDirectory,
    $ninaPluginArtifactDirectory
) | ForEach-Object { Reset-ArtifactDirectory -Path $_ }

Invoke-DotNet -Arguments @('publish', (Join-Path $root 'src\UvexAdv.Service\UvexAdv.Service.csproj'), '-c', $Configuration, '--no-build', '-o', $serviceArtifactDirectory)
Invoke-DotNet -Arguments @('publish', (Join-Path $root 'src\UvexAdv.Qhy.Service\UvexAdv.Qhy.Service.csproj'), '-c', $Configuration, '--no-build', '-o', $qhyServiceArtifactDirectory)
Invoke-DotNet -Arguments @('publish', (Join-Path $root 'src\UvexAdv.Phd2.Watchdog\UvexAdv.Phd2.Watchdog.csproj'), '-c', $Configuration, '--no-build', '-o', $phd2WatchdogArtifactDirectory)
Invoke-DotNet -Arguments @('publish', (Join-Path $root 'src\UvexAdv.Admin\UvexAdv.Admin.csproj'), '-c', $Configuration, '--no-build', '--self-contained', 'false', '-o', $adminArtifactDirectory)
Invoke-DotNet -Arguments @('publish', (Join-Path $root 'src\UvexAdv.Commissioning.Tool\UvexAdv.Commissioning.Tool.csproj'), '-c', $Configuration, '--no-build', '--self-contained', 'false', '-o', $commissioningToolArtifactDirectory)
Invoke-DotNet -Arguments @('publish', (Join-Path $root 'src\UvexAdv.Reduction.Launcher\UvexAdv.Reduction.Launcher.csproj'), '-c', $Configuration, '--no-build', '--self-contained', 'false', '-o', $reductionLauncherArtifactDirectory)
Invoke-DotNet -Arguments @('publish', (Join-Path $root 'src\UvexAdv.Nina.Plugin\UvexAdv.Nina.Plugin.csproj'), '-c', $Configuration, '--no-build', '-o', $ninaPluginArtifactDirectory)

foreach ($artifactDirectory in @(
    $serviceArtifactDirectory,
    $qhyServiceArtifactDirectory,
    $phd2WatchdogArtifactDirectory,
    $adminArtifactDirectory,
    $commissioningToolArtifactDirectory,
    $reductionLauncherArtifactDirectory,
    $ninaPluginArtifactDirectory
)) {
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $artifactDirectory 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $artifactDirectory 'THIRD_PARTY_NOTICES.md') -Force
}

Write-Host "Artifacts: $artifacts"
Write-Host 'N.I.N.A. plugin was built but not installed. Close N.I.N.A., then run scripts\install-nina-plugin.ps1.'
Write-Host 'Publishing does not change any installed Windows service or machine configuration. The PHD2 watchdog is built but not installed; use the explicit elevated installer script after review.'
Write-Host 'The QHY artifact defaults to Simulator=true; hardware mode requires the explicit elevated installer switch.'
