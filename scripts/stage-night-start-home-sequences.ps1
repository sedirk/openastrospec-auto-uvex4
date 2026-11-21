#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z_-]+$')][string]$StageName
)
# Offline candidates only. No parameter accepts a live output directory. Every
# source byte except the one property is preserved (including BOM/line endings).
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$stage = Join-Path $repo "output\$StageName"
if (Test-Path -LiteralPath $stage) { throw "Use a fresh stage; will not replace $stage" }
if ((Get-Item -LiteralPath (Join-Path $repo 'output')).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing a redirected output directory.'
}
$type = 'UvexAdv.Nina.Plugin.SequenceItems.UvexNightSequenceContainer, UvexAdv.Nina.Plugin'
$field = 'HomeMountAtNightStart'
$stamp = [regex]('(?m)^(?<spaces>[ \t]*)"\$type"\s*:\s*"' + [regex]::Escape($type) + '"[ \t]*,[ \t]*(?<nl>\r?\n)')
$sourceNames = @('front0.json','mid0.json','later0.json','front1.json','later1.json',
    'OpenAstroSpec-Spectroscopy-MultiTarget.json','Templates\OpenAstroSpec-Spectroscopy-TargetBlock.template.json')
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function HashBytes([byte[]]$bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }
$entries = foreach ($relative in $sourceNames) {
    $src = Join-Path $source $relative
    $bytes = [IO.File]::ReadAllBytes($src)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($bom) {3} else {0}
    $original = $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
    $parsed = [Text.Json.Nodes.JsonNode]::Parse($original)
    $start = $stamp.Matches($original)
    if ($start.Count -ne 1) { throw "Expected one explicit night in $relative." }
    if ($original -match ('"' + $field + '"\s*:')) { throw "$relative already makes an explicit choice. Preserve it and review separately." }
    $m = $start[0]
    $added = $m.Groups['spaces'].Value + '"' + $field + '": true,' + $m.Groups['nl'].Value
    $candidate = $original.Insert($m.Index + $m.Length, $added)
    $null = [Text.Json.Nodes.JsonNode]::Parse($candidate)
    if ($candidate.Remove($m.Index + $m.Length, $added.Length) -cne $original) { throw 'Unrelated sequence content changed.' }
    [byte[]]$newBytes = [Text.UTF8Encoding]::new($bom).GetPreamble() + $utf8.GetBytes($candidate)
    $kind = if ($relative -match '^(front|mid|later)[01]\.json$') {'plans'} else {'templates'}
    [pscustomobject]@{ source = $relative; staged = "$kind/$([IO.Path]::GetFileName($relative))"
        originalSha256 = HashBytes $bytes; candidateSha256 = HashBytes $newBytes; bytes = $newBytes }
}
foreach ($entry in $entries) {
    $dest = Join-Path $stage $entry.staged
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))
    $file = [IO.File]::Open($dest, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($entry.bytes); $file.Flush($true) } finally { $file.Dispose() }
    if ((Get-FileHash -LiteralPath $dest).Hash -ne $entry.candidateSha256 -or
        (Get-FileHash -LiteralPath (Join-Path $source $entry.source)).Hash -ne $entry.originalSha256) {
        throw "Source or staged candidate changed: $($entry.source). Do not install these candidates."
    }
}
$entries | Select-Object source,staged,originalSha256,candidateSha256 |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'source-hashes.json') -Encoding utf8NoBOM
Write-Output "Staged $($entries.Count) candidates in $stage. No original was written."
