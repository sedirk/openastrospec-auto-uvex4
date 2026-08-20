#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'UVEX-ADV-PHD2-WATCHDOG'
$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    $output = & sc.exe delete $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to delete $serviceName`n$($output -join [Environment]::NewLine)"
    }
}

$installRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'UVEX-ADV'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $installRoot 'Phd2Watchdog'))
if (-not $installDirectory.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove PHD2 watchdog binaries outside the UVEX-ADV install root: $installDirectory"
}
if (Test-Path -LiteralPath $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

Write-Host 'OpenAstroSpec Auto — UVEX4 PHD2 watchdog service and binaries removed.'
Write-Host 'ProgramData configuration, lease evidence, and last health status were deliberately preserved.'
