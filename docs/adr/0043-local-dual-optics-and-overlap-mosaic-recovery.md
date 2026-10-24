# ADR-0043 — Local dual-optics and overlapping-mosaic acquisition recovery

- Status: Accepted; implementation and installation do not constitute on-sky commissioning.
- Date: 2026-10-24
- Authority: the owner explicitly requests production integration of dual-optics
  differences and mosaic solving, resolution of insufficient overlap and local
  model validation, historical regression, and installation without observing.
- Narrowly supersedes ADR-0004/0008/0030's hint-only restriction for the locally
  validated acquisition fallback below. No device ownership changes.

## Problem

The October 23 record contains useful same-pointing QHY/G3 pairs, but they only
affected solver hints. A 300 arcsecond search step can lose the short detector
dimension, and a roughly 747 arcsecond correction disconnected two otherwise
usable frames. Independent registration also exposed a false isolated pixel
candidate. Repeating the same exposure ladder cannot repair missing sky overlap.

## Decision

### One production acquisition path

Both frontends continue through the shared G3 acquisition workflow. A new field
without a validated optical anchor first uses direct formal solving. A freshly
registered image may transport an existing formal anchor before another full
solve, avoiding avoidable capture-to-command latency. Completed optical misses may use a qualified local dual
model, independently registered neighbouring frames, or a newly solved derived
mosaic. Optional failure falls back inside the original acquisition envelope;
it is not a new safety stop or a separate observing engine. Owner, cancellation,
weather, horizon, actual stopped state and original deadlines remain authoritative.

### Local dual-optics model

Use immutable, hash-checked same-pointing formal QHY/G3 samples. Bind stable
camera/optical/mount identities, installation epoch, exact setup and commissioning
hashes, detector geometry, binning, gain, parity, pier side and focus state. Old
samples without these proofs are diagnostic only, not silently activated.
Only past samples may train a prediction: keep an independent, later holdout,
distinct source frames, spatial/temporal applicability and empirical uncertainty.
Do not select away a discrepant holdout or reuse future samples in a causal replay.

The output is a locally applicable predicted G3 centre/scale/orientation, not a
formal solution of the current image, a global Active calibration, or a fine
pixel-to-mount model. It must be bound to a fresh stationary QHY WCS and a fresh
G3 image. Accepted uncertainty fits the existing recognition envelope. A current
formal G3 disagreement invalidates the local model for the runner. A subsequent
candidate does not overwrite the immutable model decision or original samples.

Only after at least three applicable, independently validated samples are found
may this fallback request up to two stationary quick QHY witnesses per runner;
an applicable existing fresh witness may replace one. Requests use the existing
native worker owner, precharged attempt count, checked terminal cleanup and
original return/time clock. No additional mount motion or budget is created.

This inference may authorize a bounded coarse acquisition correction through the
existing G3 ledger and N.I.N.A. mediator; it never issues a separate QHY move or
replenishes a budget. A measured target and an independent new short-frame pair
are required before fine placement. A model point alone cannot authorize science
or substitute for measured slit arrival. The legacy optional pre-positioning
setting remains separate; `Skip` still skips that pre-G3 move.

### Connected overlapping fields and mosaic solving

Derive maximum adjacent sampling distance from actual detector dimensions and
commissioned/validated plate scale, with uncertainty on both endpoints. Unknown
orientation uses the conservative short dimension. Tighten the configured search
step and coarse correction as needed to preserve useful overlap; never increase
their configured maximum. Every intermediate exposure and movement consumes the
original action/time/distance budget, including the full original return reserve.
If the smaller steps cannot fit, report an exhausted target, not a new budget.

Register real pixels with a geometrically distributed fit and independent holdout
stars. Reject an ambiguous equally supported mapping, parity/scale/rotation or
identity change, insufficient holdouts and inadequate overlap. Exclude the target
region when its position is known, so its own desired location does not verify
the transform. Mount coordinates alone cannot manufacture registration.

