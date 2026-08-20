# ADR-0004: Optional, versioned wide-field-to-slit-field transfer

**Status:** Accepted; clarifies the acquisition hand-off in baseline sections 5.1 and 5.2  
**Date:** 2026-08-18  
**Decision owners:** Observatory owner and UVEX-ADV project

## Context

The GS350/QHY path and the C11/G3 slit-field path are only roughly coaxial. A
measured relation between them can reduce the number of G3 acquisition attempts:
after QHY coarse centering, the mount can make one bounded pre-positioning move
that is predicted to place the target in the G3 field.

That relation is not an equipment constant. Differential thermal expansion,
flexure, mount pointing error, hour angle, declination, pier side, camera
orientation, and removal or reinstallation of either optical train can change it.
The software is also intended to support other installations. Compiling the
current observatory's offset or matrix into code, treating a remembered value as
a default, or silently reusing a stale measurement would therefore be incorrect.

The original observing workflow remains a complete acquisition path without this
optimization: QHY performs the wide-field solve and coarse centering; G3 then
captures and attempts a direct fine-field solve. If the G3 field contains too few
usable stars, the mount follows a bounded local search, capturing and solving at
each point, until a solution is found or the declared limits are exhausted. Once
the target is identified, a separate G3-pixel-to-mount model closes the final loop
onto the measured slit locus.

## Decision

### Two different models

The implementation must keep these models separate in types, configuration, UI,
evidence, and validity checks:

1. **Wide-to-slit-field transfer** (`QhyToG3Transfer`) predicts an optional mount
   pre-positioning correction after a QHY solve and before the first G3 solve.
2. **G3 pixel-to-mount transform** (`G3PixelToMount`) converts a measured G3 target
   displacement into the small corrections used for closed-loop slit placement.

Skipping or invalidating `QhyToG3Transfer` does not authorize the software to
guess `G3PixelToMount`. The latter still needs current commissioned evidence or a
bounded live calibration before automatic slit placement.

### Explicit transfer policy and safe default

Every Night Setup and target run exposes `WideToSlitTransferMode` with these
values:

- `AutoIfValidElseSkip` — the default. Use the optional pre-positioning move only
  when a selected record passes every applicability, uncertainty, and motion gate;
  otherwise record the reason and proceed directly to G3 acquisition.
- `Skip` — visibly omit the pre-positioning move for this setup or target.
- `RequireValid` — require a passing transfer record and enter
  `PausedNeedsAttention` before movement if none is valid. This is an explicit
  specialist policy, not the default.

An absent, expired, incompatible, or high-uncertainty transfer is therefore not a
failure in the default mode. It produces `TransferSkipped` evidence and invokes
the original direct-G3 path. No mode adds a routine confirmation dialog.

### Versioned and editable transfer records

A transfer record is data, never a literal in source code. The configuration and
workflow UI must display, import/export, clone/edit, activate, retire, and
explicitly skip it. Editing creates a new immutable version; it never rewrites the
record already referenced by a running observation.

Each record contains at least:

- calibration ID, schema/model kind, version, creator/method, creation and
  verification UTC, source evidence hashes, sample count, and superseded record;
- coefficients with explicit tangent-plane units, reference sky position and
  orientation, fit residuals/covariance, prediction uncertainty, and the maximum
  uncertainty allowed for use;
- stable QHY and G3 camera identities, GS350 and C11 optical-train identifiers,
  mount identity, ROI/binning/orientation, and an explicit `InstallationEpochId`
  that changes after removal, reinstallation, collimation, or other optical
  realignment;
- pier side and the measured/applicable ranges of hour angle, declination,
  altitude/azimuth, temperature, and time, using `null` to mean “not measured”
  rather than “unlimited”;
- `ValidFromUtc`, `ValidUntilUtc`, maximum pre-positioning move, and any additional
  per-step, cumulative, horizon, settle, and reversal restrictions.

The editor must show the effective record, coefficients, predicted move,
uncertainty, age, hardware fingerprint, applicability ranges, and the exact reason
for `Valid`, `Invalid`, or `Skipped`. A manual numeric change is identified as a
manual override with author, UTC, reason, and provenance; it is not silently
promoted to commissioned status.

The transfer is invalidated or skipped when any bound identity or configuration
changes, the installation epoch changes, the pier side or applicability range does
not match, the record expires, prediction uncertainty exceeds its limit, the
predicted move exceeds a motion limit, a meridian flip occurs without a compatible
record, or a fresh G3 solve disagrees beyond the residual threshold. The system may
derive a new candidate version from successful QHY/G3 solve pairs, but must retain
the source samples and old version and must pass the same quality gates before the
candidate can become active.

### Direct G3 acquisition and bounded search

After QHY coarse centering, and after the optional pre-positioning move when one is
actually used, the coordinator always captures a fresh G3 frame and attempts a
direct solve. G3 exposure/gain/detection ladders are versioned equipment settings;
they are not universal constants. Low star count, clouds, saturation, or poor focus
must remain visible quality results.

When a direct G3 solve fails or the solved field does not yet contain the requested
target, the coordinator may execute a deterministic square, spiral, or configured
local search around the saved search origin. It must:

- define maximum step, radius, cumulative motion, attempt count, elapsed time, and
  worst-case horizon allowance before the first move;
- retain every G3 frame, solve result, commanded offset, settle result, and current
  displacement from the origin;
- recheck pause/cancel, mount state, horizon, environment, and data-persistence
  gates before every new move or exposure;
- never reuse an invalid wide-to-slit transfer as hidden search guidance; and
- return to the declared origin on exhaustion when the return remains safe, or
  pause with the actual final position and reason when it does not.

A successful solve must identify the catalogue target using WCS and temporal
continuity before the independent `G3PixelToMount` loop moves it onto the current
measured slit. Exhausting the search enters `PausedNeedsAttention`; “until found”
never means unbounded movement.

### Run evidence

The observation manifest records the selected mode, whether the optional move was
used or skipped, record ID and hash, applicability decision and reasons, predicted
and commanded corrections, prediction uncertainty, post-move G3 residual, any
new calibration sample, search origin/pattern/limits/attempts, and final outcome.
Raw acquisition frames remain immutable.

## Consequences

### Positive

- A fresh, applicable model can place the target in the G3 field on the first
  attempt without making that optimization a prerequisite.
- Thermal drift, flexure, pier-side changes, realignment, and deployment on another
  telescope become explicit validity questions instead of hidden systematic error.
- The original QHY-to-direct-G3 workflow remains testable and usable when no
  trustworthy cross-optical calibration exists.
- Operators can see and change policy or calibration data without source edits,
  while each observation remains reproducible.

### Costs and risks

- The plugin and commissioning schema need a new transfer record distinct from the
  existing G3 pixel-to-mount transform.
- Building a useful adaptive model requires solve pairs over representative
  temperatures, sky positions, pier sides, and installation epochs.
- Bounded G3 search is slower than a good pre-positioning prediction and needs
  explicit horizon/time budgeting, but it is the required fallback rather than a
  guessed constant.

## Relationship to earlier decisions

The frozen baseline already states that the optical paths are not assumed rigid or
coaligned, that their transformations are measured and pier-side/flexure dependent,
and that direct G3 solving with bounded local recovery is permitted. This ADR makes
the optionality, safe default, editability, invalidation, and fallback semantics
explicit; no frozen baseline text or hash changes are required.

ADR-0001 remains authoritative for device ownership, ADR-0002 for automatic
progression and operator pause, and ADR-0003 for measured slit geometry and the
three independent focus domains.
