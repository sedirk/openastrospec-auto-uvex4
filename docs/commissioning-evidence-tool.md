# OpenAstroSpec Auto — UVEX4 commissioning evidence tool

This tool produces the immutable evidence files required by real observation
mode without connecting to or controlling any observatory device. It can read
the current user's PHD2 registry profile. It does **not** start PHD2, open a
camera, connect COM5, acquire an image, move the mount, or move UVEX.

The four hashes used by real mode are intentionally different:

- `NightSetupSha256`: SHA-256 of the exact locked Night Setup JSON bytes.
- `Phd2ProfileEvidenceSha256`: SHA-256 of PHD2's canonical registry-field
  payload, not the exported JSON file.
- `PresetSha256`: SHA-256 of the exact schema 4 preset JSON bytes.
- `HardwareFingerprintSha256`: self-hash of the seven identity/binding fields
  in the fingerprint object. Its serialization is byte-compatible with
  `RealCommissioningPresetLoader`.

Every primary evidence JSON also has a `.sha256` file for its file hash and a
`.bindings.json` file containing the values that the N.I.N.A. Profile must bind.
Files default to `%ProgramData%\UVEX-ADV\commissioning`; `-OutputPath` selects
another directory. Existing evidence is never replaced; create a new versioned
artifact when measurements or bindings change.

The generated `NinaProfileValues` map primarily contains values that the current
schema-4 commissioning/Night-Setup-schema-2 loaders cross-check against hashed evidence. It also writes
the two explicit PHD2 JSON-RPC runtime-name expectations (`G3M2210M` and
`On-Step (ASCOM)`), which are deliberately separate from the registry/menu
names (`ToupTek Camera` and `OnStep Telescope (ASCOM)`). Those runtime names are
not claimed as registry evidence: the live PHD2 identity call must match them
exactly before capture or guiding.

The map deliberately omits G3/QHY focal lengths, pixel sizes, UVEX position
tolerance, G3 local-search limits, and the independent
`QhyCoarseCenteringLimits` schema-1 envelope. The current preset schema does not
bind those values, so this tool must not make them look commissioned. In
particular it never derives the C11 focal length from “CCDT67”, copies a
focal-length seed, guesses a tolerance, or converts the fine G3/slit `Motion`
record into a QHY coarse-motion limit. Those inputs require their own measured
evidence and a future loader/schema extension; until then they must be entered
explicitly and remain locked in the run action hash/manifest. Consequently the
map explicitly leaves `ObservationUseRealMode`, `RealModeCommissioned`, and
`AllowDegradedSupervisedScience` set to `false`: it fills evidence-backed
bindings and runtime identity expectations but does not itself authorize
hardware actions.

The environment booleans also produce a portable safety-mode binding. When
`RequireSafetyMonitor`, `RequireOpenDomeOrRoof` and `RequireWeatherData` are all
`false`, the generated map selects `OperatorWeakSupervision`, sets
`WeakSupervisionEnabled=true`, and makes the N.I.N.A. optical-cover adapter
optional. Missing Safety Monitor, roof, weather and optical-cover adapters then
remain warning-only; a connected adapter that explicitly reports unsafe, rain,
closed or error still blocks the run. When the three environment requirements
are all `true`, the generated map selects the complete N.I.N.A. safety stack and
requires optical-cover evidence. Neither case sets `RealModeCommissioned=true`;
the plugin may mark a complete package statically verified only after checking
all hashes, internal references and identities.

## 1. Export and verify the current PHD2 profile

All expected values are mandatory. A mismatch, ambiguous USB binding, missing
registry field, or changed profile aborts the export.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\export-phd2-profile-evidence.ps1 `
  -ProfileId 2 `
  -ProfileName 'c11+ccdt67+slit+2210' `
  -CameraName 'ToupTek Camera' `
  -CameraStableId '<exact persisted G3 USB instance path>' `
  -MountName 'OnStep Telescope (ASCOM)' `
  -Binning 1 `
  -GainPercent 100
```

The `Phd2ProfileEvidenceSha256` printed by this command is the value required by
the plugin. `EvidenceFileSha256` protects the exported evidence file itself.

