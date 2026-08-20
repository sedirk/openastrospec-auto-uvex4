# ADR-0005: PHD2-calibration-guided slit placement with versioned fallbacks

**Status:** Accepted; supersedes the fixed acquisition-before-guiding ordering in baseline sections 5.2, 5.3 and 7, and clarifies the final-placement authority in ADR-0004  
**Date:** 2026-08-19  
**Decision owners:** Observatory owner and UVEX-ADV project

## Context

The target must ultimately be placed on a slit only a few detector pixels wide.
The software therefore needs a current relationship between G3 detector motion
and mount correction. Two possible ways to obtain that relationship were
evaluated:

1. an independent `+E/-E/+N/-N` probe sequence fitted by UVEX-ADV; and
2. the calibration that PHD2 already measures for the same G3 camera and mount,
   followed by PHD2 runtime lock-position shifts.

Neither relationship is a permanent property of the installation. Camera
rotation, ROI, binning, pier side, backlash, guide rate, flexure, thermal change,
mount service, and optical reassembly can change it. A plate-solved sensor angle
describes orientation, but it does not measure guide-pulse rate, backlash,
directional asymmetry, settling, or whether a small correction actually reached
its requested location.

The site also has two common degraded cases:

- polar alignment is serviced infrequently, so a calibration may be measurably
  non-orthogonal while still delivering useful bounded corrections; and
- an ultra-bright target may be the only coherent object in the G3 field. In
  that case a longer exposure can be unsuitable for centroiding even though a
  short exposure can guide directly on the science target.

A single hard-coded orthogonality threshold, a compiled four-direction matrix,
or a workflow that insists on an independent guide star would turn these common
conditions into unnecessary dead ends. Conversely, accepting a calibration or
ghost image merely because it is the only available signal would invent motion
authority and target identity.

## Decision

### Fine-motion authority selection

The normal final-placement authority is the **current, quality-classified PHD2
calibration for the exact G3/mount topology**, used through bounded PHD2 runtime
lock-position shifts. This reuses the component that already owns G3 and guide
pulses and avoids a second process issuing competing pulse commands.

An independent four-direction `G3PixelToMount` fit remains a supported,
versioned diagnostic and fallback. Its probe amplitude, direction sequence,
validity bounds, coefficients, uncertainty, and motion limits are configuration
and evidence, never literals inferred from the present installation. A changed
camera orientation, ROI, binning, PHD2 profile, mount identity, guide rate,
installation epoch, pier side, or failed validation invalidates it.

Plate-solve rotation is seed-only. It may predict detector orientation or help
initialize a fit, but it cannot by itself authorize slit-placement motion.

The selected authority and every rejected alternative are shown in the N.I.N.A.
workflow UI and retained in the run manifest. The independent four-direction
fit is never silently substituted for the unrelated `QhyToG3Transfer` optical
axis model.

### PHD2 calibration quality is graded, not one-dimensional

PHD2 calibrations are retained and ranked as candidates rather than reduced to
one `orthogonality <= 10 degrees` boolean. A versioned policy classifies each
candidate using at least:

- exact profile, G3 stable identity, mount identity and registry/runtime binding;
- camera ROI, binning, orientation fingerprint, installation epoch and pier side;
- calibration age, declination and both axis parity values;
- finite RA/Dec angles and rates within commissioned physical ranges;
- orthogonality error, rate ratio, directional response and any measured
  forward/reverse closure or backlash evidence;
- a current guide/settle epoch; and
- fresh, post-correction target/slit and guide/lock residuals.

The policy exposes named grades such as `Excellent`, `Qualified`,
`DegradedSupervised`, and `Rejected`, with explicit reasons and effective
limits. Numeric boundaries are versioned commissioning data. Orthogonality is a
quality penalty and risk indicator, not by itself proof of failure. Polar
misalignment may contribute to poor guiding, but a measured calibration-axis
orthogonality error can also reflect backlash, mount response, guide-rate
asymmetry, optics, or measurement conditions; the software does not label the
cause without evidence.

The best currently usable candidate is selected. A degraded candidate receives
smaller stages, tighter cumulative/time bounds, mandatory fresh residuals after
every stage, and a visible downgrade. Grossly invalid identity, missing parity,
non-finite or implausible rates, wrong topology/pier side, failed settle, unknown
motion result, or a residual that worsens beyond policy remains rejected.
Degraded operation never silently becomes an unattended science-quality claim.

