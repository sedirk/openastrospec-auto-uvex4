[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$reductionRoot = Join-Path $projectRoot 'reduction'
$pythonw = Join-Path $reductionRoot '.venv\Scripts\pythonw.exe'
$module = Join-Path $reductionRoot 'src\uvex_reduce\library_ui.py'
if (-not (Test-Path -LiteralPath $pythonw -PathType Leaf)) {
    throw 'The pinned reduction Python runtime is required. No dependencies will be upgraded.'
}
if (-not (Test-Path -LiteralPath $module -PathType Leaf)) {
    throw 'The observation library source is missing.'
}
. (Join-Path $PSScriptRoot 'shortcut-utils.ps1')
$chineseName = -join @([char]0x89C2, [char]0x6D4B, [char]0x8D44, [char]0x6599, [char]0x5E93)
$name = 'OpenAstroSpec ' + $chineseName + '.lnk'
$icon = Join-Path $env:LOCALAPPDATA 'Programs\UVEX-ADV\Reduction\UvexAdv.Reduction.Launcher.exe'
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { $icon = $pythonw }
foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $shortcut = Join-Path $folder $name
    Set-UvexShortcut -Path $shortcut -TargetPath $pythonw -WorkingDirectory $reductionRoot `
        -IconLocation "$icon,0" -Description 'OpenAstroSpec Library - offline in-place observation catalogue' `
        -Arguments '-m uvex_reduce.library_ui'
    Write-Output "Library shortcut installed: $shortcut"
}
Write-Output 'No observation application/service was changed or started.'
