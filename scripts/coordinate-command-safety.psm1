Set-StrictMode -Version Latest

if ($null -eq ('UvexAdv.Commissioning.ValidatedEquatorialCoordinate' -as [type])) {
    Add-Type -TypeDefinition @'
using System;

namespace UvexAdv.Commissioning
{
    public sealed class ValidatedEquatorialCoordinate
    {
        private readonly double rightAscensionDegrees;
        private readonly double declinationDegrees;
        private readonly string epoch;
        private readonly string transformSchema;
        private readonly string transformOutputSha256;

        public double RightAscensionDegrees { get { return rightAscensionDegrees; } }
        public double DeclinationDegrees { get { return declinationDegrees; } }
        public string Epoch { get { return epoch; } }
        public string TransformSchema { get { return transformSchema; } }
        public string TransformOutputSha256 { get { return transformOutputSha256; } }

        public ValidatedEquatorialCoordinate(
            double rightAscensionDegrees,
            double declinationDegrees,
            string epoch,
            string transformSchema,
            string transformOutputSha256)
        {
            if (double.IsNaN(rightAscensionDegrees) || double.IsInfinity(rightAscensionDegrees) ||
                rightAscensionDegrees < 0 || rightAscensionDegrees >= 360)
                throw new ArgumentOutOfRangeException("rightAscensionDegrees");
            if (double.IsNaN(declinationDegrees) || double.IsInfinity(declinationDegrees) ||
                declinationDegrees < -90 || declinationDegrees > 90)
                throw new ArgumentOutOfRangeException("declinationDegrees");
            if (!string.Equals(epoch, "J2000", StringComparison.Ordinal))
                throw new ArgumentException("Epoch must be exactly J2000.", "epoch");
            if (!string.Equals(transformSchema, "uvex-adv.nina-coordinate-transform.v1", StringComparison.Ordinal))
                throw new ArgumentException("Transform schema is invalid.", "transformSchema");
            if (string.IsNullOrWhiteSpace(transformOutputSha256) || transformOutputSha256.Length != 64)
                throw new ArgumentException("Transform output SHA-256 is required.", "transformOutputSha256");
            for (var index = 0; index < transformOutputSha256.Length; index++)
                if (!Uri.IsHexDigit(transformOutputSha256[index]))
                    throw new ArgumentException("Transform output SHA-256 must be hexadecimal.", "transformOutputSha256");

            this.rightAscensionDegrees = rightAscensionDegrees;
            this.declinationDegrees = declinationDegrees;
            this.epoch = epoch;
            this.transformSchema = transformSchema;
            this.transformOutputSha256 = transformOutputSha256;
        }
    }

    public sealed class NinaSlewRequestPreview
    {
        private readonly Guid operationId;
        private readonly ValidatedEquatorialCoordinate coordinate;
        private readonly Uri requestUri;
        private readonly string requestSha256;
        private readonly string intentSha256;

        public Guid OperationId { get { return operationId; } }
        public ValidatedEquatorialCoordinate Coordinate { get { return coordinate; } }
        public Uri RequestUri { get { return requestUri; } }
        public string RequestSha256 { get { return requestSha256; } }
        public string IntentSha256 { get { return intentSha256; } }
        public bool AutomaticRetryAllowed { get { return false; } }
        public string AcceptancePolicy
        {
            get
            {
                return "One dispatch only; an ambiguous response is reconciled from a fresh mount readback, never retried automatically.";
            }
        }

        public NinaSlewRequestPreview(
            Guid operationId,
            ValidatedEquatorialCoordinate coordinate,
            Uri requestUri,
            string requestSha256,
            string intentSha256)
        {
            if (operationId == Guid.Empty) throw new ArgumentException("Operation ID is required.", "operationId");
            if (coordinate == null) throw new ArgumentNullException("coordinate");
            if (requestUri == null) throw new ArgumentNullException("requestUri");
            if (requestSha256 == null) throw new ArgumentNullException("requestSha256");
            if (intentSha256 == null) throw new ArgumentNullException("intentSha256");
            this.operationId = operationId;
            this.coordinate = coordinate;
            this.requestUri = requestUri;
            this.requestSha256 = requestSha256;
            this.intentSha256 = intentSha256;
        }
    }
}
'@
}

