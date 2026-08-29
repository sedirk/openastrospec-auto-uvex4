#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AllInOneInstallerPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$AllInOneSha256,
    [Parameter(Mandatory)][string]$SharedSdkArchivePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$SharedSdkArchiveSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$SharedSdkDllSha256,
    [string]$HardwareConfigurationPath = 'C:\ProgramData\UVEX-ADV\qhy\appsettings.json',
    [string]$ServiceArtifactDirectory,
    [string]$VendorSdkRoot = 'C:\Program Files\QHYCCD\AllInOne\sdk',
    [switch]$AllowUnsignedOfficialInstaller,
    [switch]$ResumeAfterVerifiedAllInOne
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ServiceArtifactDirectory)) {
    $ServiceArtifactDirectory = Join-Path $root 'artifacts\qhy-service'
}

function Resolve-RequiredFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Description)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Description was not found at '$resolved'."
    }
    $resolved
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Description)

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not $actual.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description SHA-256 mismatch. Expected $Expected, received $actual."
    }
    $actual
}

$installer = Resolve-RequiredFile $AllInOneInstallerPath 'QHY AllInOne installer'
$sdkArchive = Resolve-RequiredFile $SharedSdkArchivePath 'QHY shared SDK archive'
$configuration = Resolve-RequiredFile $HardwareConfigurationPath 'QHY machine configuration'
$artifactExecutable = Resolve-RequiredFile (Join-Path $ServiceArtifactDirectory 'UvexAdv.Qhy.Service.exe') 'QHY service artifact'
[void]$artifactExecutable

$installerHash = Assert-Sha256 $installer $AllInOneSha256 'QHY AllInOne installer'
$sdkArchiveHash = Assert-Sha256 $sdkArchive $SharedSdkArchiveSha256 'QHY shared SDK archive'
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -and -not $AllowUnsignedOfficialInstaller) {
    throw "QHY AllInOne installer Authenticode status is '$($signature.Status)'. Re-run only after verifying the official source and explicitly pass -AllowUnsignedOfficialInstaller."
}

$presentQhy = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match '^USB\\VID_1618&PID_058[78]' }
if ($presentQhy) {
    $ids = ($presentQhy.InstanceId -join ', ')
    throw "QHY camera is still present ($ids). Turn off or physically disconnect QHYminiCam8M before installing the complete driver package."
}
if (Get-Process -Name NINA -ErrorAction SilentlyContinue) {
    throw 'N.I.N.A. is still running. Close it before updating the complete QHY driver/SDK installation.'
}

