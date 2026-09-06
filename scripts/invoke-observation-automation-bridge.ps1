[CmdletBinding(DefaultParameterSetName = 'Snapshot')]
param(
    [Parameter(ParameterSetName = 'Snapshot')][switch]$Snapshot,
    [Parameter(Mandatory, ParameterSetName = 'Wait')][switch]$Wait,
    [Parameter(Mandatory, ParameterSetName = 'Wait')][long]$AfterRevision,
    [Parameter(ParameterSetName = 'Wait')][ValidateRange(0, 60000)][int]$TimeoutMilliseconds = 30000,
    [Parameter(Mandatory, ParameterSetName = 'Invoke')][switch]$Invoke,
    [Parameter(Mandatory, ParameterSetName = 'Invoke')][ValidateSet('enable-bridge','arm-real-control','disarm-real-control','arm-slit-quality-warning','disarm-slit-quality-warning','select-simulation','select-real','apply-target-draft','import-planetarium-target','import-framing-target','start-selected','restart-real-run','pause','resume','takeover','cancel')][string]$Command,
    [Parameter(Mandatory, ParameterSetName = 'Invoke')][long]$ExpectedRevision,
    [Parameter(ParameterSetName = 'Invoke')][string]$OperatorAttestation = '',
    [Parameter(ParameterSetName = 'Invoke')][hashtable]$TargetDraft,
    [ValidateRange(100, 30000)][int]$ConnectTimeoutMilliseconds = 5000,
    [ValidateRange(100, 120000)][int]$ResponseTimeoutMilliseconds = 65000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pipeName = 'OpenAstroSpec.UVEX4.ObservationAutomation.v1'
$request = [ordered]@{
    protocolVersion = 1
    requestId = [Guid]::NewGuid().ToString('N')
    operation = switch ($PSCmdlet.ParameterSetName) {
        'Wait' { 'wait' }
        'Invoke' { 'invoke' }
        default { 'snapshot' }
    }
}
if ($PSCmdlet.ParameterSetName -eq 'Wait') {
    $request.afterRevision = $AfterRevision
    $request.timeoutMilliseconds = $TimeoutMilliseconds
}
if ($PSCmdlet.ParameterSetName -eq 'Invoke') {
    $request.command = $Command
    $request.expectedRevision = $ExpectedRevision
    if ($null -ne $TargetDraft) { $request.targetDraft = $TargetDraft }
    if (-not [string]::IsNullOrWhiteSpace($OperatorAttestation)) {
        $request.operatorAttestation = $OperatorAttestation
    }
}

$pipe = [IO.Pipes.NamedPipeClientStream]::new(
    '.',
    $pipeName,
    [IO.Pipes.PipeDirection]::InOut,
    [IO.Pipes.PipeOptions]::Asynchronous)
try {
    $pipe.Connect($ConnectTimeoutMilliseconds)
    $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 4096, $true)
    $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true
    try {
        $writer.WriteLine(($request | ConvertTo-Json -Depth 5 -Compress))
        $readTask = $reader.ReadLineAsync()
        if (-not $readTask.Wait($ResponseTimeoutMilliseconds)) {
            throw "The N.I.N.A. automation bridge did not respond within $ResponseTimeoutMilliseconds ms."
        }
        $line = $readTask.Result
        if ([string]::IsNullOrWhiteSpace($line)) {
            throw 'The N.I.N.A. automation bridge closed without a response.'
        }
        $line | ConvertFrom-Json
    }
    finally {
        $writer.Dispose()
        $reader.Dispose()
    }
}
finally {
    $pipe.Dispose()
}
