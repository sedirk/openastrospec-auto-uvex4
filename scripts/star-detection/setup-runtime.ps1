[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonPath)

# Explicit, optional image-analysis installation only. Never starts/stops NINA,
# selects a detector, opens a device, or modifies reduction/.venv.
$ErrorActionPreference = 'Stop'
if (Get-Process NINA -ErrorAction SilentlyContinue) {
    throw 'Close N.I.N.A. before switching the versioned SEP runtime; never hot-swap image algorithms during an active run.'
}
$python = (Resolve-Path -LiteralPath $PythonPath).Path
& $python -c "import sys; assert sys.version_info[:2] == (3,11), 'Python 3.11 required'"
if ($LASTEXITCODE -ne 0) { throw 'A working Python 3.11 interpreter is required.' }
$root = Join-Path $env:LOCALAPPDATA 'UVEX-ADV\star-detection'
$installation = Join-Path $root ('sep-1.4.1-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $installation -Force | Out-Null
$venv = Join-Path $installation 'venv'
& $python -m venv $venv
if ($LASTEXITCODE -ne 0) { throw "Environment creation failed; diagnostics retained at $installation" }
$runtimePython = Join-Path $venv 'Scripts\python.exe'
& $runtimePython -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw "Dependency install failed; previous runtime is unchanged. Files retained at $installation" }
foreach ($name in @('worker.py','sep_detector.py','focus_metrics.py')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $installation $name)
}
& $runtimePython -E -s -c "import sep,numpy; assert sep.__version__ == '1.4.1'; print('SEP runtime import verified')"
if ($LASTEXITCODE -ne 0) { throw 'Runtime import failed; previous configuration is unchanged.' }
$configuration = Join-Path $root 'runtime.json'
if (Test-Path -LiteralPath $configuration) {
    Copy-Item -LiteralPath $configuration -Destination (Join-Path $installation 'previous-runtime.json')
}
$settings = @{ python_path=$runtimePython; worker_path=(Join-Path $installation 'worker.py') }
$temporary = Join-Path $installation 'runtime.json'
$settings | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
Copy-Item -LiteralPath $temporary -Destination $configuration -Force
Write-Host "Optional SEP runtime configured: $configuration"
Write-Host 'NINA and its selected detector were not changed. See docs/star-detection-sep.md.'
