[CmdletBinding()]
param([switch]$Strict)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$git = (Get-Command git -ErrorAction Stop).Source
$files = @(& $git -C $root ls-files --cached --others --exclude-standard) |
    Sort-Object -Unique
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }

$self = 'scripts/audit-public-release.ps1'
$documentedCommand = 'docs/open-source-release-checklist.md'
$patterns = [ordered]@{
    # Match any concrete Windows home directory without baking this workstation's
    # account name into the audit tool itself. Placeholders such as
    # C:\Users\<user> and environment variables are intentionally ignored.
    'local Windows user path' = 'C:[\\/]+Users[\\/]+(?!<|%|\$)[^\\/\s"''<>]+'
    'observatory stable camera identity' = 'QHYminiCam8M-[0-9a-fA-F]{12,}'
    # Require a value after the final '#'. A bare VID/PID product assertion is
    # public device-family metadata, not a workstation USB instance binding.
    'USB instance path requiring review' = 'usb#vid_[0-9a-fA-F]{4}&pid_[0-9a-fA-F]{4}#(?!<)[^\\/\s"''<>]+'
    # Keep every octet in the match and require a numeric boundary so version
    # strings such as 10.0.40219.1 cannot be mistaken for an RFC1918 address.
    'private IPv4 address' = '(?<![0-9])(?:10\.(?:[0-9]{1,3}\.){2}[0-9]{1,3}|192\.168\.[0-9]{1,3}\.[0-9]{1,3}|172\.(?:1[6-9]|2[0-9]|3[01])\.[0-9]{1,3}\.[0-9]{1,3})(?![0-9])'
    'RTSP URL' = 'rtsp://'
    # N.I.N.A. file-pattern placeholders are deliberately written as
    # $$TOKENNAME$$.  They are public template syntax, not assigned secrets.
    # Keep scanning ordinary quoted assignments while excluding only that
    # exact placeholder prefix.
    'possible secret assignment' = '(?i)(api[_-]?key|password|secret|token)\s*[:=]\s*["''](?!\$\$)[^"'']{8,}'
}

$findings = [Collections.Generic.List[object]]::new()
foreach ($relativePath in $files) {
    $normalized = $relativePath.Replace('\', '/')
    if ($normalized -eq $self -or $normalized -eq $documentedCommand) { continue }
    $fullPath = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
    try { $lines = Get-Content -LiteralPath $fullPath -ErrorAction Stop }
    catch { continue }
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        foreach ($entry in $patterns.GetEnumerator()) {
            foreach ($match in [regex]::Matches($lines[$lineIndex], $entry.Value)) {
                # Test-only identities must say "fixture" in the instance
                # suffix. Keep this exception deliberately narrow: it applies
                # only to USB-instance findings below tests/, never production
                # code, documentation, configuration, or other finding kinds.
                $explicitUsbFixture =
                    $entry.Key -eq 'USB instance path requiring review' -and
                    $normalized.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase) -and
                    $match.Value -match '^usb#vid_[0-9a-fA-F]{4}&pid_[0-9a-fA-F]{4}#fixture-[A-Za-z0-9_-]+$'
                if ($explicitUsbFixture) { continue }
                $findings.Add([pscustomobject]@{
                    Kind = $entry.Key
                    Path = $normalized
                    Line = $lineIndex + 1
                    Text = $lines[$lineIndex].Trim()
                })
            }
        }
    }
}

$binaryExtensions = @('.dll', '.exe', '.zip', '.fit', '.fits', '.fts', '.db', '.sqlite', '.log')
$trackedBinaries = @($files | Where-Object {
    $binaryExtensions -contains [IO.Path]::GetExtension($_).ToLowerInvariant()
})

Write-Host "Public-release audit: $($findings.Count) text finding(s), $($trackedBinaries.Count) cached/untracked binary/data candidate(s)."
if ($findings.Count -gt 0) {
    $findings | Sort-Object Kind, Path, Line | Format-Table Kind, Path, Line, Text -Wrap
}
if ($trackedBinaries.Count -gt 0) {
    Write-Host 'Cached/untracked binary/data candidates:'
    $trackedBinaries | ForEach-Object { Write-Host "  $_" }
}

if ($Strict -and ($findings.Count -gt 0 -or $trackedBinaries.Count -gt 0)) {
    throw 'Public-release audit has unresolved findings. Review and sanitize deliberately before publishing.'
}
