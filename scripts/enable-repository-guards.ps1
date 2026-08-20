[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& git -C $root rev-parse --is-inside-work-tree | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'OpenAstroSpec Auto — UVEX4 is not a Git work tree.' }

& git -C $root config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure the versioned Git hooks directory.' }

& (Join-Path $PSScriptRoot 'verify-design-baseline.ps1')
Write-Host 'Repository guards enabled through .githooks.'
