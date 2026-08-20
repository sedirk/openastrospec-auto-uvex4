#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [switch]$ResetConfiguration,
    [switch]$PreflightOnly,
    [string]$LeaseWriterSid = ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $root 'artifacts\phd2-watchdog'
}

$serviceName = 'UVEX-ADV-PHD2-WATCHDOG'
$executableName = 'UvexAdv.Phd2.Watchdog.exe'
$requiredFiles = @(
    $executableName,
    'UvexAdv.Phd2.Watchdog.dll',
    'UvexAdv.Phd2.dll',
    'watchdog.default.json'
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $ArtifactDirectory $requiredFile) -PathType Leaf)) {
        throw "The PHD2 watchdog artifact is incomplete or stale (missing $requiredFile). Run scripts\build.ps1 first."
    }
}

function Test-WatchdogConfiguration {
    param([Parameter(Mandatory)][string]$Path)

    $configuration = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([int]$configuration.schemaVersion -ne 1) { throw "Unsupported watchdog configuration schema in '$Path'." }
    if ([string]$configuration.phd2Host -cne '127.0.0.1') { throw "Watchdog PHD2 host must be exactly 127.0.0.1 in '$Path'." }
    if ([int]$configuration.phd2Port -ne 4400) { throw "Watchdog PHD2 port must be exactly 4400 in '$Path'." }
    $poll = [int]$configuration.pollIntervalMilliseconds
    if ($poll -lt 100 -or $poll -gt 10000) { throw "Watchdog poll interval must be 100-10000 ms in '$Path'." }
    $stopRetry = [int]$configuration.stopFailureRetryMilliseconds
    if ($stopRetry -lt 1000 -or $stopRetry -gt 60000) {
        throw "Watchdog stop-failure retry interval must be 1000-60000 ms in '$Path'."
    }
    $statusPublish = [int]$configuration.statusPublishIntervalMilliseconds
    if ($statusPublish -lt 1000 -or $statusPublish -gt 300000) {
        throw "Watchdog status-publish interval must be 1000-300000 ms in '$Path'."
    }
    if ([string]$configuration.leaseFileName -cne 'lease.json') {
        throw "Watchdog leaseFileName must be exactly lease.json in '$Path'."
    }
    if ([string]$configuration.statusFileName -cne 'status.json') {
        throw "Watchdog statusFileName must be exactly status.json in '$Path'."
    }
}

