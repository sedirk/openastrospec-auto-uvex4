[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$DefinitionPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f-]{64,95}$')][string]$DefinitionSha256,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$NightSetupPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f-]{64,95}$')][string]$NightSetupSha256,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$Phd2EvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f-]{64,95}$')][string]$Phd2EvidenceFileSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f-]{64,95}$')][string]$Phd2ProfileEvidenceSha256,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'commissioning-tool-utils.ps1')
$definition = Get-Content -LiteralPath $DefinitionPath -Raw | ConvertFrom-Json
$requiredSchema4Inputs = @('FineMotionAuthority', 'Phd2SlitPlacement', 'GhostAssistanceMode', 'GhostAssistance', 'SlitWheelIdentity')
$definitionProperties = @($definition.PSObject.Properties.Name)
$missingSchema4Inputs = @($requiredSchema4Inputs | Where-Object { $_ -notin $definitionProperties })
if ($definition.SchemaVersion -ne 4) {
    throw 'Commissioning measurement definition schema 4 is required to generate a production schema 5 preset.'
}
if ($missingSchema4Inputs.Count -gt 0) {
    throw "Commissioning definition is missing explicit schema 4 input field(s): $($missingSchema4Inputs -join ', ')."
}
$ghostMode = [int]$definition.GhostAssistanceMode
if (($ghostMode -eq 0 -and $null -ne $definition.GhostAssistance) -or
    ($ghostMode -in 1, 2 -and $null -eq $definition.GhostAssistance)) {
    throw 'GhostAssistanceMode/payload mismatch: Skip requires null; Auto or RequireValid requires complete evidence.'
}
$arguments = @(
    'commissioning', 'create',
    '--definition', (Resolve-Path -LiteralPath $DefinitionPath).Path,
    '--definition-sha', $DefinitionSha256,
    '--night-setup', (Resolve-Path -LiteralPath $NightSetupPath).Path,
    '--night-setup-sha', $NightSetupSha256,
    '--phd2-evidence', (Resolve-Path -LiteralPath $Phd2EvidencePath).Path,
    '--phd2-evidence-file-sha', $Phd2EvidenceFileSha256,
    '--phd2-profile-sha', $Phd2ProfileEvidenceSha256
)
if ($OutputPath) { $arguments += @('--output', $OutputPath) }
Invoke-UvexCommissioningTool -Arguments $arguments
