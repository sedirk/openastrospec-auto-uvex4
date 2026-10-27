[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$AstapPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedAstapSha256,
    [Parameter(Mandatory)][string]$DatabasePath
)

# Explicit local analysis installation only. No download, device access, service
# restart, NINA startup or change to the reduction environment.
$ErrorActionPreference = 'Stop'
if (Get-Process NINA -ErrorAction SilentlyContinue) { throw 'Close N.I.N.A. normally before changing the optional analysis runtime.' }
$python = (Resolve-Path -LiteralPath $PythonPath).Path
$astap = (Resolve-Path -LiteralPath $AstapPath).Path
$database = (Resolve-Path -LiteralPath $DatabasePath).Path
if ((Get-FileHash -LiteralPath $astap).Hash -ne $ExpectedAstapSha256) { throw 'ASTAP hash does not match the approved artifact.' }
& $python -E -s -c "import sys,sep,numpy,scipy,astropy; assert sys.version_info[:2]==(3,11); assert sep.__version__=='1.4.1'; assert numpy.__version__=='2.2.6'; assert scipy.__version__=='1.15.3'; assert astropy.__version__=='7.1.0'"
if ($LASTEXITCODE -ne 0) { throw 'Existing isolated analysis environment failed validation; it was not changed.' }
$cells = @(1,3,9,15,21,27,33,38,43,48,52,56,60,63,65,67,68,69)
$reverse = $cells.Clone(); [array]::Reverse($reverse); $cells += $reverse
$files = @('acknowledgement of databases.txt')
for ($ring=0; $ring -lt $cells.Count; $ring++) {
    for ($cell=1; $cell -le $cells[$ring]; $cell++) { $files += ('d80_{0:00}{1:00}.1476' -f ($ring+1),$cell) }
}
foreach ($name in $files) {
    $file = Get-Item -LiteralPath (Join-Path $database $name)
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Not a regular catalogue file: $name" }
}
$root = Join-Path $env:LOCALAPPDATA 'UVEX-ADV/star-detection'
$installation = Join-Path $root ('astap-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path (Join-Path $installation 'd80'))
$manifest = @()
foreach ($name in $files) {
    $source = Join-Path $database $name
    $target = Join-Path "$installation/d80" $name
    $hash = (Get-FileHash -LiteralPath $source).Hash
    Copy-Item -LiteralPath $source -Destination $target
    if ((Get-FileHash -LiteralPath $target).Hash -ne $hash) { throw 'Catalogue copy hash mismatch; old runtime was not changed.' }
    $manifest += @{name=$name;sha256=$hash;bytes=(Get-Item -LiteralPath $target).Length}
}
$installedExe = Join-Path $installation 'astap_cli.exe'
Copy-Item -LiteralPath $astap -Destination $installedExe
if ((Get-FileHash -LiteralPath $installedExe).Hash -ne $ExpectedAstapSha256) { throw 'Installed solver hash mismatch.' }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$installation/catalogue-manifest.json" -Encoding UTF8
$next = Join-Path $installation 'runtime.json'
@{pythonPath=$python;astapPath=$installedExe;astapSha256=$ExpectedAstapSha256;databasePath=(Join-Path $installation 'd80')} |
    ConvertTo-Json | Set-Content -LiteralPath $next -Encoding UTF8
if (Get-Process NINA -ErrorAction SilentlyContinue) { throw 'N.I.N.A. started during staging; the previous runtime remains selected.' }
$selected = Join-Path $root 'astap-runtime.json'
if (Test-Path -LiteralPath $selected) {
    [IO.File]::Replace($next,$selected,(Join-Path $installation 'previous-runtime.json'))
} else { [IO.File]::Move($next,$selected) }
Write-Output "Optional ASTAP runtime installed and verified: $selected"
Write-Output '1476 catalogue tiles and upstream acknowledgement retained; no application or observation started.'