function Set-AtomicFileFromTemplate {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $temporary = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $inputStream = [IO.File]::OpenRead($Source)
        try {
            $outputStream = [IO.FileStream]::new(
                $temporary,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None,
                4096,
                [IO.FileOptions]::WriteThrough)
            try {
                $inputStream.CopyTo($outputStream)
                $outputStream.Flush($true)
            }
            finally { $outputStream.Dispose() }
        }
        finally { $inputStream.Dispose() }

        if (Test-Path -LiteralPath $Destination) {
            [IO.File]::Replace($temporary, $Destination, $null)
        }
        else {
            [IO.File]::Move($temporary, $Destination)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Invoke-ServiceControl {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($Arguments[0]) failed with exit code $LASTEXITCODE`n$($output -join [Environment]::NewLine)"
    }
    $output
}

try { $null = [Security.Principal.SecurityIdentifier]::new($LeaseWriterSid) }
catch { throw "LeaseWriterSid '$LeaseWriterSid' is not a valid Windows SID." }

$defaultConfiguration = Join-Path $ArtifactDirectory 'watchdog.default.json'
Test-WatchdogConfiguration -Path $defaultConfiguration
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'UVEX-ADV'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $installRoot 'Phd2Watchdog'))
if (-not $installDirectory.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace PHD2 watchdog binaries outside the UVEX-ADV install root: $installDirectory"
}
$dataRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'UVEX-ADV\phd2-safety'))
$configurationPath = Join-Path $dataRoot 'config.json'
if (Test-Path -LiteralPath $configurationPath) {
    Test-WatchdogConfiguration -Path $configurationPath
}
if ($PreflightOnly) {
    Write-Host 'PHD2 watchdog preflight passed. No files, services, PHD2 connections, or hardware state were changed.'
    return
}

# Verify a complete binary tree before stopping an existing watchdog instance.
$stagingDirectory = [IO.Path]::GetFullPath((Join-Path $installRoot ('.Phd2Watchdog.installing-' + [Guid]::NewGuid().ToString('N'))))
if (-not $stagingDirectory.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage the PHD2 watchdog outside the UVEX-ADV install root: $stagingDirectory"
}
New-Item -ItemType Directory -Force $stagingDirectory | Out-Null
try {
    Copy-Item -Path (Join-Path $ArtifactDirectory '*') -Destination $stagingDirectory -Recurse -Force
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $requiredFile) -PathType Leaf)) {
            throw "Staged PHD2 watchdog deployment is incomplete: $requiredFile"
        }
    }

    New-Item -ItemType Directory -Force $dataRoot | Out-Null
    if ($ResetConfiguration -or -not (Test-Path -LiteralPath $configurationPath)) {
        Set-AtomicFileFromTemplate `
            -Source (Join-Path $stagingDirectory 'watchdog.default.json') `
            -Destination $configurationPath
    }
    Test-WatchdogConfiguration -Path $configurationPath

    # LocalService owns the watchdog/status; the explicitly selected N.I.N.A.
    # user owns heartbeat renewal. No broad network-service identity is used.
    # icacls resolves an unprefixed SID as an account name on localized
    # Windows installations. Prefix every literal SID with `*` so both the
    # service identity and the selected N.I.N.A. heartbeat writer are applied
    # as SIDs without a locale-dependent name lookup.
    $aclOutput = & icacls.exe $dataRoot /grant '*S-1-5-19:(OI)(CI)M' "*$LeaseWriterSid`:(OI)(CI)M" /T 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to grant watchdog ProgramData access`n$($aclOutput -join [Environment]::NewLine)"
    }

    # The executable's validation path is read-only and never starts the host
    # or opens a PHD2 socket.
    & (Join-Path $stagingDirectory $executableName) --validate-config
    if ($LASTEXITCODE -ne 0) { throw 'The staged watchdog rejected the machine configuration.' }

    $existing = Get-Service $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') {
            Stop-Service $serviceName -Force
            $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        Invoke-ServiceControl -Arguments @('delete', $serviceName) | Out-Null
        $existing.Dispose()
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ((Get-Service $serviceName -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
        }
        if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
            throw "Timed out waiting for $serviceName deletion."
        }
    }

    if (Test-Path -LiteralPath $installDirectory) {
        Remove-Item -LiteralPath $installDirectory -Recurse -Force
    }
    Move-Item -LiteralPath $stagingDirectory -Destination $installDirectory
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

$executable = Join-Path $installDirectory $executableName
$quotedExecutable = '"' + $executable + '"'
Invoke-ServiceControl -Arguments @(
    'create', $serviceName,
    'binPath=', $quotedExecutable,
    'start=', 'auto',
    'obj=', 'NT AUTHORITY\LocalService',
    'DisplayName=', 'OpenAstroSpec Auto — UVEX4 PHD2 Safety Watchdog'
) | Out-Null
Invoke-ServiceControl -Arguments @(
    'description', $serviceName,
    'Independent expired-lease watchdog; may only confirm-stop PHD2 at 127.0.0.1:4400.'
) | Out-Null
Invoke-ServiceControl -Arguments @(
    'failure', $serviceName,
    'reset=', '86400',
    'actions=', 'restart/5000/restart/15000/restart/60000'
) | Out-Null
Start-Service $serviceName
$installed = Get-Service $serviceName
$installed.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

$effectiveConfiguration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
$statusPath = Join-Path $dataRoot ([string]$effectiveConfiguration.statusFileName)
$statusDeadline = [DateTime]::UtcNow.AddSeconds(15)
while (-not (Test-Path -LiteralPath $statusPath) -and [DateTime]::UtcNow -lt $statusDeadline) {
    Start-Sleep -Milliseconds 250
}
if (-not (Test-Path -LiteralPath $statusPath)) {
    Write-Warning "The service is Running but did not publish $statusPath within 15 seconds. Check Windows Event Log."
}

Write-Host "Installed $serviceName with automatic Windows startup."
Write-Host 'Network invariant: no listener; outbound PHD2 access is pinned to 127.0.0.1:4400.'
Write-Host "Lease/config/status root: $dataRoot"
Write-Host "Lease heartbeat writer SID: $LeaseWriterSid"
Write-Host "Read health: & '$executable' --status"