A bounded in-memory graph belongs to one runner/owner epoch and geometry. It may
transport a formal WCS anchor through validated image edges, or compose actual
pixels into a new derived mosaic and run the existing formal solver on it. Parent
raw files stay immutable; derived FITS, source/edge hashes, native solver outcome,
projection back to the current image and accumulated uncertainty are retained.
The current-frame result is labelled inferred/registered, never a successful
native solve of a file that actually failed. No graph survives a focus/owner/
geometry/pier change or restart as motion authority. Missing overlap is an optical
miss, not permission to join images using commanded positions.

### Exposure timing, polar drift and the wide-field optical witness

The owner's additional requirement is explicit: polar-alignment error can move
the image while the mount's reported coordinates remain unchanged. Record the
actual capture request/completion bracket and requested/read-back exposure time;
use a FITS shutter timestamp only when consistent with that native bracket.
Maintain earliest/latest possible exposure midpoint, not a fabricated timestamp
at the end of processing. Include exposure smear, download, registration and
solver delay. Prefer recent adjacent frames and an already validated image
anchor; do not intentionally wait through the whole solve ladder before using it.

Initial realtime quality windows are 45 seconds for the current midpoint's
worst-case age, 60 seconds between adjacent midpoints and 120 seconds for a
connected set. They do not extend the original acquisition/return/night deadline.
Recheck after solving and before consuming an inferred position. Outside these
windows, retire only the optional inference and retain the ordinary bounded
route. Offline spatial replay may deliberately inspect older records, but must
not claim that those records meet the production temporal acceptance rules.

Two independent stationary QHY formal WCS frames can measure actual sky drift
when G3 contains no useful stars. This is an **optical position witness**, not an
absolute physical encoder: differential WCS error, double-optics flexure, focus,
rotation, scale, shutter-midpoint separation and processing latency are retained
as uncertainty. Apply the validated local double-optics relation at the actual
G3 exposure time, account for displacement since then, and expire at the earlier
of the measured drift/uncertainty envelope and the original G3 freshness limit.
An unchanged mount coordinate is never a zero-drift measurement. A single QHY
image or an unvalidated pair cannot create drift authority. No current G3 star
is required merely to obtain this wide-field drift witness; independent fresh
target/slit evidence remains necessary for subsequent fine placement.

Same-pointing G3 image pairs may also supply a measured drift upper bound from
actual registered translation/rotation, including fit and readback uncertainty.
Without a measured drift rate, registered geometry describes the captured image
only: it cannot extrapolate the image to an invented current instant. All inferred
positions retain explicit warning/provenance and require independent short-frame
target confirmation. Hash-check the complete immutable parent chain at use time;
do not hash a rolling manifest as if legitimate appended evidence were corruption.

### Closed-loop and terminal boundaries

Recheck fresh owner/safety/current image binding before consuming inference and
before every command. Use the same durable write-before-motion intent, origin,
charged counters and earliest clock. The next field must independently validate
the prediction; final handoff requires new target/slit measurements, then the
existing PHD2 and ATR checks. A rejected optical model/registration may use the
remaining direct/search route; after exhausted quality and confirmed terminal
stop, apply the saved skip-target policy. Unsafe, unconfirmed stop, ownership or
integrity failures are never converted into an optical miss or successful return.

## Verification and staged acceptance

- Drive the real shared fallback dispatcher with finite device/solver ports,
  including direct miss → local/registered/mosaic inference → fresh confirmation
  → original-budget correction/handoff; test ordering and late vetoes.
- Use historical successful and failed immutable FITS/pairs, verify source hashes,
  separate causal prediction from retrospective cross-validation, and preserve
  unsuccessful solves/no-overlap counterexamples.
- Test unsafe/cancel, stale binding, changed focus/owner/pier/geometry, corrupt
  source, false registration, insufficient overlap, depleted return/time/actions,
  and absence of a usable model. No quality miss may create an extra retry budget.
- Generate a production quality failure and execute the next native target,
  with independent stop/unsafe counterexamples that prevent continuation.
- Run repository release checks, exact artifact deployment and idle runtime
  acceptance. This request authorizes no real exposure, focus or mount/roof
  movement and no automatic start/resume. Live acquisition remains separately
  commissioned through the normal validated frontend.