Use `scripts/test-commissioning-evidence.ps1 -Phd2 ...` to re-read the registry
and prove that it still matches both hashes and all expected fields.

## 2. Create a locked Night Setup

Prepare a JSON definition containing every `NightSetupRecord` schema 2 field.
Nullable or unavailable measurements must be written explicitly as `null`; a
missing property is rejected. The definition must include measured values for
slit position/width, grating steps, M2 steps, camera identities/settings, ROI,
dispersion direction, wavelength limits, calibration strategy, horizon policy,
safety capability, second-order risk state, and exactly three independent
`FocusDomains`. `G3SaturationAdu` must also be explicit: the commissioned
G3M2210M/PHD2 path uses 4095 because its native 12-bit plateau is stored in a
16-bit FITS container; the FITS container bit depth is not substituted for this
measured clipping level. The tool does not infer focal length, slit, UVEX position, ROI,
filter state, safety state, a historical focus position, a USB instance, or a
USB topology path.

Each focus-domain entry explicitly contains `Role`, `Owner`,
`LogicalDeviceId`, `PhysicalBinding`, `StartPositionSteps`, bounded `Limits`,
`Metric`, `VerifiedUtc`, optional legacy `ValidUntilUtc`, and `Confidence`. The metric also
binds its source camera identity and immutable evidence SHA-256. The role
contract is:

| Role | Required logical/physical binding | Required evidence path |
|---|---|---|
| `C11Main` | owner `N.I.N.A.`, logical ID `ASCOM.StarFocuserPro.Focuser`, physical Gemini, COM8, and the exact CH340 device instance | `G3StellarShape` from the locked G3 identity |
| `Gs350WideField` | explicit versioned owner, logical ID `ASCOM.ToupTek.AAF`, physical ToupTek AAF, endpoint `AUTOFOCUSER`, exact `VID_0547&PID_14AD` device instance, and measured USB `TopologyPath` | `QhyStellarShapeAndPlateSolve` from the locked QHY identity |
| `UvexSpectral` | owner `UvexAdv.Service`, logical ID `UVEX4.M2`, physical UVEX M2, COM5, and the exact UVEX/CH340 device instance | `AtrSpectralLineWidth` from the locked ATR identity |

G3 observes the C11 focal plane before light enters the UVEX slit. Therefore a
G3 star-shape failure belongs to `C11Main` and can only implicate the physical
Gemini/Star Focuser Pro path. UVEX M2 is downstream of the slit and cannot
improve that gate. Likewise, QHY evidence cannot authorize Gemini or M2, and
ATR spectral-line evidence cannot authorize either external focuser. Assigning
a mechanism, role, metric kind, or metric-source camera to another optical path
is rejected.

`HardwareInstanceId` must identify one concrete device instance, not merely a
family such as “CH340” or `VID_0547&PID_14AD`. `Gs350WideField.TopologyPath` is
mandatory and is compared independently, so moving or replacing the AAF fails
compatibility even if Windows exposes the same driver name. All range,
single-move, cumulative-move, approach-direction, and backlash values are
human-supplied measurements. A zero single/cumulative limit with approach
direction `None` records a manually locked focus domain without commissioning
automatic movement.

For example, the required shape of one entry is:

```json
{
  "Role": "Gs350WideField",
  "Owner": "ManualOperator",
  "LogicalDeviceId": "ASCOM.ToupTek.AAF",
  "PhysicalBinding": {
    "Mechanism": "ToupTekAaf",
    "ConnectionEndpoint": "AUTOFOCUSER",
    "HardwareInstanceId": "USB\\VID_0547&PID_14AD\\<exact-instance>",
    "TopologyPath": "<measured USB topology path>"
  },
  "StartPositionSteps": 0,
  "Limits": {
    "MinimumPositionSteps": 0,
    "MaximumPositionSteps": 1,
    "MaximumSingleMoveSteps": 0,
    "MaximumCumulativeMoveSteps": 0,
    "ApproachDirection": "None",
    "BacklashCompensationSteps": 0
  },
  "Metric": {
    "Kind": "QhyStellarShapeAndPlateSolve",
    "SourceCameraStableDeviceId": "<exact locked QHY identity>",
    "Value": 1.0,
    "Unit": "<measured metric and unit>",
    "EvidenceSha256": "<64 hexadecimal characters>"
  },
  "VerifiedUtc": "<UTC measurement time>",
  "ValidUntilUtc": null,
  "Confidence": 1.0
}
```

