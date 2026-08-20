[CmdletBinding(DefaultParameterSetName = 'NightSetup')]
param(
    [Parameter(Mandatory, ParameterSetName = 'NightSetup')][switch]$NightSetup,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][switch]$Phd2,
    [Parameter(Mandatory, ParameterSetName = 'Commissioning')][switch]$Commissioning,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$InputPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f-]{64,95}$')][string]$Sha256,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][ValidateRange(0, [int]::MaxValue)][int]$ProfileId,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][string]$ProfileName,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][string]$CameraName,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][string]$CameraStableId,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][string]$MountName,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][int]$Binning,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][int]$GainPercent,
    [Parameter(Mandatory, ParameterSetName = 'Phd2')][string]$ProfileEvidenceSha256,
    [Parameter(Mandatory, ParameterSetName = 'Commissioning')][string]$DefinitionPath,
    [Parameter(Mandatory, ParameterSetName = 'Commissioning')][string]$DefinitionSha256,
    [Parameter(Mandatory, ParameterSetName = 'Commissioning')][string]$NightSetupPath,
    [Parameter(Mandatory, ParameterSetName = 'Commissioning')][string]$NightSetupSha256,
    [Parameter(Mandatory, ParameterSetName = 'Commissioning')][string]$Phd2EvidencePath,
    [Parameter(Mandatory, ParameterSetName = 'Commissioning')][string]$Phd2EvidenceFileSha256,
    [Parameter(Mandatory, ParameterSetName = 'Commissioning')][string]$Phd2ProfileEvidenceSha256
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'commissioning-tool-utils.ps1')
$input = (Resolve-Path -LiteralPath $InputPath).Path
if ($PSCmdlet.ParameterSetName -eq 'NightSetup') {
    Invoke-UvexCommissioningTool -Arguments @('night-setup', 'validate', '--input', $input, '--sha', $Sha256)
    return
}
if ($PSCmdlet.ParameterSetName -eq 'Phd2') {
    Invoke-UvexCommissioningTool -Arguments @(
        'phd2', 'validate', '--input', $input, '--file-sha', $Sha256, '--profile-sha', $ProfileEvidenceSha256,
        '--profile-id', $ProfileId.ToString([Globalization.CultureInfo]::InvariantCulture), '--profile-name', $ProfileName,
        '--camera-name', $CameraName, '--camera-stable-id', $CameraStableId, '--mount-name', $MountName,
        '--binning', $Binning.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--gain-percent', $GainPercent.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    return
}
Invoke-UvexCommissioningTool -Arguments @(
    'commissioning', 'validate', '--input', $input, '--sha', $Sha256,
    '--definition', (Resolve-Path -LiteralPath $DefinitionPath).Path, '--definition-sha', $DefinitionSha256,
    '--night-setup', (Resolve-Path -LiteralPath $NightSetupPath).Path, '--night-setup-sha', $NightSetupSha256,
    '--phd2-evidence', (Resolve-Path -LiteralPath $Phd2EvidencePath).Path,
    '--phd2-evidence-file-sha', $Phd2EvidenceFileSha256, '--phd2-profile-sha', $Phd2ProfileEvidenceSha256
)
