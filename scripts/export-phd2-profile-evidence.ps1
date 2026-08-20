[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(0, [int]::MaxValue)][int]$ProfileId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ProfileName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CameraName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CameraStableId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$MountName,
    [Parameter(Mandatory)][ValidateRange(1, 16)][int]$Binning,
    [Parameter(Mandatory)][ValidateRange(0, 100)][int]$GainPercent,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'commissioning-tool-utils.ps1')
$arguments = @(
    'phd2', 'export',
    '--profile-id', $ProfileId.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--profile-name', $ProfileName,
    '--camera-name', $CameraName,
    '--camera-stable-id', $CameraStableId,
    '--mount-name', $MountName,
    '--binning', $Binning.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--gain-percent', $GainPercent.ToString([Globalization.CultureInfo]::InvariantCulture)
)
if ($OutputPath) { $arguments += @('--output', $OutputPath) }
Invoke-UvexCommissioningTool -Arguments $arguments
