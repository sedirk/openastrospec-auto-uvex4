[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $root 'docs\design-baseline.sha256'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Frozen-design manifest is missing: $manifestPath"
}

$rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$failures = [Collections.Generic.List[string]]::new()
$entryCount = 0

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

foreach ($line in Get-Content -LiteralPath $manifestPath) {
    $trimmed = $line.Trim()
    if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
    if ($trimmed -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
        $failures.Add("Malformed manifest entry: $line")
        continue
    }

    $entryCount++
    $expected = $Matches[1].ToUpperInvariant()
    $relative = $Matches[2].Trim().Replace('/', [IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        $failures.Add("Manifest path escapes the repository: $relative")
        continue
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Protected file is missing: $relative")
        continue
    }

    $actual = (Get-Sha256Hex -Path $path).ToUpperInvariant()
    if ($actual -ne $expected) {
        $failures.Add("Protected file changed: $relative (expected $expected, actual $actual)")
    }
}

if ($entryCount -eq 0) {
    $failures.Add('Frozen-design manifest contains no protected files.')
}

if ($failures.Count -gt 0) {
    $message = @(
        'Frozen design verification failed.'
        $failures
        'Do not bypass this check. Revert accidental edits. For an explicitly approved design change, add a superseding ADR and run scripts\update-design-baseline-hash.ps1 -ConfirmFrozenDesignChange.'
    ) -join [Environment]::NewLine
    throw $message
}

if (-not $Quiet) {
    Write-Host "Frozen design verified ($entryCount files)."
}
