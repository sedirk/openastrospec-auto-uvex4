[CmdletBinding()]
param([switch] $Quiet)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'coordinate-command-safety.psm1'
Import-Module $modulePath -Force

function Assert-True {
    param([Parameter(Mandatory)][bool] $Condition, [Parameter(Mandatory)][string] $Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock] $Action,
        [Parameter(Mandatory)][string] $MessageFragment
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message.IndexOf($MessageFragment, [StringComparison]::Ordinal) -lt 0) {
            throw "Expected failure containing '$MessageFragment', got '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected failure containing '$MessageFragment', but the action passed."
}

function New-ValidEnvelope {
    $json = @'
{
  "Schema": "uvex-adv.nina-coordinate-transform.v1",
  "Input": { "RaDegrees": 310.773, "DecDegrees": 45.02269, "Epoch": "JNOW" },
  "J2000": { "RaDegrees": 310.35798, "DecDegrees": 45.280339, "Epoch": "J2000" },
  "RoundTrip": { "RaDegrees": 310.773, "DecDegrees": 45.02269, "Epoch": "JNOW", "ErrorArcseconds": 0.001 }
}
'@
    return $json | ConvertFrom-Json
}

$hash = 'A' * 64
$coordinate = ConvertFrom-UvexNinaCoordinateTransform -Envelope (New-ValidEnvelope) -TransformOutputSha256 $hash
Assert-True ($coordinate.RightAscensionDegrees -eq 310.35798) 'Validated RA was not preserved.'
Assert-True ($coordinate.DeclinationDegrees -eq 45.280339) 'Validated Dec was not preserved.'
Assert-True ($coordinate.Epoch -ceq 'J2000') 'Validated epoch was not preserved.'

# This is the exact PowerShell coercion that contributed to the incident. The
# guard must reject the missing field before any numeric conversion can occur.
Assert-True ([double]$null -eq 0) 'The regression test no longer reproduces PowerShell null-to-zero coercion.'
$missingDec = New-ValidEnvelope
$missingDec.J2000.PSObject.Properties.Remove('DecDegrees')
$missingDec.J2000 | Add-Member -NotePropertyName Dec -NotePropertyValue 45.280339
Assert-Throws {
    ConvertFrom-UvexNinaCoordinateTransform -Envelope $missingDec -TransformOutputSha256 $hash
} '$.J2000.DecDegrees'

$nullDec = New-ValidEnvelope
$nullDec.J2000.DecDegrees = $null
Assert-Throws {
    ConvertFrom-UvexNinaCoordinateTransform -Envelope $nullDec -TransformOutputSha256 $hash
} '$.J2000.DecDegrees'

$stringDec = New-ValidEnvelope
$stringDec.J2000.DecDegrees = '45.280339'
Assert-Throws {
    ConvertFrom-UvexNinaCoordinateTransform -Envelope $stringDec -TransformOutputSha256 $hash
} 'must be a JSON number'

$nanDec = New-ValidEnvelope
$nanDec.J2000.DecDegrees = [double]::NaN
Assert-Throws {
    ConvertFrom-UvexNinaCoordinateTransform -Envelope $nanDec -TransformOutputSha256 $hash
} 'must be finite'

$outOfRangeDec = New-ValidEnvelope
$outOfRangeDec.J2000.DecDegrees = 90.0001
Assert-Throws {
    ConvertFrom-UvexNinaCoordinateTransform -Envelope $outOfRangeDec -TransformOutputSha256 $hash
} 'must be in [-90, 90]'

$wrongEpoch = New-ValidEnvelope
$wrongEpoch.J2000.Epoch = 'JNOW'
Assert-Throws {
    ConvertFrom-UvexNinaCoordinateTransform -Envelope $wrongEpoch -TransformOutputSha256 $hash
} "must be exactly 'J2000'"

$badRoundTrip = New-ValidEnvelope
$badRoundTrip.RoundTrip.ErrorArcseconds = 0.051
Assert-Throws {
    ConvertFrom-UvexNinaCoordinateTransform -Envelope $badRoundTrip -TransformOutputSha256 $hash
} 'exceeds 0.05 arcsec'

$priorCulture = [Globalization.CultureInfo]::CurrentCulture
try {
    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('fr-FR')
    $operationId = [guid]'11111111-2222-3333-4444-555555555555'
    $first = New-UvexNinaSlewRequestPreview -Coordinate $coordinate -OperationId $operationId
    $second = New-UvexNinaSlewRequestPreview -Coordinate $coordinate -OperationId $operationId
}
finally {
    [Globalization.CultureInfo]::CurrentCulture = $priorCulture
}

$expectedUri = 'http://127.0.0.1:1888/v2/api/equipment/mount/slew?ra=310.35798&dec=45.280339&waitForResult=true&center=false&rotate=false&rotationAngle=0'
Assert-True ($first.RequestUri.AbsoluteUri -ceq $expectedUri) 'Request preview is not canonical or culture invariant.'
Assert-True ($first.RequestSha256 -ceq $second.RequestSha256) 'Request hash is not deterministic.'
Assert-True ($first.IntentSha256 -ceq $second.IntentSha256) 'Intent hash is not deterministic.'
Assert-True ($first.RequestSha256 -match '^[0-9A-F]{64}$') 'Request hash is not uppercase SHA-256.'
Assert-True (-not $first.AutomaticRetryAllowed) 'Ambiguous mount requests must never enable automatic retry.'
Assert-Throws {
    New-UvexNinaSlewRequestPreview -Coordinate $coordinate -OperationId ([guid]::Empty)
} 'non-empty GUID'
Assert-Throws {
    New-UvexNinaSlewRequestPreview -Coordinate $coordinate -OperationId ([guid]::NewGuid()) -Endpoint 'http://localhost:1888/v2/api/equipment/mount/slew'
} 'must be exactly'

# Any future tracked PowerShell file that directly constructs the N.I.N.A. mount
# slew endpoint must opt into strict mode and the canonical preview guard. The
# guard and its offline test are excluded because they never dispatch a request.
$excluded = @(
    [IO.Path]::GetFullPath($modulePath),
    [IO.Path]::GetFullPath($PSCommandPath)
)
$coordinateCommandFiles = Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object {
    $_.Extension -in '.ps1', '.psm1' -and
    [IO.Path]::GetFullPath($_.FullName) -notin $excluded -and
    (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)equipment/mount/slew'
}
foreach ($file in $coordinateCommandFiles) {
    $source = Get-Content -LiteralPath $file.FullName -Raw
    Assert-True ($source -match '(?m)^Set-StrictMode\s+-Version\s+Latest\s*$') "$($file.Name) constructs a mount request without Set-StrictMode -Version Latest."
    Assert-True ($source -match '\bNew-UvexNinaSlewRequestPreview\b') "$($file.Name) constructs a mount request without a canonical hashed preview."
    Assert-True ($source -notmatch '\[(double|single|decimal)\]\s*\$[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+') "$($file.Name) directly casts a dynamic property to a number."
}

$moduleSource = Get-Content -LiteralPath $modulePath -Raw
Assert-True ($moduleSource -notmatch 'Invoke-(RestMethod|WebRequest)') 'The request-preview module must never dispatch hardware requests.'
Assert-True ($moduleSource -notmatch '\[Net\.HttpWebRequest\]') 'The request-preview module must never create hardware requests.'

if (-not $Quiet) {
    Write-Host 'Coordinate-command safety tests passed (offline; no request was dispatched).'
}