function Get-UvexRequiredPropertyValue {
    param(
        [Parameter(Mandatory)][object] $InputObject,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Path
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Coordinate transform output is missing required property '$Path.$Name'."
    }
    if ($property.Name -cne $Name) {
        throw "Coordinate transform property '$Path.$($property.Name)' must use exact field name '$Name'."
    }
    if ($null -eq $property.Value) {
        throw "Coordinate transform property '$Path.$Name' is null."
    }
    return $property.Value
}

function ConvertTo-UvexFiniteDouble {
    param(
        [Parameter(Mandatory)][object] $Value,
        [Parameter(Mandatory)][string] $Path
    )

    $isNumeric =
        $Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64] -or
        $Value -is [single] -or $Value -is [double] -or $Value -is [decimal]
    if (-not $isNumeric) {
        throw "Coordinate transform property '$Path' must be a JSON number, not '$($Value.GetType().FullName)'."
    }

    $number = [Convert]::ToDouble($Value, [Globalization.CultureInfo]::InvariantCulture)
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) {
        throw "Coordinate transform property '$Path' must be finite."
    }
    return $number
}

function Get-UvexValidatedCoordinateNode {
    param(
        [Parameter(Mandatory)][object] $Node,
        [Parameter(Mandatory)][string] $Path,
        [string] $RequiredEpoch = ''
    )

    $raValue = Get-UvexRequiredPropertyValue -InputObject $Node -Name 'RaDegrees' -Path $Path
    $ra = ConvertTo-UvexFiniteDouble -Value $raValue -Path "$Path.RaDegrees"
    $decValue = Get-UvexRequiredPropertyValue -InputObject $Node -Name 'DecDegrees' -Path $Path
    $dec = ConvertTo-UvexFiniteDouble -Value $decValue -Path "$Path.DecDegrees"
    $epoch = [string](Get-UvexRequiredPropertyValue -InputObject $Node -Name 'Epoch' -Path $Path)

    if ($ra -lt 0 -or $ra -ge 360) {
        throw "Coordinate transform property '$Path.RaDegrees' must be in [0, 360)."
    }
    if ($dec -lt -90 -or $dec -gt 90) {
        throw "Coordinate transform property '$Path.DecDegrees' must be in [-90, 90]."
    }
    if ([string]::IsNullOrWhiteSpace($epoch)) {
        throw "Coordinate transform property '$Path.Epoch' is empty."
    }
    if (-not [string]::IsNullOrWhiteSpace($RequiredEpoch) -and $epoch -cne $RequiredEpoch) {
        throw "Coordinate transform property '$Path.Epoch' must be exactly '$RequiredEpoch', not '$epoch'."
    }

    return [pscustomobject]@{ RaDegrees = $ra; DecDegrees = $dec; Epoch = $epoch }
}