The numeric values above illustrate JSON shape only and are not commissioned
defaults. Copying them without measurements is invalid evidence. A legacy
non-null `ValidUntilUtc` must follow `VerifiedUtc`, but crossing that date does
not invalidate otherwise unchanged installation/focus evidence. Runtime owner,
stable identity, topology, exact position and fresh live-metric checks are the
state-based invalidators.

Compute the definition hash, then create the locked record:

```powershell
$definition = 'C:\commissioning\night-setup-definition.json'
$definitionSha = (Get-FileHash -LiteralPath $definition -Algorithm SHA256).Hash
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-night-setup-record.ps1 `
  -DefinitionPath $definition `
  -DefinitionSha256 $definitionSha
```

The definition uses enum names such as `BlueAtLeftRedAtRight` and
`CompactEmissionLineObject`. `TelescopeFocusPositionSteps`, camera
`TemperatureC`, `HorizonPolicy.SampleInterval`, and
`HorizonPolicy.AzimuthProfile` must still be present when their value is `null`.
In schema 2, `TelescopeFocusPositionSteps` is a deprecated compatibility mirror:
when non-null it must equal `C11Main.StartPositionSteps`, and no compatibility
gate uses it as proof for GS350 or UVEX focus. `M2PositionSteps` must equal the
independent `UvexSpectral.StartPositionSteps`.

Schema 1 files remain JSON-readable for audit and migration. Validation reports
them as non-commissionable, and the real compatibility gate is indeterminate
because one telescope step value cannot attest three focus domains. The tool
does not migrate schema 1 by copying that value, querying devices, or guessing
missing history; create a new schema 2 definition from fresh evidence instead.

Validate a locked output independently:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-commissioning-evidence.ps1 `
  -NightSetup -InputPath '<night setup JSON>' -Sha256 '<NightSetupSha256>'
```

## 3. Create a schema 4 commissioning preset

The measurement definition is schema 3 input evidence, not a commissioned
preset. It must explicitly contain:

- exact G3 exposure, gain, binning, effective saturation ADU and WCS parity;
- an optional legacy `ValidUntilUtc` (normally `null`) and a UTC PHD2 calibration timestamp;
- measured slit geometry plus an existing evidence file and its SHA-256;
- `SlitWheelIdentity` with measurement model
  `UVEX-DARK-APERTURE-TWO-EDGE-HDR-V1`, locked short/long exposure times and
  shared edge-PSF parameters, plus four independently measured wheel
  positions. Each entry carries its exact label/nominal micrometre identity,
  physical dark-aperture edge-to-edge width (never reflected-ridge FWHM),
  direct/shared-PSF resolution, reflected-edge-to-aperture-centre offset,
  secondary-edge amplitude ratio, empirical uncertainty, timestamp, combined
  derived evidence path/hash and distinct immutable short/long source evidence
  paths/SHA-256 values, detector dimensions, installation epoch and
  statistical classification limits;
- at least four non-zero, bounded `MountCalibrationSample` values spanning two
  image directions;
- transform residual/condition limits, motion limits and environment gates;
- numeric `FineMotionAuthority`, a non-null complete `Phd2SlitPlacement`,
  numeric `GhostAssistanceMode`, and an explicit `GhostAssistance` object or
  `null` according to that mode.

The enum-shaped fine-motion fields are deliberately JSON numbers because the
production plugin loader uses default `System.Text.Json` enum handling. String
names in these fields are rejected before a preset is written:

| Field | Numeric values |
|---|---|
| `FineMotionAuthority` | `0` independent transform, `1` PHD2 lock shift, `2` prefer PHD2 then independent |
| `Phd2SlitPlacement.CoordinateDomain` | `0` full sensor, `1` ROI-local |
| `Phd2SlitPlacement.RotationAuthority` | only `1`, qualified PHD2 calibration; plate-solve angle remains seed-only |
| `Phd2SlitPlacement.GuideMode` | `0` off-slit, `1` degraded direct target, `2` prefer off-slit then direct target, `3` prefer direct target then fall back to an off-slit star |
| `GhostAssistanceMode` | `0` Skip, `1` AutoIfValidElseSkip, `2` RequireValid |

