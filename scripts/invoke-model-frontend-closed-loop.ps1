[CmdletBinding()]
param(
    [ValidateSet('Simulation','Real')][string]$Mode = 'Simulation',
    [string]$OperatorAttestation = '',
    [switch]$NewRunBoundary,
    [switch]$AllowSupervisedSlitQualityWarning,
    [string]$ObserveRunId = '',
    [ValidateRange(1, 1440)][int]$MaximumMinutes = 180,
    [string]$ModelRepairCommand = '',
    [ValidateRange(0, 20)][int]$MaximumModelRepairAttempts = 5,
    [ValidateRange(10, 600)][int]$StartAcknowledgementSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$requiredRealAttestation = 'OPERATOR-ATTESTS-ROOF-OPEN-CLEAR-SKY-DEVICE-MOTION-AUTHORIZED'
if ($ObserveRunId -and $NewRunBoundary) {
    throw '-ObserveRunId cannot create a new run boundary.'
}
if ($Mode -eq 'Real' -and $OperatorAttestation -ne $requiredRealAttestation) {
    throw "Real mode requires -OperatorAttestation $requiredRealAttestation"
}

$client = Join-Path $PSScriptRoot 'invoke-observation-automation-bridge.ps1'
$controllerRunId = 'frontend-model-loop-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $env:LOCALAPPDATA "UVEX-ADV\automation\runs\$controllerRunId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$manifestPath = Join-Path $runRoot 'manifest.json'
$events = [Collections.Generic.List[object]]::new()
$modelRepairAttempts = 0
$productionRunGeneration = 0
$productionObservationRunId = $null
$productionManifestPath = $null
$startedUtc = [DateTimeOffset]::UtcNow
$deadline = $startedUtc.AddMinutes($MaximumMinutes)
$succeeded = $false
$terminalState = 'Unknown'
$lastObservedRevisionKey = $null
$maximumStaleRevisionRetries = 3

function Get-OptionalValue([object]$Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

function Add-LoopEvent([string]$Code, [object]$Data) {
    $script:events.Add([ordered]@{
        timestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        code = $Code
        data = $Data
    })
}

function Write-JsonAtomically([string]$Path, [object]$Value) {
    $temporaryPath = $Path + '.tmp-' + [Guid]::NewGuid().ToString('N')
    $backupPath = $Path + '.bak-' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText(
            $temporaryPath,
            ($Value | ConvertTo-Json -Depth 30),
            [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($Path)) {
            [IO.File]::Replace($temporaryPath, $Path, $backupPath)
            [IO.File]::Delete($backupPath)
        } else {
            try {
                [IO.File]::Move($temporaryPath, $Path)
            } catch [IO.IOException] {
                if (-not [IO.File]::Exists($Path)) { throw }
                [IO.File]::Replace($temporaryPath, $Path, $backupPath)
                [IO.File]::Delete($backupPath)
            }
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
        if ([IO.File]::Exists($backupPath)) {
            [IO.File]::Delete($backupPath)
        }
    }
}

function Save-Manifest {
    $manifest = [ordered]@{
        schema = 'openastrospec-auto-uvex4.model-frontend-closed-loop.v2'
        runId = $controllerRunId
        entryPoint = 'NinaDockableICommandBridge'
        mode = $Mode
        startedUtc = $startedUtc.ToString('O')
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        succeeded = $script:succeeded
        terminalState = $script:terminalState
        productionRunGeneration = $script:productionRunGeneration
        productionObservationRunId = $script:productionObservationRunId
        productionManifestPath = $script:productionManifestPath
        directDeviceApiCalls = $false
        productionRunnerRemainsSoleWorkflowOwner = $true
        operatorSafetyAttestationSupplied = $Mode -eq 'Real' -and $OperatorAttestation -eq $requiredRealAttestation
        operatorSlitQualityWarningAuthorizationSupplied = [bool]$AllowSupervisedSlitQualityWarning
        observeExistingRunId = $ObserveRunId
        genericAutomaticResumeEnabled = $false
        modelRepairAttempts = $script:modelRepairAttempts
        events = $events
    }
    Write-JsonAtomically $manifestPath $manifest
}

function Assert-BridgeResponse([object]$Response, [string]$Operation) {
    if ($null -eq $Response -or $null -eq (Get-OptionalValue $Response 'snapshot')) {
        throw "The N.I.N.A. automation bridge returned no snapshot for $Operation."
    }
    if ((Get-OptionalValue $Response 'ok') -ne $true) {
        throw "The N.I.N.A. automation bridge rejected $Operation`: $(Get-OptionalValue $Response 'code'): $(Get-OptionalValue $Response 'message')"
    }
    if ($null -eq (Get-OptionalValue $Response.snapshot 'observationRunId')) {
        $hasRunIdProperty = $null -ne $Response.snapshot.PSObject.Properties['observationRunId']
        $hasRunBindingIdentity =
            -not [string]::IsNullOrWhiteSpace([string](Get-OptionalValue $Response.snapshot 'bridgeInstanceId')) -and
            -not [string]::IsNullOrWhiteSpace([string](Get-OptionalValue $Response.snapshot 'pluginBuildSha256'))
        if (-not $hasRunIdProperty -and -not $hasRunBindingIdentity) {
            throw 'The installed bridge is older than the run-binding protocol; install the current plug-in before starting a closed loop.'
        }
    }
}

function Invoke-BridgeSnapshot {
    $response = & $client -Snapshot -ConnectTimeoutMilliseconds 10000 -ResponseTimeoutMilliseconds 15000
    Assert-BridgeResponse $response 'snapshot'
    $response
}

function Invoke-BridgeWait([long]$AfterRevision) {
    $response = & $client -Wait -AfterRevision $AfterRevision -TimeoutMilliseconds 30000 -ConnectTimeoutMilliseconds 10000 -ResponseTimeoutMilliseconds 45000
    Assert-BridgeResponse $response 'wait'
    $response
}

function Invoke-BridgeButton([string]$Name, [long]$Revision, [string]$Attestation = '') {
    $currentRevision = $Revision
    $staleRetryCount = 0
    while ($true) {
        $response = & $client -Invoke -Command $Name -ExpectedRevision $currentRevision -OperatorAttestation $Attestation -ConnectTimeoutMilliseconds 10000 -ResponseTimeoutMilliseconds 15000
        Add-LoopEvent 'FRONTEND_BUTTON_REQUEST' ([ordered]@{
            command = $Name
            accepted = Get-OptionalValue $response 'ok'
            code = Get-OptionalValue $response 'code'
            message = Get-OptionalValue $response 'message'
            expectedRevision = $currentRevision
            revision = Get-OptionalValue (Get-OptionalValue $response 'snapshot') 'revision'
            staleRetryCount = $staleRetryCount
        })
        Save-Manifest

        if ((Get-OptionalValue $response 'ok') -eq $true) {
            Assert-BridgeResponse $response "command '$Name'"
            return $response
        }
        if ([string](Get-OptionalValue $response 'code') -ne 'STALE_REVISION') {
            Assert-BridgeResponse $response "command '$Name'"
        }

        $snapshot = Get-OptionalValue $response 'snapshot'
        if ($null -eq $snapshot) {
            throw "The N.I.N.A. automation bridge returned STALE_REVISION without a replacement snapshot for command '$Name'."
        }
        if ($staleRetryCount -ge $maximumStaleRevisionRetries) {
            throw "The visible command '$Name' remained stale after $maximumStaleRevisionRetries bounded retries."
        }
        if ((Get-AvailableCommands $snapshot) -notcontains $Name) {
            throw "The visible command '$Name' is no longer available after the front-end revision changed."
        }

        $nextRevision = Get-OptionalValue $snapshot 'revision'
        if ($null -eq $nextRevision) {
            throw "The N.I.N.A. automation bridge returned STALE_REVISION without a replacement revision for command '$Name'."
        }
        $staleRetryCount++
        Add-LoopEvent 'FRONTEND_STALE_REVISION_RETRY' ([ordered]@{
            command = $Name
            retry = $staleRetryCount
            previousExpectedRevision = $currentRevision
            nextExpectedRevision = [long]$nextRevision
        })
        Save-Manifest
        $currentRevision = [long]$nextRevision
    }
}

function Get-AvailableCommands([object]$Snapshot) {
    $value = Get-OptionalValue $Snapshot 'availableCommands'
    if ($null -eq $value) { return @() }
    @($value)
}

function Enable-BridgeAndRealAuthorization([object]$Response) {
    if ((Get-OptionalValue $Response.snapshot 'bridgeEnabled') -ne $true) {
        $Response = Invoke-BridgeButton 'enable-bridge' ([long](Get-OptionalValue $Response.snapshot 'revision'))
    }
    if ($Mode -eq 'Real' -and (Get-OptionalValue $Response.snapshot 'realControlArmedForThisNinaSession') -ne $true) {
        $Response = Invoke-BridgeButton 'arm-real-control' ([long](Get-OptionalValue $Response.snapshot 'revision')) $OperatorAttestation
    }
    if ($Mode -eq 'Real' -and $AllowSupervisedSlitQualityWarning -and
        (Get-OptionalValue $Response.snapshot 'supervisedSlitQualityWarningAuthorized') -ne $true) {
        $Response = Invoke-BridgeButton 'arm-slit-quality-warning' ([long](Get-OptionalValue $Response.snapshot 'revision')) 'OPERATOR-ACCEPTS-SLIT-PRECISION-WARNING-SUPERVISED-ATR-PROBE'
    }
    $Response
}

function Select-RequestedMode([object]$Response) {
    $selected = (Get-OptionalValue $Response.snapshot 'realModeSelected') -eq $true
    $requiresChange = ($Mode -eq 'Real' -and -not $selected) -or ($Mode -eq 'Simulation' -and $selected)
    if (-not $requiresChange) { return $Response }

    $command = if ($Mode -eq 'Real') { 'select-real' } else { 'select-simulation' }
    if ((Get-AvailableCommands $Response.snapshot) -notcontains $command) {
        throw "The requested $Mode mode cannot be selected in the current front-end state."
    }
    Invoke-BridgeButton $command ([long](Get-OptionalValue $Response.snapshot 'revision'))
}

function Start-NewProductionRun([object]$Response, [string]$Reason) {
    $Response = Enable-BridgeAndRealAuthorization $Response
    $Response = Select-RequestedMode $Response
    $baselineRunId = [string](Get-OptionalValue $Response.snapshot 'observationRunId')
    $baselineUiError = [string](Get-OptionalValue $Response.snapshot 'uiError')
    $available = Get-AvailableCommands $Response.snapshot

    if ($Mode -eq 'Real') {
        if ($NewRunBoundary) {
            # Explicitly select the existing visible "new run" command after
            # a NINA restart. An empty UI run id does not prove the absence of
            # durable ledgers. This never edits their files from the controller.
            if ($available -notcontains 'restart-real-run') {
                throw 'The explicitly requested visible new-run command is not available.'
            }
            $startCommand = 'restart-real-run'
        } elseif ([string]::IsNullOrWhiteSpace($baselineRunId)) {
            if ($available -notcontains 'start-selected') {
                throw 'The visible Start command cannot create a clean real run in the current front-end state.'
            }
            $startCommand = 'start-selected'
        } elseif ($available -contains 'restart-real-run') {
            $startCommand = 'restart-real-run'
        } else {
            throw 'The front end cannot create a fresh real run boundary. restart-real-run is required when a previous production run is present.'
        }
    } else {
        if ($available -notcontains 'start-selected') {
            throw 'The front end cannot start a fresh simulation from its current state; no existing run is resumed or cancelled automatically.'
        }
        $startCommand = 'start-selected'
    }

    $attestation = if ($startCommand -eq 'restart-real-run') { $OperatorAttestation } else { '' }
    $dispatchUtc = [DateTimeOffset]::UtcNow
    $Response = Invoke-BridgeButton $startCommand ([long](Get-OptionalValue $Response.snapshot 'revision')) $attestation
    $script:productionRunGeneration++
    $script:productionObservationRunId = $null
    $script:productionManifestPath = $null
    Add-LoopEvent 'FRESH_FRONTEND_RUN_DISPATCHED' ([ordered]@{
        mode = $Mode
        command = $startCommand
        reason = $Reason
        generation = $script:productionRunGeneration
        baselineObservationRunId = $baselineRunId
    })
    Save-Manifest
    [pscustomobject]@{
        Response = $Response
        BaselineObservationRunId = $baselineRunId
        BaselineUiError = $baselineUiError
        DispatchUtc = $dispatchUtc
        AcknowledgementDeadline = [DateTimeOffset]::UtcNow.AddSeconds($StartAcknowledgementSeconds)
    }
}

function Save-BlockerSnapshot([object]$Snapshot, [string]$Code, [string]$State) {
    $safeCode = $Code -replace '[^A-Za-z0-9_.-]', '_'
    $blockerPath = Join-Path $runRoot ("blocker-{0:00}-{1}-{2}.json" -f $events.Count, $State, $safeCode)
    Write-JsonAtomically $blockerPath $Snapshot
    Add-LoopEvent 'FRONTEND_BLOCKER_CAPTURED' ([ordered]@{
        state = $State
        code = $Code
        observationRunId = Get-OptionalValue $Snapshot 'observationRunId'
        snapshot = $blockerPath
        evidence = Get-OptionalValue $Snapshot 'failureEvidencePath'
    })
    Save-Manifest
    $blockerPath
}

function Invoke-StructuredModelRepair([object]$Snapshot, [string]$State) {
    $code = [string](Get-OptionalValue $Snapshot 'failureCode')
    if ([string]::IsNullOrWhiteSpace($code)) { $code = "${State}_WITHOUT_FAILURE_CODE" }
    $blockerPath = Save-BlockerSnapshot $Snapshot $code $State

    if ([string]::IsNullOrWhiteSpace($ModelRepairCommand)) {
        throw "Front-end state $State requires an explicit model repair command; no automatic Resume is permitted. Blocker: $blockerPath"
    }
    if ($script:modelRepairAttempts -ge $MaximumModelRepairAttempts) {
        throw "Front-end state $State exhausted the $MaximumModelRepairAttempts structured model repair attempts. Blocker: $blockerPath"
    }

    $script:modelRepairAttempts++
    Add-LoopEvent 'MODEL_REPAIR_REQUESTED' ([ordered]@{
        attempt = $script:modelRepairAttempts
        state = $State
        code = $code
        snapshot = $blockerPath
        command = $ModelRepairCommand
    })
    Save-Manifest
    & $ModelRepairCommand $blockerPath $controllerRunId
    if (-not $?) {
        throw "Model repair command failed for $State blocker $code."
    }
    Add-LoopEvent 'MODEL_REPAIR_RETURNED' ([ordered]@{ attempt=$script:modelRepairAttempts; state=$State; code=$code })
    Save-Manifest
}

function Reconnect-BridgeAfterRepair {
    $reconnectDeadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    while ([DateTimeOffset]::UtcNow -lt $reconnectDeadline) {
        try {
            return Invoke-BridgeSnapshot
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    throw 'The N.I.N.A. automation bridge did not return within three minutes after model repair.'
}

function Read-VerifiedCompletionManifest([object]$Snapshot, [string]$ExpectedObservationRunId) {
    $path = [string](Get-OptionalValue $Snapshot 'runManifestPath')
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Completed run '$ExpectedObservationRunId' did not publish a run manifest path."
    }

    $verificationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    $lastIssue = 'manifest not yet readable'
    do {
        try {
            if (-not [IO.File]::Exists($path)) { throw "manifest does not exist: $path" }
            $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
                try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
                finally { $reader.Dispose() }
            }
            finally { $stream.Dispose() }
            $manifestRunId = [string](Get-OptionalValue $manifest 'observationRunId')
            $plan = Get-OptionalValue $manifest 'plan'
            $planRunId = [string](Get-OptionalValue $plan 'observationRunId')
            $manifestSnapshot = Get-OptionalValue $manifest 'snapshot'
            $snapshotRunId = [string](Get-OptionalValue $manifestSnapshot 'observationRunId')
            $terminal = [string](Get-OptionalValue $manifest 'terminalState')
            if ([string]::IsNullOrWhiteSpace($terminal)) {
                $terminal = [string](Get-OptionalValue $manifestSnapshot 'state')
            }
            if ($manifestRunId -ne $ExpectedObservationRunId) {
                throw "manifest run id '$manifestRunId' does not match '$ExpectedObservationRunId'"
            }
            if ($planRunId -ne $ExpectedObservationRunId) {
                throw "manifest plan run id '$planRunId' does not match '$ExpectedObservationRunId'"
            }
            if ($snapshotRunId -ne $ExpectedObservationRunId) {
                throw "manifest snapshot run id '$snapshotRunId' does not match '$ExpectedObservationRunId'"
            }
            if ($terminal -ne 'Completed') {
                throw "manifest terminal state is '$terminal', not 'Completed'"
            }

            if ([int](Get-OptionalValue $manifest 'schemaVersion') -ne 1) {
                throw "manifest schemaVersion is not 1"
            }
            if ([string](Get-OptionalValue $manifestSnapshot 'state') -ne 'Completed') {
                throw "manifest snapshot is not Completed"
            }
            $completedStages = [int](Get-OptionalValue $manifestSnapshot 'completedStageCount')
            $totalStages = [int](Get-OptionalValue $manifestSnapshot 'totalStageCount')
            if ($completedStages -ne 11 -or $totalStages -ne 11) {
                throw "manifest stage completion is $completedStages/$totalStages instead of 11/11"
            }
            if ($null -ne (Get-OptionalValue $manifestSnapshot 'currentStage') -or
                $null -ne (Get-OptionalValue $manifestSnapshot 'nextStage') -or
                -not [string]::IsNullOrWhiteSpace([string](Get-OptionalValue $manifestSnapshot 'pauseReason'))) {
                throw "completed manifest still contains a current/next stage or pause reason"
            }

            $adapter = [string](Get-OptionalValue (Get-OptionalValue (Get-OptionalValue $manifest 'lockedMetadata') 'labels') 'adapter')
            $expectedAdapter = if ($Mode -eq 'Real') { 'real' } else { 'simulator' }
            if ($adapter -ne $expectedAdapter) {
                throw "manifest adapter '$adapter' does not match requested mode '$expectedAdapter'"
            }
            foreach ($hashName in @('planSha256','lockedMetadataSha256')) {
                $hashValue = [string](Get-OptionalValue $manifest $hashName)
                if ($hashValue -notmatch '^[A-Fa-f0-9]{64}$') {
                    throw "manifest $hashName is not a SHA-256 value"
                }
            }

            $gatesObject = Get-OptionalValue $manifest 'gates'
            $gateProperties = @($gatesObject.PSObject.Properties)
            if ($gateProperties.Count -ne 11) {
                throw "manifest contains $($gateProperties.Count) stage gates instead of 11"
            }
            foreach ($gateProperty in $gateProperties) {
                $gate = $gateProperty.Value
                if ([string](Get-OptionalValue $gate 'disposition') -ne 'Passed') {
                    throw "stage gate '$($gateProperty.Name)' is not Passed"
                }
                if ([string](Get-OptionalValue $gate 'code') -like 'SIM_*' -and $Mode -eq 'Real') {
                    throw "real completion contains simulator gate '$($gateProperty.Name)'"
                }
            }

            $journal = @(Get-OptionalValue $manifest 'journal')
            if ($journal.Count -eq 0) { throw 'manifest journal is empty' }
            $lastJournal = $journal[-1]
            if ([string](Get-OptionalValue $lastJournal 'kind') -ne 'Snapshot' -or
                [string](Get-OptionalValue $lastJournal 'state') -ne 'Completed' -or
                [string](Get-OptionalValue $lastJournal 'code') -ne 'RUN_COMPLETED' -or
                [long](Get-OptionalValue $lastJournal 'revision') -ne [long](Get-OptionalValue $manifest 'revision')) {
                throw 'manifest final journal entry is not the durable RUN_COMPLETED snapshot revision'
            }

            if ($Mode -eq 'Real') {
                $counters = Get-OptionalValue $manifest 'counters'
                if ([long](Get-OptionalValue $counters 'atrAttemptedFrames') -le 0 -or
                    [long](Get-OptionalValue $counters 'atrAcceptedFrames') -le 0) {
                    throw 'real completion has no accepted ATR science frame counters'
                }
            }

            foreach ($evidence in @(Get-OptionalValue $manifest 'evidence')) {
                $evidencePath = [string](Get-OptionalValue $evidence 'absolutePath')
                $evidenceSha256 = [string](Get-OptionalValue $evidence 'sha256')
                if ([string]::IsNullOrWhiteSpace($evidencePath) -or -not [IO.File]::Exists($evidencePath)) {
                    throw "manifest evidence is missing: $evidencePath"
                }
                if ($evidenceSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                    throw "manifest evidence lacks a valid SHA-256: $evidencePath"
                }
                $actualEvidenceSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
                if ($actualEvidenceSha256 -ne $evidenceSha256) {
                    throw "manifest evidence SHA-256 mismatch: $evidencePath"
                }
            }
            return [pscustomobject]@{
                Path = [IO.Path]::GetFullPath($path)
                ObservationRunId = $manifestRunId
                TerminalState = $terminal
                Revision = Get-OptionalValue $manifest 'revision'
                Adapter = $adapter
                CompletedStageCount = $completedStages
                TotalStageCount = $totalStages
            }
        } catch {
            $lastIssue = $_.Exception.Message
            Start-Sleep -Milliseconds 250
        }
    } while ([DateTimeOffset]::UtcNow -lt $verificationDeadline)

    throw "The completed production manifest could not be verified: $lastIssue"
}

try {
    $response = Invoke-BridgeSnapshot
    if ($ObserveRunId) {
        if ([string](Get-OptionalValue $response.snapshot 'observationRunId') -ne $ObserveRunId) {
            throw 'The requested existing run is not the currently visible production run.'
        }
        # Read-only attachment after an explicitly invoked UI recovery. It does
        # not arm, Resume, replace a run, or waive final completion verification.
        $productionObservationRunId = $ObserveRunId
        Add-LoopEvent 'EXISTING_FRONTEND_RUN_OBSERVED' ([ordered]@{
            observationRunId = $ObserveRunId
            bridgeInstanceId = Get-OptionalValue $response.snapshot 'bridgeInstanceId'
            pluginBuildSha256 = Get-OptionalValue $response.snapshot 'pluginBuildSha256'
            state = Get-OptionalValue $response.snapshot 'runState'
            buttonDispatched = $false
        })
        Save-Manifest
    } else {
        $response = Enable-BridgeAndRealAuthorization $response
        $startContext = Start-NewProductionRun $response 'initial-controller-start'
        $response = $startContext.Response
    }

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        # Process the already-returned snapshot before waiting again. This
        # prevents a terminal or blocker revision from being skipped.
        $snapshot = $response.snapshot
        $terminalState = [string](Get-OptionalValue $snapshot 'runState')
        $revisionKey = "$(Get-OptionalValue $snapshot 'bridgeInstanceId')/$(Get-OptionalValue $snapshot 'revision')"
        if ($revisionKey -ne $lastObservedRevisionKey) {
            Add-LoopEvent 'FRONTEND_STATE_OBSERVED' ([ordered]@{
                revision = Get-OptionalValue $snapshot 'revision'
                bridgeInstanceId = Get-OptionalValue $snapshot 'bridgeInstanceId'
                observationRunId = Get-OptionalValue $snapshot 'observationRunId'
                state = $terminalState
                stage = Get-OptionalValue $snapshot 'currentStage'
                failureCode = Get-OptionalValue $snapshot 'failureCode'
                evidence = Get-OptionalValue $snapshot 'failureEvidencePath'
                uiError = Get-OptionalValue $snapshot 'uiError'
            })
            $lastObservedRevisionKey = $revisionKey
            Save-Manifest
        }

        $observedRunId = [string](Get-OptionalValue $snapshot 'observationRunId')
        if ([string]::IsNullOrWhiteSpace([string]$productionObservationRunId)) {
            $runUpdatedUtc = [DateTimeOffset]::MinValue
            $rawRunUpdatedUtc = Get-OptionalValue $snapshot 'runUpdatedUtc'
            # PowerShell 7 deserializes ISO JSON dates to DateTime. Casting
            # that value to string drops fractional seconds; a fast stage
            # failure could then look older than this run's dispatch forever.
            if ($rawRunUpdatedUtc -is [DateTimeOffset]) {
                $runUpdatedUtc = $rawRunUpdatedUtc
            } elseif ($rawRunUpdatedUtc -is [DateTime]) {
                $runUpdatedUtc = [DateTimeOffset]$rawRunUpdatedUtc
            } else {
                [DateTimeOffset]::TryParse([string]$rawRunUpdatedUtc, [ref]$runUpdatedUtc) | Out-Null
            }
            if (-not [string]::IsNullOrWhiteSpace($observedRunId) -and
                $observedRunId -ne $startContext.BaselineObservationRunId -and
                $runUpdatedUtc -ge $startContext.DispatchUtc) {
                $productionObservationRunId = $observedRunId
                Add-LoopEvent 'PRODUCTION_RUN_BOUND' ([ordered]@{
                    generation = $productionRunGeneration
                    observationRunId = $productionObservationRunId
                    baselineObservationRunId = $startContext.BaselineObservationRunId
                    state = $terminalState
                })
                Save-Manifest
            } else {
                $uiError = [string](Get-OptionalValue $snapshot 'uiError')
                if (-not [string]::IsNullOrWhiteSpace($uiError) -and $uiError -ne $startContext.BaselineUiError) {
                    throw "The front end rejected the fresh run before assigning an ObservationRunId: $uiError $(Get-OptionalValue $snapshot 'uiErrorTechnicalDetails')"
                }
                if ([DateTimeOffset]::UtcNow -ge $startContext.AcknowledgementDeadline) {
                    throw "The front end did not assign a new ObservationRunId within $StartAcknowledgementSeconds seconds; the old run cannot be treated as this controller run."
                }
                $response = Invoke-BridgeWait ([long](Get-OptionalValue $snapshot 'revision'))
                continue
            }
        } elseif ($observedRunId -ne $productionObservationRunId) {
            throw "The production ObservationRunId changed unexpectedly from '$productionObservationRunId' to '$observedRunId'."
        }

        if ($terminalState -eq 'Completed') {
            $verified = Read-VerifiedCompletionManifest $snapshot $productionObservationRunId
            $productionManifestPath = $verified.Path
            $succeeded = $true
            Add-LoopEvent 'PRODUCTION_FRONTEND_LOOP_COMPLETED' ([ordered]@{
                observationRunId = $productionObservationRunId
                manifest = $productionManifestPath
                manifestTerminalState = $verified.TerminalState
                manifestRevision = $verified.Revision
                latestEvidence = Get-OptionalValue $snapshot 'latestEvidencePath'
                modelRepairAttempts = $modelRepairAttempts
            })
            Save-Manifest
            break
        }

        if ($terminalState -eq 'Cancelled') {
            $code = [string](Get-OptionalValue $snapshot 'failureCode')
            throw "Production run '$productionObservationRunId' was cancelled; it is not restarted automatically. $code $(Get-OptionalValue $snapshot 'failureMessage')"
        }

        if ($terminalState -in @('Paused','ManualTakeover')) {
            $code = [string](Get-OptionalValue $snapshot 'failureCode')
            if ([string]::IsNullOrWhiteSpace($code)) { $code = $terminalState.ToUpperInvariant() }
            $blockerPath = Save-BlockerSnapshot $snapshot $code $terminalState
            throw "Production run '$productionObservationRunId' is in operator-owned state $terminalState. The controller will neither Resume nor restart it. Snapshot: $blockerPath"
        }

        if ($terminalState -in @('PausedNeedsAttention','Faulted')) {
            Invoke-StructuredModelRepair $snapshot $terminalState
            $response = Reconnect-BridgeAfterRepair
            $response = Enable-BridgeAndRealAuthorization $response
            $startContext = Start-NewProductionRun $response "structured-repair-after-$terminalState"
            $response = $startContext.Response
            continue
        }

        $response = Invoke-BridgeWait ([long](Get-OptionalValue $snapshot 'revision'))
    }

    if (-not $succeeded) { throw "The front-end closed loop exceeded $MaximumMinutes minutes." }
}
catch {
    Add-LoopEvent 'CONTROLLER_STOPPED' ([ordered]@{
        terminalState = $terminalState
        productionObservationRunId = $productionObservationRunId
        reason = $_.Exception.Message
    })
    Save-Manifest
    throw
}
finally {
    Save-Manifest
    Write-Output ([pscustomobject]@{
        RunId = $controllerRunId
        ProductionObservationRunId = $productionObservationRunId
        Succeeded = $succeeded
        TerminalState = $terminalState
        ManifestPath = $manifestPath
        ProductionManifestPath = $productionManifestPath
        ModelRepairAttempts = $modelRepairAttempts
    })
}
