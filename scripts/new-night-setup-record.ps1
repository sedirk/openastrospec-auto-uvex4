[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$DefinitionPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f-]{64,95}$')][string]$DefinitionSha256,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'commissioning-tool-utils.ps1')
$arguments = @(
    'night-setup', 'create',
    '--definition', (Resolve-Path -LiteralPath $DefinitionPath).Path,
    '--definition-sha', $DefinitionSha256
)
if ($OutputPath) { $arguments += @('--output', $OutputPath) }
Invoke-UvexCommissioningTool -Arguments $arguments
