[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Enable','Disable')][string]$Mode)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pythonw = Join-Path $root 'reduction/.venv/Scripts/pythonw.exe'
$library = Join-Path $env:LOCALAPPDATA 'UVEX-ADV/library'
$enabled = Join-Path $library 'probe-retention.enabled'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'OpenAstroSpec Rejected Probe Retention.lnk'
if ($Mode -eq 'Disable') {
    # Exact files only; the worker notices the flag within one second between
    # batches. No process kill, service action, or observation cancellation.
    Remove-Item -LiteralPath $enabled -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $startup -ErrorAction SilentlyContinue
    Write-Output 'Disabled future batches. Any in-progress exact-file batch completes with its audit.'
    exit
}
if (-not (Test-Path -LiteralPath $pythonw -PathType Leaf)) { throw 'Pinned Python environment missing.' }
New-Item -ItemType Directory -Path $library -Force | Out-Null
@{ policy='accepted-science-terminal-run-rejected-probes-only'; version=1;
   permanent=$true; enabledUtc=[DateTimeOffset]::UtcNow.ToString('O') } |
    ConvertTo-Json | Set-Content -LiteralPath $enabled -Encoding utf8
. (Join-Path $PSScriptRoot 'shortcut-utils.ps1')
$arguments = '-m uvex_reduce.library_probe_retention_cli --watch --execute-permanent-rejected-probes'
Set-UvexShortcut -Path $startup -TargetPath $pythonw -WorkingDirectory (Join-Path $root 'reduction') `
    -Description 'Opt-in offline rejected-probe retention; good probes/science/dependencies protected' `
    -IconLocation "$pythonw,0" -Arguments $arguments
$process = Start-Process -FilePath $pythonw -ArgumentList $arguments -WorkingDirectory (Join-Path $root 'reduction') `
    -WindowStyle Hidden -PassThru
Write-Output "Retention enabled, process $($process.Id). No NINA/device action. Status: $library/probe-retention-status.json"