### Guide first, then shift the lock when appropriate

When PHD2 is the selected fine-motion authority, the sequence may select a guide
source and establish a current settled guide epoch **before** final slit
placement. It then computes, from one fresh immutable G3 frame in one declared
coordinate domain:

```text
desiredGuideLock = guideCentroid + (slitAcquisitionPoint - targetCentroid)
```

Each exact lock-position stage is bounded and written as durable intent before
dispatch. PHD2 lock readback only proves that the runtime reference changed; it
does not prove that the star or telescope reached the requested place. Every
stage must therefore be followed by operation-bound settle evidence and a new
immutable G3 frame. The target, slit and guide centroids are remeasured, and the
next stage is planned from the fresh residual. A failed or ambiguous stage uses
the same durable ledger to return toward the declared runtime-lock origin when
safe, or enters `PausedNeedsAttention` without an automatic retry.

No registry/profile lock overlay is rewritten with the current slit point. The
slit acquisition point remains a versioned detector-geometry record; the PHD2
lock position remains per-run runtime state.

### Ordinary and direct-target guiding modes

The preferred mode selects an independent, unsaturated off-slit guide star. Its
identity, guard distance, morphology and fresh lock residual must pass.

If no independent star is usable and the science target itself is uniquely
identified, the run may use `DegradedDirectTargetGuiding`. This mode uses the
shortest commissioned G3 exposure that preserves a stable centroid, explicitly
binds `guide == target`, applies the same staged-lock and fresh-residual rules,
and records that atmospheric image motion and slit-crossing flux modulation may
be worse. A short direct-target guide frame is not focus evidence and does not
replace a long-exposure WCS frame.

### Deterministic ghost assistance and non-visual fallback

Optical ghosts may assist a bright-target locator only when an ordinary program
can match a versioned, installation-specific template with multi-frame common
motion, unique geometry, uncertainty, exposure range, orientation and
installation-epoch checks. The ghost is auxiliary evidence; it cannot be the
sole default identity or motion authority. With no passing template, the
software uses the deterministic fallback: fresh G3 long-exposure plate solving,
then bounded small moves and re-solving, followed by N.I.N.A. WCS centering and
fresh validation. No large-model visual judgement is part of the production
workflow.

## Consequences

### Positive

- Current PHD2 calibration measures the same detector/mount response used for
  guiding and can move either an off-slit guide star or the target itself without
  a competing pulse-command owner.
- A slightly imperfect but demonstrably useful calibration is no longer thrown
  away solely because it crossed one arbitrary threshold.
- Camera rotation and reassembly invalidate both primary and fallback mappings
  through explicit topology fingerprints instead of silently corrupting them.
- The workflow retains bounded WCS recovery when neither a guide calibration nor
  a ghost template is trustworthy.

### Costs and risks

- Guiding and slit placement are now a coupled closed loop, so settle-event
  attribution, single command authority, durable intent, and fresh-frame
  residuals are mandatory.
- PHD2 protocol events do not carry the initiating RPC request identifier. The
  coordinator must bind them to a local operation epoch and invalidate that
  epoch on external lock changes, dither, pause, disconnect, configuration
  change, or operator takeover.
- Direct-target guiding is scientifically degraded and can modulate slit
  throughput; manifests and downstream reduction must retain that mode.
- Calibration grades require commissioning across representative pier sides,
  declinations, seeing, guide rates and mechanical states rather than a single
  universal angle threshold.

## Relationship to earlier decisions

ADR-0001 remains authoritative for sole ownership: N.I.N.A. owns the telescope
mediator, while PHD2 remains the sole G3 owner and the only guide-pulse authority
during PHD2-calibration-guided placement. ADR-0002 still requires automatic
progression on passing gates and an inspectable pause on indeterminate results.
ADR-0003 remains authoritative for measured slit geometry. ADR-0004 remains
authoritative for the optional, separately versioned QHY-to-G3 pre-positioning
model; this ADR supersedes only its assumption that the independent
`G3PixelToMount` model is always the final-placement authority.