$maintenanceRoot = [IO.Path]::GetFullPath('C:\ProgramData\UVEX-ADV\qhy\maintenance')
$runId = 'qhy-allinone-update-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$runDirectory = [IO.Path]::GetFullPath((Join-Path $maintenanceRoot $runId))
if (-not $runDirectory.StartsWith($maintenanceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write QHY maintenance evidence outside '$maintenanceRoot'."
}
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$installerLog = Join-Path $runDirectory 'allinone-installer.log'
$configurationBackup = Join-Path $runDirectory 'appsettings.before.json'
Copy-Item -LiteralPath $configuration -Destination $configurationBackup

$vendorSdkRootFull = [IO.Path]::GetFullPath($VendorSdkRoot)
$vendorInstallRoot = [IO.Path]::GetFullPath('C:\Program Files\QHYCCD\AllInOne')
if (-not $vendorSdkRootFull.StartsWith($vendorInstallRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to update a vendor SDK outside '$vendorInstallRoot'."
}
$sdkBackup = Join-Path $runDirectory 'vendor-sdk.before'
if (Test-Path -LiteralPath $vendorSdkRootFull) {
    Copy-Item -LiteralPath $vendorSdkRootFull -Destination $sdkBackup -Recurse
}
$privateSdk = 'C:\Program Files\UVEX-ADV\QhyService\native\qhyccd.dll'
$privateSdkBefore = if (Test-Path -LiteralPath $privateSdk) {
    [ordered]@{
        Path = $privateSdk
        Version = (Get-Item -LiteralPath $privateSdk).VersionInfo.FileVersion
        Sha256 = (Get-FileHash -LiteralPath $privateSdk -Algorithm SHA256).Hash
    }
} else { $null }

$service = Get-Service -Name 'UVEX-ADV-QHY' -ErrorAction SilentlyContinue
$serviceWasRunning = $service -and $service.Status -ne 'Stopped'
$configurationChanged = $false
try {
    if ($serviceWasRunning) {
        Stop-Service -Name 'UVEX-ADV-QHY' -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    if ($ResumeAfterVerifiedAllInOne) {
        $expectedVersion = (Get-Item -LiteralPath $installer).VersionInfo.ProductVersion.Trim()
        $registeredPackage = Get-ItemProperty `
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', `
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.DisplayName -like 'QHYCCD_Win_AllInOne*' -and
                [string]$_.DisplayVersion -eq $expectedVersion
            } |
            Select-Object -First 1
        if (-not $registeredPackage) {
            throw "Refusing AllInOne resume: installed registration does not prove version '$expectedVersion'."
        }
        Set-Content -LiteralPath $installerLog -Encoding utf8 -Value `
            "AllInOne execution skipped only after verifying installed registration version $expectedVersion."
    } else {
        $installerArguments = @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/CLOSEAPPLICATIONS',
            ('/LOG="' + $installerLog + '"')
        )
        $installerProcess = Start-Process -FilePath $installer -ArgumentList $installerArguments -Wait -PassThru
        if ($installerProcess.ExitCode -ne 0) {
            throw "QHY AllInOne installer exited with code $($installerProcess.ExitCode)."
        }
    }

    $sdkStage = Join-Path $runDirectory 'shared-sdk-stage'
    Expand-Archive -LiteralPath $sdkArchive -DestinationPath $sdkStage
    $sdkDllCandidates = @(Get-ChildItem -LiteralPath $sdkStage -Recurse -File -Filter 'qhyccd.dll' |
        Where-Object { $_.Directory.Name -eq 'x64' })
    if ($sdkDllCandidates.Count -ne 1) {
        throw "Expected one x64 qhyccd.dll in the official SDK archive, found $($sdkDllCandidates.Count)."
    }
    $stagedDll = $sdkDllCandidates[0]
    $sdkDllHash = Assert-Sha256 $stagedDll.FullName $SharedSdkDllSha256 'QHY shared x64 SDK DLL'
    $sdkPackageRoot = Split-Path $stagedDll.Directory.FullName -Parent
    New-Item -ItemType Directory -Force -Path $vendorSdkRootFull | Out-Null
    Copy-Item -Path (Join-Path $sdkPackageRoot '*') -Destination $vendorSdkRootFull -Recurse -Force

    $vendorSdkDll = Join-Path $vendorSdkRootFull 'x64\qhyccd.dll'
    $installedSdkHash = Assert-Sha256 $vendorSdkDll $SharedSdkDllSha256 'Installed shared QHY x64 SDK DLL'
    $machineConfiguration = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json
    if ($null -eq $machineConfiguration.Qhy -or [bool]$machineConfiguration.Qhy.Simulator) {
        throw "Machine configuration '$configuration' is not a real-hardware QHY configuration."
    }
    $machineConfiguration.Qhy.NativeSdkPath = $vendorSdkDll
    $machineConfiguration.Qhy.NativeSdkSha256 = $installedSdkHash
    $configurationTemp = Join-Path (Split-Path $configuration -Parent) ('.appsettings.updating-' + [Guid]::NewGuid().ToString('N') + '.json')
    $machineConfiguration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configurationTemp -Encoding utf8
    Move-Item -LiteralPath $configurationTemp -Destination $configuration -Force
    $configurationChanged = $true

    & (Join-Path $PSScriptRoot 'install-qhy-service.ps1') `
        -ArtifactDirectory $ServiceArtifactDirectory `
        -EnableHardware `
        -HardwareConfigurationPath $configuration `
        -VendorSdkDirectory (Join-Path $vendorSdkRootFull 'x64')

    if (Test-Path -LiteralPath $privateSdk) {
        throw "The updated QHY service still contains a private SDK at '$privateSdk'."
    }
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:47845/api/v1/health' -TimeoutSec 15
    $configurationValid =
        [string]$health.status -eq 'ok' -and
        -not [bool]$health.configuration.simulator -and
        [string]$health.configuration.adapter -eq 'qhy-native' -and
        [string]$health.configuration.nativeSdkSha256 -eq $installedSdkHash
    if (-not $configurationValid) {
        throw 'Updated QHY service health/configuration proof did not pass.'
    }

    $allInOneRegistration = Get-ItemProperty `
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', `
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'QHYCCD_Win_AllInOne*' } |
        Sort-Object DisplayVersion -Descending |
        Select-Object -First 1
    $evidence = [ordered]@{
        DocumentType = 'UVEX-ADV.QhyAllInOneUpdateEvidence'
        SchemaVersion = 1
        RunId = $runId
        CompletedUtc = [DateTimeOffset]::UtcNow
        CameraDisconnectedDuringInstallation = $true
        AllInOne = [ordered]@{
            InstallerPath = $installer
            Sha256 = $installerHash
            AuthenticodeStatus = [string]$signature.Status
            ResumedAfterVerifiedInstallation = [bool]$ResumeAfterVerifiedAllInOne
            RegisteredDisplayName = $allInOneRegistration.DisplayName
            RegisteredDisplayVersion = $allInOneRegistration.DisplayVersion
            InstallerLog = $installerLog
        }
        SharedSdk = [ordered]@{
            ArchivePath = $sdkArchive
            ArchiveSha256 = $sdkArchiveHash
            InstalledPath = $vendorSdkDll
            DllVersion = (Get-Item -LiteralPath $vendorSdkDll).VersionInfo.FileVersion
            DllSha256 = $sdkDllHash
        }
        PriorPrivateSdk = $privateSdkBefore
        PrivateSdkRemoved = -not (Test-Path -LiteralPath $privateSdk)
        Service = [ordered]@{
            Name = 'UVEX-ADV-QHY'
            Status = [string](Get-Service -Name 'UVEX-ADV-QHY').Status
            ConfigurationValid = $configurationValid
            NativeSdkPath = $vendorSdkDll
            NativeSdkSha256 = $health.configuration.nativeSdkSha256
        }
        FirmwareAction = 'No blind firmware flash; read firmware and FPGA versions after camera power-on.'
    }
    $evidencePath = Join-Path $runDirectory 'qhy-allinone-update-evidence.json'
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding utf8
    $evidenceHash = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
    Set-Content -LiteralPath ($evidencePath + '.sha256') -Value "$evidenceHash  $([IO.Path]::GetFileName($evidencePath))" -Encoding ascii
    Write-Host "QHY complete-package update succeeded. Evidence: $evidencePath"
}
catch {
    if ($configurationChanged -and (Test-Path -LiteralPath $configurationBackup)) {
        Copy-Item -LiteralPath $configurationBackup -Destination $configuration -Force
    }
    $existingService = Get-Service -Name 'UVEX-ADV-QHY' -ErrorAction SilentlyContinue
    if ($existingService -and $existingService.Status -eq 'Stopped') {
        try { Start-Service -Name 'UVEX-ADV-QHY' } catch { }
    }
    throw
}
