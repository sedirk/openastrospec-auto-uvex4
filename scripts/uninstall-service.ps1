#Requires -RunAsAdministrator
$service = Get-Service 'UVEX-ADV' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service 'UVEX-ADV' -Force }
    sc.exe delete 'UVEX-ADV' | Out-Null
}
$shortcutTargets = @(
    (Join-Path $env:PUBLIC 'Desktop\OpenAstroSpec Auto - UVEX4 Manager.lnk'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\OpenAstroSpec Auto - UVEX4 Manager.lnk'),
    (Join-Path $env:PUBLIC 'Desktop\UVEX-ADV Manager.lnk'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\UVEX-ADV Manager.lnk')
)
foreach ($shortcutPath in $shortcutTargets) {
    if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force }
}
Write-Host 'OpenAstroSpec Auto — UVEX4 service and manager shortcuts removed. Legacy ProgramData configuration, logs and database were preserved.'
