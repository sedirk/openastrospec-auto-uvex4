param(
    [Parameter(Mandatory=$true)][string]$DataRoot,
    [Parameter(Mandatory=$true)][string]$ObservatoryAssembly,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($DataRoot)
$output=[IO.Path]::GetFullPath($OutputPath)
if($output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Diagnostic output cannot be inside observational data.'
}
if(Test-Path -LiteralPath $output){throw 'Output must be new.'}
[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ObservatoryAssembly)) | Out-Null
$options=[Text.Json.JsonSerializerOptions]::new()
$options.PropertyNameCaseInsensitive=$true
function Convert-Frame([object]$value){
    $json=$value | ConvertTo-Json -Depth 12 -Compress
    return [Text.Json.JsonSerializer]::Deserialize($json,[UvexAdv.Observatory.QhyG3SolvedFrame],$options)
}
function Convert-Policy([object]$value){
    $json=$value | ConvertTo-Json -Depth 12 -Compress
    return [Text.Json.JsonSerializer]::Deserialize($json,[UvexAdv.Observatory.QhyG3FastPairPolicy],$options)
}
$reference=[DateTimeOffset]::new(2026,10,24,0,0,0,[TimeSpan]::Zero)
$rows=foreach($file in Get-ChildItem (Join-Path $root 'calibration\qhy-g3') -Recurse -Filter 'candidate-*.json'){
    $candidate=(Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json -AsHashtable).candidate
    $q=$candidate.qhy
    $g=$candidate.g3
    $qe=([DateTimeOffset]$q.exposureEndedUtc-[DateTimeOffset]$q.exposureStartedUtc).TotalSeconds
    $qm=([DateTimeOffset]$q.exposureMidpointUtc-[DateTimeOffset]$q.exposureStartedUtc).TotalSeconds
    $qs=([DateTimeOffset]$q.solveCompletedUtc-[DateTimeOffset]$q.exposureEndedUtc).TotalSeconds
    $ge=([DateTimeOffset]$g.exposureEndedUtc-[DateTimeOffset]$g.exposureStartedUtc).TotalSeconds
    $gs=([DateTimeOffset]$g.solveCompletedUtc-[DateTimeOffset]$g.exposureEndedUtc).TotalSeconds
    $first=$q.Clone()
    $first.exposureStartedUtc=$reference.AddSeconds($gs)
    $first.exposureMidpointUtc=$reference.AddSeconds($gs+$qm)
    $first.exposureEndedUtc=$reference.AddSeconds($gs+$qe)
    $first.solveCompletedUtc=$reference.AddSeconds($gs+$qe+$qs)
    $second=$q.Clone()
    $second.framePath='offline-prototype-second-not-acquired.fit'
    $second.frameSha256=('A'*64)
    $second.exposureStartedUtc=$first.solveCompletedUtc
    $second.exposureMidpointUtc=$first.solveCompletedUtc.AddSeconds($qm)
    $second.exposureEndedUtc=$first.solveCompletedUtc.AddSeconds($qe)
    $second.solveCompletedUtc=$first.solveCompletedUtc.AddSeconds($qe+$qs)
    $f=Convert-Frame $first
    $s=Convert-Frame $second
    $policy=Convert-Policy $candidate.collectionPolicy
    $uncertainty=[Math]::Sqrt([Math]::Pow($q.pixelScaleArcseconds,2)+[Math]::Pow($candidate.collectionPolicy.maximumMountSpanArcseconds,2))
    foreach($modelUncertainty in @(0.0,10.0,21.810878796657416)){
        $available=100*$g.pixelScaleArcseconds-$modelUncertainty
        $result=[UvexAdv.Observatory.QhyG3SkyDriftPolicy]::Build($f,$s,$uncertainty,$uncertainty,
            $reference.AddSeconds(-$ge),$reference,[int][Math]::Round(1000*$ge),$policy,$s.SolveCompletedUtc,$available)
        [pscustomobject]@{
            runId=$candidate.observationRunId
            scenario='Two hypothetical new witnesses with actual-record exposure/solve durations; exact G3 timing; zero sky drift and zero extra IO/CPU/HTTP latency'
            modelUncertaintyArcseconds=$modelUncertainty
            actualProductionPolicyCode=$result.Gate.Code
            accepted=($null-ne$result.Witness)
            capArcseconds=100*$g.pixelScaleArcseconds
            g3ExposureSeconds=$ge
            qhyExposureSeconds=$qe
            qhySolveSeconds=$qs
            g3SolveSeconds=$gs
            baselineSeconds=($s.ExposureMidpointUtc-$f.ExposureMidpointUtc).TotalSeconds
            additionalUncertaintyArcseconds=if($null-ne$result.Witness){$result.Witness.AdditionalUncertaintyArcseconds}else{$null}
            nativeSourceAcquisitionOccurred=$false
        }
    }
}
$assembly=(Get-FileHash -LiteralPath $ObservatoryAssembly -Algorithm SHA256).Hash
$report=[ordered]@{
    purpose='Offline prototype against the actual .311 pure policy assembly; no production invocation or observed second image'
    assemblySha256=$assembly
    scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    sourceCandidateCount=($rows.Count/3)
    summary=@($rows | Group-Object modelUncertaintyArcseconds,actualProductionPolicyCode | ForEach-Object{[pscustomobject]@{group=$_.Name;count=$_.Count}})
    rows=@($rows)
    rawSourcesWritten=$false
    hardwareAccessed=$false
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$bytes=[Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 12))
$stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
$report.summary | ConvertTo-Json -Depth 5