The four slit entries are not accepted merely because their labels reproduce
the declared `1=300, 2=15, 3=25, 4=35 µm` table. Their independent LED
measurements must be statistically distinguishable, and their measured pixel
widths must have the same strict rank order as `15 < 25 < 35 < 300 µm` on the
locked G3 geometry. This catches a wheel assembled with swapped physical slits
even when the definition still carries the expected human names. The tool does
not assume a linear pixels-per-micrometre scale and never repairs the mapping
automatically.

The long HDR exposure may saturate the reflecting metal. That saturation is
recorded rather than rejected when the black aperture leaves a measurable dip
in intensity or saturation coverage. A long frame clipped everywhere has zero
dark-aperture dynamic range and is rejected as an upper-bound exposure, even
when the short frame still shows the bright ridge.

Schema 4 always requires `Phd2SlitPlacement`, even for independent-transform
authority. The record must explicitly supply:

- installation/topology: installation epoch, locked topology SHA-256,
  sensor dimensions, ROI, coordinate domain, sensor rotation, qualified
  rotation authority and exact `East`/`West` pier side;
- guide acquisition: selected guide mode, expected exposure, and separately
  measured positive `OffSlitGuidingExposureMilliseconds` and
  `DirectTargetGuidingExposureMilliseconds`;
- bounded motion/recovery: per-stage and cumulative pixels, attempts, elapsed
  and stage time, measurement/safety ages, all lock/slit/acquisition residual
  tolerances, off-slit separation, flux, altitude and axis-rate envelopes;
- deterministic selection: fresh-frame timeouts, target/guide radii and SNR,
  uniqueness, slit extraction limits and maximum residual growth;
- the complete versioned `CalibrationQualityPolicy`, its exact SHA-256, the
  optional measured bidirectional ratios, and all three evidence-completeness
  booleans.

The tool recomputes the topology fingerprint from the locked registry profile,
canonical registry-evidence SHA-256, runtime names `G3M2210M` and
`On-Step (ASCOM)`, stable camera ID, G3 binning, installation/sensor/ROI,
rotation and pier fields. It also recomputes the quality-policy SHA-256 using
the same default JSON bytes as the plugin. Any mismatch, omitted field,
plate-solve-only rotation, unknown pier side, or missing ordinary/direct
exposure aborts without output. Definition-producing software should use
`Phd2SensorTopology.ComputeFingerprintSha256` and
`Phd2SlitPlacementContract.ComputePolicySha256`; neither hash is guessed.

Ghost mode `0` requires an explicit `GhostAssistance: null`. Modes `1` and `2`
require the complete strongly typed schema-1 envelope: calibration and its
content/evidence hashes, match policy and hash, source-extraction policy and
hash, runtime installation/optical/orientation fingerprint, external identity
age and residual gates, and the independent C11/G3 focus-confidence gate. The
tool reuses the deterministic Observatory validators and verifies calibration,
extraction backend/policy, runtime identity, G3/profile/pier/gain/exposure,
detector ROI/binning, validity interval and locked C11 focus evidence. It never
creates a ghost template from a present-night image or an LLM. The preset writer
uses numeric enum JSON specifically so both ghost extractor-kind fields can be
read by the production loader; Night Setup authoring continues to accept its
documented string enums.

Each mount sample has these fields:

```json
{
  "CommandedRaArcseconds": 10.0,
  "CommandedDecArcseconds": 0.0,
  "MeasuredPixelShiftX": 0.0,
  "MeasuredPixelShiftY": 5.0
}
```

The transform is fitted by `MountTransformCalibrator`. Singular,
ill-conditioned, high-residual, over-limit or incomplete samples abort without
writing a preset. Profile seed values are never promoted to commissioned data.
Creation and validation also re-read the current PHD2 registry and require it
to remain byte-canonically identical to the locked PHD2 evidence; this is still
a read-only operation and does not start PHD2 or open G3.

