[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Enable','Disable')][string]$Mode,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pythonw = Join-Path $root 'reduction/.venv/Scripts/pythonw.exe'
$library = Join-Path $env:LOCALAPPDATA 'UVEX-ADV/library'
$enabled = Join-Path $library 'terminal-target-retention.enabled'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'OpenAstroSpec Terminal Target Retention.lnk'
$previousEnabled = Join-Path $library 'target-success-retention.enabled'
$previousStartup = Join-Path ([Environment]::GetFolderPath('Startup')) 'OpenAstroSpec Target Success Retention.lnk'

if ($Mode -eq 'Disable') {
    Remove-Item -LiteralPath $enabled -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $startup -ErrorAction SilentlyContinue
    Write-Output 'Disabled future terminal-target batches. Any in-progress exact-file batch completes with its audit.'
    exit
}

if (-not (Test-Path -LiteralPath $pythonw -PathType Leaf)) {
    throw 'Pinned Python environment missing.'
}
if ($PreflightOnly) {
    Write-Output 'Terminal-target retention preflight passed; no files or processes were changed.'
    exit
}

New-Item -ItemType Directory -Path $library -Force | Out-Null
@{
    policy = 'terminal-target-disposable-frame-retention'
    version = 1
    permanent = $true
    terminalStates = @('Completed','Faulted','Failed','Cancelled','Canceled')
    keepAcceptedScience = $true
    keepCreditedTrial = $true
    keepQhyPhotometry = $true
    intervalSeconds = 300
    enabledUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath $enabled -Encoding utf8

# Retire only the exact previous opt-in. Its worker observes the removed flag
# and exits without touching an in-progress exact-file transaction.
Remove-Item -LiteralPath $previousEnabled -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $previousStartup -ErrorAction SilentlyContinue

. (Join-Path $PSScriptRoot 'shortcut-utils.ps1')
$arguments = '-m uvex_reduce.library_probe_retention_cli --watch --policy terminal-target --execute-permanent-terminal-target --interval-seconds 300'
Set-UvexShortcut -Path $startup -TargetPath $pythonw -WorkingDirectory (Join-Path $root 'reduction') `
    -Description 'Terminal-target QHY/G3 acquisition and uncredited ATR trial retention; science and dependencies protected' `
    -IconLocation "$pythonw,0" -Arguments $arguments
$process = Start-Process -FilePath $pythonw -ArgumentList $arguments -WorkingDirectory (Join-Path $root 'reduction') `
    -WindowStyle Hidden -PassThru
Write-Output "Terminal-target retention enabled, process $($process.Id). No N.I.N.A./device action. Status: $library/terminal-target-retention-status.json"
