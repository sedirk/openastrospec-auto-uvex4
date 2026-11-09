[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Enable','Disable')][string]$Mode,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pythonw = Join-Path $root 'reduction/.venv/Scripts/pythonw.exe'
$library = Join-Path $env:LOCALAPPDATA 'UVEX-ADV/library'
$enabled = Join-Path $library 'target-success-retention.enabled'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'OpenAstroSpec Target Success Retention.lnk'
$legacyEnabled = Join-Path $library 'probe-retention.enabled'
$legacyStartup = Join-Path ([Environment]::GetFolderPath('Startup')) 'OpenAstroSpec Rejected Probe Retention.lnk'

if ($Mode -eq 'Disable') {
    Remove-Item -LiteralPath $enabled -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $startup -ErrorAction SilentlyContinue
    Write-Output 'Disabled future successful-target batches. Any in-progress exact-file batch completes with its audit.'
    exit
}

if (-not (Test-Path -LiteralPath $pythonw -PathType Leaf)) {
    throw 'Pinned Python environment missing.'
}
if ($PreflightOnly) {
    Write-Output 'Target-success retention preflight passed; no files or processes were changed.'
    exit
}

New-Item -ItemType Directory -Path $library -Force | Out-Null
@{
    policy = 'successful-target-parse-and-trial-retention'
    version = 2
    permanent = $true
    keepAcceptedScience = $true
    keepCreditedTrial = $true
    keepQhyPhotometry = $true
    enabledUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath $enabled -Encoding utf8

# Replace only the exact legacy opt-in files. The old worker observes its flag
# and exits at the next one-second poll; no process or observation is touched.
Remove-Item -LiteralPath $legacyEnabled -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacyStartup -ErrorAction SilentlyContinue

. (Join-Path $PSScriptRoot 'shortcut-utils.ps1')
$arguments = '-m uvex_reduce.library_probe_retention_cli --watch --policy target-success --execute-permanent-target-success'
Set-UvexShortcut -Path $startup -TargetPath $pythonw -WorkingDirectory (Join-Path $root 'reduction') `
    -Description 'Successful-target QHY/G3 parse and ATR trial retention; science and dependencies protected' `
    -IconLocation "$pythonw,0" -Arguments $arguments
$process = Start-Process -FilePath $pythonw -ArgumentList $arguments -WorkingDirectory (Join-Path $root 'reduction') `
    -WindowStyle Hidden -PassThru
Write-Output "Target-success retention enabled, process $($process.Id). No N.I.N.A./device action. Status: $library/target-success-retention-status.json"
