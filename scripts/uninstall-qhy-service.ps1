#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$serviceName = 'UVEX-ADV-QHY'
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

Write-Host 'OpenAstroSpec Auto — UVEX4 QHY service removed. Program files, machine configuration, manifests, and captured frames were preserved.'
