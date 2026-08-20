[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))

function Read-RepositoryText {
    param([Parameter(Mandatory)][string]$RelativePath)

    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Branding check is missing '$RelativePath'."
    }
    Get-Content -LiteralPath $path -Raw
}

function Assert-ContainsText {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Expected
    )

    $text = Read-RepositoryText $RelativePath
    if ($text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "Branding check expected '$Expected' in '$RelativePath'."
    }
}

function Assert-DoesNotContainText {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Unexpected
    )

    $text = Read-RepositoryText $RelativePath
    if ($text.IndexOf($Unexpected, [StringComparison]::Ordinal) -ge 0) {
        throw "Branding check found stale public text '$Unexpected' in '$RelativePath'."
    }
}

# Canonical public surfaces.
Assert-ContainsText 'README.md' '# OpenAstroSpec Auto — UVEX4'
Assert-ContainsText 'docs\project-branding-and-name-migration.md' '> **OpenAstroSpec Auto — UVEX4**'
Assert-ContainsText 'products\observatory\product.json' '"displayName": "OpenAstroSpec Auto — UVEX4"'
Assert-ContainsText 'products\spectral-studio\product.json' '"displayName": "OpenAstroSpec Spectral Studio — UVEX4"'
Assert-ContainsText 'src\UvexAdv.Nina.Plugin\Properties\AssemblyInfo.cs' 'AssemblyTitle("OpenAstroSpec Auto — UVEX4")'
Assert-ContainsText 'src\UvexAdv.Nina.Plugin\Templates.xaml' 'x:Key="OpenAstroSpec Auto — UVEX4_Options"'
Assert-ContainsText 'src\UvexAdv.Nina.Plugin\Templates.xaml' 'Text="OpenAstroSpec Auto — UVEX4"'
Assert-ContainsText 'src\UvexAdv.Nina.Plugin\ObservationDockable.cs' 'Title = "OpenAstroSpec 自动观测";'
Assert-ContainsText 'src\UvexAdv.Admin\MainWindow.xaml' 'Title="OpenAstroSpec Auto — UVEX4 管理器"'
Assert-ContainsText 'reduction\src\uvex_reduce\studio.py' 'OpenAstroSpec Spectral Studio — UVEX4'

Assert-DoesNotContainText 'src\UvexAdv.Nina.Plugin\Properties\AssemblyInfo.cs' 'AssemblyTitle("UVEX-ADV'
Assert-DoesNotContainText 'src\UvexAdv.Nina.Plugin\Templates.xaml' 'Text="UVEX-ADV 自动观测"'
Assert-DoesNotContainText 'src\UvexAdv.Admin\MainWindow.xaml' 'Title="UVEX-ADV 管理器"'
Assert-DoesNotContainText 'reduction\src\uvex_reduce\studio.py' 'self.root.title("UVEX-ADV Spectral Studio")'

# Compatibility identities deliberately remain stable in this display-only phase.
Assert-ContainsText 'src\UvexAdv.Nina.Plugin\UvexPluginSettings.cs' 'Guid.Parse("A4183531-55BD-4FD0-B04A-97ED7EDC15DA")'
Assert-ContainsText 'scripts\install-nina-plugin.ps1' "'UVEX-ADV Spectroscopy'"
Assert-ContainsText 'scripts\install-service.ps1' "`$serviceName = 'UVEX-ADV'"
Assert-ContainsText 'scripts\install-service.ps1' "Join-Path `$env:ProgramData 'UVEX-ADV'"
Assert-ContainsText 'scripts\install-qhy-service.ps1' "`$serviceName = 'UVEX-ADV-QHY'"
Assert-ContainsText 'scripts\install-phd2-watchdog.ps1' "`$serviceName = 'UVEX-ADV-PHD2-WATCHDOG'"

if (-not $Quiet) {
    Write-Host 'Verified OpenAstroSpec public branding and retained UVEX-ADV compatibility identities.'
}
