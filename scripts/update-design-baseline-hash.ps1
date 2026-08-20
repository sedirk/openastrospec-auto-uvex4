[CmdletBinding()]
param([switch]$ConfirmFrozenDesignChange)

$ErrorActionPreference = 'Stop'
if (-not $ConfirmFrozenDesignChange) {
    throw 'Refusing to update frozen-design hashes without -ConfirmFrozenDesignChange. Read CONTRIBUTING.md and add a superseding ADR first.'
}

$root = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $root 'docs\design-baseline.sha256'
$protectedFiles = @(
    'AGENTS.md',
    'docs/design/observatory-automation-baseline.md',
    'docs/adr/0001-single-owner-device-orchestration.md',
    'docs/adr/0002-automatic-progression-with-operator-pause.md'
)

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha256.ComputeHash($stream)
        return -join ($bytes | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

$lines = @(
    '# SHA-256 manifest for frozen acquisition-design records.'
    '# Update only through the explicit design-change process in CONTRIBUTING.md.'
)
foreach ($relative in $protectedFiles) {
    $path = Join-Path $root $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Protected file is missing: $relative"
    }
    $hash = (Get-Sha256Hex -Path $path).ToLowerInvariant()
    $lines += "$hash  $relative"
}

Set-Content -LiteralPath $manifestPath -Value $lines -Encoding Ascii
Write-Host "Updated frozen-design manifest: $manifestPath"
Write-Host 'Review the ADR, baseline and hash diff together before committing.'
