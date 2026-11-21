[CmdletBinding()]
param(
    [DateTimeOffset]$At = [DateTimeOffset]::Now,
    [ValidateRange(1,60)][int]$MinutesBefore = 5,
    [ValidateRange(1,30)][int]$MinutesAfter = 2
)

# Offline evidence only. Never instantiate ASCOM, open a serial port, connect
# N.I.N.A. equipment, or change USB / event-log settings. PresentNow is not
# a historical claim; collect it before touching power if the site is safe.
$from = $At.AddMinutes(-$MinutesBefore).LocalDateTime
$to = $At.AddMinutes($MinutesAfter).LocalDateTime
$profile = 'HKLM:\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\RRCI.Dome'
$port = (Get-ItemProperty -LiteralPath $profile -Name 'Com Port' -ErrorAction SilentlyContinue).'Com Port'
$serialMap = Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM' -ErrorAction SilentlyContinue
$configuredPortListedNow = if ($port -and $serialMap) {
    @($serialMap.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' -and $_.Value -eq $port }).Count -gt 0
} else { $null }

$channels = @('System','Microsoft-Windows-Kernel-PnP/Device Management','Microsoft-Windows-Kernel-PnP/Configuration',
    'Microsoft-Windows-Kernel-PnP/Driver Watchdog','Microsoft-Windows-USB-USBHUB3/Operational',
    'Microsoft-Windows-USB-USBXHCI/Operational','Microsoft-Windows-DriverFrameworks-UserMode/Operational')
$windows = @(foreach ($channel in $channels) {
    $meta = Get-WinEvent -ListLog $channel -ErrorAction SilentlyContinue
    $errorReading = $null
    $events = @()
    if ($meta -and $meta.IsEnabled) {
        try {
            $events = @(Get-WinEvent -FilterHashtable @{ LogName=$channel; StartTime=$from; EndTime=$to } -ErrorAction Stop |
                Where-Object { $_.ProviderName -match 'USB|Kernel-PnP' -or $_.Message -match 'USB\\VID_|CH340|RRCI' } |
                Sort-Object TimeCreated | ForEach-Object {
                    [pscustomobject]@{ Time=$_.TimeCreated.ToString('o'); Id=$_.Id; Provider=$_.ProviderName; Message=($_.Message -replace '\s+',' ').Trim() }
                })
        } catch {
            if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { $errorReading = $_.Exception.Message }
        }
    }
    [pscustomobject]@{ Channel=$channel; Enabled=$(if ($meta) {$meta.IsEnabled}); ReadError=$errorReading; Events=$events }
})

$nina = Join-Path $env:LOCALAPPDATA 'NINA\Logs'
$pattern = 'RRCI|DomeVM\|(?:ChooseDome|Disconnect)|UsbDeviceWatcher_Device(?:Inserted|Removed)|ROOF_(?:ADAPTER|STATE|POSITION|TRANSITION)'
$ninaLines = @(foreach ($file in Get-ChildItem -LiteralPath $nina -File -Filter '*.log' -ErrorAction SilentlyContinue |
    Where-Object {$_.LastWriteTime -ge $from} | Sort-Object Name) {
    # N.I.N.A.'s rolling log filename starts with its local process-start time.
    $start = [DateTime]::MinValue
    if ($file.Name -match '^([0-9]{8}-[0-9]{6})-' -and
        [DateTime]::TryParseExact($Matches[1], 'yyyyMMdd-HHmmss', [cultureinfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None, [ref]$start) -and $start -gt $to) { continue }
    foreach ($match in Select-String -LiteralPath $file.FullName -Pattern $pattern -ErrorAction SilentlyContinue) {
        $line = $match.Line
        if ($line -notmatch '^(20[0-9-]+T[0-9:.]+)\|') { continue }
        $stamp = [DateTime]::Parse($Matches[1], [cultureinfo]::InvariantCulture)
        if ($stamp -ge $from -and $stamp -le $to) {
            [pscustomobject]@{ File=$file.Name; Line=$match.LineNumber; Time=$stamp.ToString('o'); Message=$line }
        }
    }
})
$legacyLog = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDocuments)) 'RRCI\RRCI_Error_Log.txt'
$legacy = Get-Item -LiteralPath $legacyLog -ErrorAction SilentlyContinue
[pscustomobject]@{
    WindowStartLocal=$from.ToString('o'); WindowEndLocal=$to.ToString('o')
    PresentSampleAt=[DateTimeOffset]::Now.ToString('o'); ConfiguredLegacyRrciPort=$port; ConfiguredPortListedNow=$configuredPortListedNow
    RrciLog=$legacyLog; RrciLogLastWrite=$(if ($legacy) {$legacy.LastWriteTime.ToString('o')})
    Windows=$windows; Nina=@($ninaLines | Sort-Object Time)
}
