[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$productRoot = Join-Path $root 'products'
$manifestFiles = @(Get-ChildItem -LiteralPath $productRoot -Filter product.json -File -Recurse)
if ($manifestFiles.Count -ne 2) {
    throw "Expected exactly two OpenAstroSpec product manifests; found $($manifestFiles.Count)."
}

$products = foreach ($file in $manifestFiles) {
    $product = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($property in @('schemaVersion', 'productId', 'displayName', 'license', 'sourceRoots', 'humanEntryPoints', 'hardwareCapable')) {
        if ($null -eq $product.$property) {
            throw "Product manifest '$($file.FullName)' is missing '$property'."
        }
    }
    if ($product.schemaVersion -ne 1) { throw "Unsupported product schema in '$($file.FullName)'." }
    if ($product.license -cne 'GPL-3.0-only') { throw "Product '$($product.productId)' must declare GPL-3.0-only." }
    foreach ($sourceRoot in @($product.sourceRoots)) {
        if ([string]::IsNullOrWhiteSpace($sourceRoot) -or [IO.Path]::IsPathRooted($sourceRoot)) {
            throw "Product '$($product.productId)' has an invalid source root '$sourceRoot'."
        }
        $fullPath = [IO.Path]::GetFullPath((Join-Path $root $sourceRoot))
        if (-not $fullPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Product '$($product.productId)' source root escapes the repository: '$sourceRoot'."
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
            throw "Product '$($product.productId)' source root does not exist: '$sourceRoot'."
        }
    }
    [pscustomobject]@{ File = $file; Definition = $product }
}

$byId = @{}
foreach ($item in $products) {
    $id = [string]$item.Definition.productId
    if ($byId.ContainsKey($id)) { throw "Duplicate product ID '$id'." }
    $byId[$id] = $item.Definition
}

foreach ($requiredId in @('uvex-adv-observatory', 'uvex-adv-spectral-studio')) {
    if (-not $byId.ContainsKey($requiredId)) { throw "Missing required product '$requiredId'." }
}
if (-not [bool]$byId['uvex-adv-observatory'].hardwareCapable) {
    throw 'OpenAstroSpec Auto — UVEX4 must honestly declare that it is hardware-capable.'
}
if ([bool]$byId['uvex-adv-spectral-studio'].hardwareCapable) {
    throw 'OpenAstroSpec Spectral Studio — UVEX4 must remain offline-only.'
}

$licensePath = Join-Path $root 'LICENSE'
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw 'Root LICENSE is missing.' }
$licenseHeading = (Get-Content -LiteralPath $licensePath -TotalCount 3) -join "`n"
if ($licenseHeading -notmatch 'GNU GENERAL PUBLIC LICENSE' -or $licenseHeading -notmatch 'Version 3') {
    throw 'Root LICENSE is not recognizable as GNU GPL version 3.'
}

$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw
foreach ($name in @('OpenAstroSpec Auto — UVEX4', 'OpenAstroSpec Spectral Studio — UVEX4', 'GPL-3.0-only')) {
    if ($readme.IndexOf($name, [StringComparison]::Ordinal) -lt 0) {
        throw "Root README does not present '$name'."
    }
}

if (-not $Quiet) {
    Write-Host 'Verified one repository, two products, GPL-3.0-only (2 manifests).'
}
