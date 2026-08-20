$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$reductionRoot = Join-Path $projectRoot 'reduction'
$pythonw = Join-Path $reductionRoot '.venv\Scripts\pythonw.exe'
$launcherProject = Join-Path $projectRoot 'src\UvexAdv.Reduction.Launcher\UvexAdv.Reduction.Launcher.csproj'
$launcherArtifacts = Join-Path $projectRoot 'artifacts\reduction-launcher'
$launcherArtifact = Join-Path $launcherArtifacts 'UvexAdv.Reduction.Launcher.exe'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\UVEX-ADV\Reduction'

if (-not (Test-Path -LiteralPath $pythonw -PathType Leaf)) {
    throw "Python GUI runtime not found: $pythonw. Install reduction\requirements-lock.txt first."
}

if (-not (Test-Path -LiteralPath $launcherArtifact -PathType Leaf)) {
    $dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    }
    & $dotnet publish $launcherProject -c Release --self-contained false -o $launcherArtifacts
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish the native reduction launcher (exit code $LASTEXITCODE)."
    }
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $launcherArtifacts '*') -Destination $installDirectory -Recurse -Force
$installedLauncher = Join-Path $installDirectory 'UvexAdv.Reduction.Launcher.exe'
@{ ProjectRoot = $projectRoot } |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $installDirectory 'launcher.settings.json') -Encoding UTF8

. (Join-Path $PSScriptRoot 'shortcut-utils.ps1')

$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = [Environment]::GetFolderPath('Programs')
$chineseName = -join @(
    [char]0x5149
    [char]0x8C31
    [char]0x5904
    [char]0x7406
)
$shortcutName = 'OpenAstroSpec ' + $chineseName + ' - UVEX4.lnk'
$shortcutTargets = @(
    (Join-Path $desktop $shortcutName),
    (Join-Path $startMenu $shortcutName)
)
$adminIcon = Join-Path $env:ProgramFiles 'UVEX-ADV\Admin\UvexAdv.Admin.exe'
$icon = if (Test-Path -LiteralPath $adminIcon -PathType Leaf) { $adminIcon } else { $installedLauncher }
foreach ($shortcutPath in $shortcutTargets) {
    Set-UvexShortcut `
        -Path $shortcutPath `
        -TargetPath $installedLauncher `
        -WorkingDirectory $installDirectory `
        -IconLocation "$icon,0" `
        -Description 'OpenAstroSpec Spectral Studio — UVEX4'
}

foreach ($legacyShortcut in @(
    (Join-Path $desktop ('UVEX-ADV ' + $chineseName + '.lnk')),
    (Join-Path $startMenu ('UVEX-ADV ' + $chineseName + '.lnk'))
)) {
    if (Test-Path -LiteralPath $legacyShortcut -PathType Leaf) {
        Remove-Item -LiteralPath $legacyShortcut -Force
    }
}

$iconRefresh = Get-Command ie4uinit.exe -ErrorAction SilentlyContinue
if ($iconRefresh) { & $iconRefresh.Source -show | Out-Null }

Write-Host "Native launcher installed: $installedLauncher"
Write-Host "Desktop shortcut created: $($shortcutTargets[0])"
Write-Host "Start menu shortcut created: $($shortcutTargets[1])"