This schema-4 `MountTransform` is specifically the **G3-pixel-to-mount** model
used after a target has been identified in the G3 frame to move it onto the
slit. It is not a GS350/QHY-to-C11/G3 boresight offset and must never be reused
as the optional pre-positioning record described by
[`ADR-0004`](adr/0004-optional-versioned-wide-to-slit-field-transfer.md). The
current commissioning schema has no `QhyToG3Transfer` field. Until a separate
versioned schema, loader, validity gate, and UI are implemented, that optional
intermediate stage remains explicitly `Skip`; direct G3 solving and bounded
local search are the valid acquisition path.

```powershell
$definitionSha = (Get-FileHash '<measurement definition>' -Algorithm SHA256).Hash
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-commissioning-preset.ps1 `
  -DefinitionPath '<measurement definition>' `
  -DefinitionSha256 $definitionSha `
  -NightSetupPath '<locked night setup>' `
  -NightSetupSha256 '<NightSetupSha256>' `
  -Phd2EvidencePath '<exported PHD2 evidence>' `
  -Phd2EvidenceFileSha256 '<EvidenceFileSha256>' `
  -Phd2ProfileEvidenceSha256 '<Phd2ProfileEvidenceSha256>'
```

`scripts/test-commissioning-evidence.ps1 -Commissioning ...` verifies the
preset file hash, all referenced files and hashes, PHD2 evidence self-hash,
Night Setup semantics, fitted transform, state bindings, and canonical hardware
fingerprint. It rebuilds the expected preset from the locked measurement inputs
and requires an exact structural match. The commissioning preset is schema 4:
its exact `NightSetupSha256` and hardware fingerprint commit the complete Night
Setup schema-2 focus bindings, while the PHD2 and optional ghost blocks bind
their own complete policies, identities and limits. A preset does not expire
solely because wall-clock time advanced: equipment-owner identity, installation
epoch, topology, ROI/binning, positions, hashes and current quality evidence are
re-read before authority is granted. Frame/WCS/settle/safety freshness windows
remain strict runtime limits.

Generating these bindings still does not authorize real mode. The emitted
N.I.N.A. values keep `ObservationUseRealMode`, `RealModeCommissioned`, and
`AllowDegradedSupervisedScience` false. `GhostAssistanceMode` exactly follows
the validated measurement definition; Auto/Require is never inferred merely
because a calibration file exists. Until each owner supplies an independently
attestable live focus identity/position snapshot, the focus compatibility gate
remains indeterminate; no adapter substitutes N.I.N.A.'s selected focuser for
the other two domains. One narrow manual-lock exception prevents the first
GS350 stage from becoming unsatisfiable: when both GS350 automatic-move limits
are zero, the live identity and USB topology match, and a QHY frame from the
current locked setup carries a passing, unexpired
`QhyStellarShapeAndPlateSolve` metric plus immutable evidence hash, GS350 may
pass without claiming a live focuser position. The historical
`StartPositionSteps` or locked metric alone never supplies that live proof.
C11/Gemini and UVEX/M2 continue to require real owner-reported positions.

## Offline test

The test projects create synthetic measurement sets only under the test temp
directory and never read the registry or open hardware:

```powershell
.\.dotnet\dotnet.exe test `
  .\tests\UvexAdv.Commissioning.Tool.Tests\UvexAdv.Commissioning.Tool.Tests.csproj `
  -c Release

.\.dotnet\dotnet.exe test `
  .\tests\UvexAdv.Observatory.Tests\UvexAdv.Observatory.Tests.csproj `
  -c Release
```

It covers canonical hash compatibility, complete Skip and calibrated-Auto
schema-4 create/validate chains, production-plugin byte deserialization,
numeric enum encoding, identical tool/plugin PHD2 validation, PHD2 policy and
topology hash rejection, ghost policy-hash and mode/payload mismatch rejection,
required-field rejection, wrong focus role/metric assignment, missing physical
identity, changed GS350 topology, expired focus evidence, legacy-schema
indeterminacy, singular/seed transform rejection, and immutable atomic output
behavior.