function ConvertFrom-UvexNinaCoordinateTransform {
    <#
    .SYNOPSIS
        Validates the complete output of the N.I.N.A. coordinate-transform helper.

    .DESCRIPTION
        This is deliberately not a permissive PowerShell cast. Every property is
        checked for existence before conversion, all coordinates are finite and
        in range, J2000 is exact, and the helper's round-trip error must pass.
    #>
    [CmdletBinding()]
    [OutputType([UvexAdv.Commissioning.ValidatedEquatorialCoordinate])]
    param(
        [Parameter(Mandatory)][object] $Envelope,
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string] $TransformOutputSha256,
        [ValidateRange(0.000001, 1.0)][double] $MaximumRoundTripErrorArcseconds = 0.05
    )

    $schema = [string](Get-UvexRequiredPropertyValue -InputObject $Envelope -Name 'Schema' -Path '$')
    if ($schema -cne 'uvex-adv.nina-coordinate-transform.v1') {
        throw "Coordinate transform schema must be exactly 'uvex-adv.nina-coordinate-transform.v1', not '$schema'."
    }

    $inputNode = Get-UvexRequiredPropertyValue -InputObject $Envelope -Name 'Input' -Path '$'
    $j2000Node = Get-UvexRequiredPropertyValue -InputObject $Envelope -Name 'J2000' -Path '$'
    $roundTripNode = Get-UvexRequiredPropertyValue -InputObject $Envelope -Name 'RoundTrip' -Path '$'
    $input = Get-UvexValidatedCoordinateNode -Node $inputNode -Path '$.Input'
    $j2000 = Get-UvexValidatedCoordinateNode -Node $j2000Node -Path '$.J2000' -RequiredEpoch 'J2000'
    $roundTrip = Get-UvexValidatedCoordinateNode -Node $roundTripNode -Path '$.RoundTrip' -RequiredEpoch $input.Epoch
    $roundTripErrorValue = Get-UvexRequiredPropertyValue -InputObject $roundTripNode -Name 'ErrorArcseconds' -Path '$.RoundTrip'
    $roundTripError = ConvertTo-UvexFiniteDouble -Value $roundTripErrorValue -Path '$.RoundTrip.ErrorArcseconds'
    if ($roundTripError -lt 0 -or $roundTripError -gt $MaximumRoundTripErrorArcseconds) {
        throw "Coordinate transform round-trip error $roundTripError arcsec exceeds $MaximumRoundTripErrorArcseconds arcsec."
    }

    return [UvexAdv.Commissioning.ValidatedEquatorialCoordinate]::new(
        $j2000.RaDegrees,
        $j2000.DecDegrees,
        $j2000.Epoch,
        $schema,
        $TransformOutputSha256.ToUpperInvariant())
}

function Get-UvexSha256Text {
    param([Parameter(Mandatory)][string] $Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function New-UvexNinaSlewRequestPreview {
    <#
    .SYNOPSIS
        Creates a canonical, hashed request preview. It never sends the request.
    #>
    [CmdletBinding()]
    [OutputType([UvexAdv.Commissioning.NinaSlewRequestPreview])]
    param(
        [Parameter(Mandatory)][object] $Coordinate,
        [Parameter(Mandatory)][guid] $OperationId,
        [uri] $Endpoint = 'http://127.0.0.1:1888/v2/api/equipment/mount/slew'
    )

    if ($Coordinate -isnot [UvexAdv.Commissioning.ValidatedEquatorialCoordinate]) {
        throw 'Coordinate must be the strong type returned by ConvertFrom-UvexNinaCoordinateTransform.'
    }
    if ($OperationId -eq [guid]::Empty) {
        throw 'OperationId must be a non-empty GUID.'
    }
    $authorizedEndpoint = 'http://127.0.0.1:1888/v2/api/equipment/mount/slew'
    if ($Endpoint.AbsoluteUri.TrimEnd('/') -cne $authorizedEndpoint) {
        throw "The N.I.N.A. slew endpoint must be exactly '$authorizedEndpoint'."
    }

    $raText = $Coordinate.RightAscensionDegrees.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    $decText = $Coordinate.DeclinationDegrees.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    $requestUriText = $authorizedEndpoint `
        + '?ra=' + [uri]::EscapeDataString($raText) `
        + '&dec=' + [uri]::EscapeDataString($decText) `
        + '&waitForResult=true&center=false&rotate=false&rotationAngle=0'
    $requestCanonical = "GET`n$requestUriText"
    $requestSha256 = Get-UvexSha256Text -Text $requestCanonical
    $intentCanonical = [ordered]@{
        schema = 'uvex-adv.nina-slew-intent-preview.v1'
        operationId = $OperationId.ToString('D')
        requestSha256 = $requestSha256
        transformOutputSha256 = $Coordinate.TransformOutputSha256
        rightAscensionDegrees = $Coordinate.RightAscensionDegrees
        declinationDegrees = $Coordinate.DeclinationDegrees
        epoch = $Coordinate.Epoch
        automaticRetryAllowed = $false
    } | ConvertTo-Json -Compress
    $intentSha256 = Get-UvexSha256Text -Text $intentCanonical

    return [UvexAdv.Commissioning.NinaSlewRequestPreview]::new(
        $OperationId,
        $Coordinate,
        [uri]$requestUriText,
        $requestSha256,
        $intentSha256)
}

Export-ModuleMember -Function ConvertFrom-UvexNinaCoordinateTransform, New-UvexNinaSlewRequestPreview
