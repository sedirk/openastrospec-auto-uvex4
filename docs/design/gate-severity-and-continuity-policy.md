# Gate severity and observation-continuity policy

Status: implementation policy, 2026-08-28. This document does not supersede
the frozen ownership or observatory-safety ADRs.

## 1. Purpose

The automatic observer exists to finish scientifically useful observations,
not to maximize the number of preconditions it can reject. Every gate must
therefore answer two separate questions:

1. may the state machine advance; and
2. how prominently must the condition be reported?

`GateDisposition` answers the first question. `GateSeverity` answers the
second. The supported meanings are:

| Result | State-machine effect | Operator effect |
| --- | --- | --- |
| `Passed + Info` | advance | ordinary timeline/evidence entry |
| `Passed + Warning` | advance | visible `警告/继续`, durable evidence and final-run warning summary |
| `Failed + Error` | pause | error notification, no next physical/science action |
| `Indeterminate + Error` | pause | required action evidence is absent or contradictory; do not guess |

A warning must never be implemented as a failed/indeterminate gate plus a
manual Resume requirement. An error must not be hidden by changing only its
label or colour.

## 2. Minimal sufficient blockers

A condition may pause the production route only when at least one of the
following is true:

1. the next command could reach the wrong physical device or violate the
   single-owner boundary;
2. the next mount/roof/cover/UVEX motion could exceed a commissioned movement,
   horizon, identity or environmental-safety boundary;
3. explicit fresh evidence says the action is unsafe (for example rain, an
   unsafe Safety Monitor, a closed/error roof or a closed/error optical cover);
4. the program no longer has the same-run, immutable WCS/slit/guide evidence
   required to place and hold the target on the slit;
5. a new ATR exposure would be knowingly unusable, such as clipping with no
   lower safe tier left, loss of guide authority, or a camera not ready to
   expose; or
6. a required terminal cleanup action was configured but could not be
   attested.

Configuration syntax errors remain startup errors when they make the requested
physical action ambiguous. They are not observational-quality gates.

## 3. Warning-and-continue conditions

The following conditions are explicitly non-blocking in the production route:

- QHYminiCam8M/GS350 service, camera, focus metric, WCS witness, acquisition or
  synchronized-photometry loss. The QHY branch is isolated and the ATR/G3/UVEX
  spectroscopy route continues. The final manifest records that simultaneous
  photometry was unavailable.
- QHY frame-quality failures. Frames are retained/flagged according to the QHY
  service policy; they do not pause spectroscopy.
- ATR target/sky contrast or single-frame SNR below the preferred value when
  the frame is not clipped. The exposure ladder continues upward; at the
  longest safe tier the frame is retained for stacking with a warning.
- A local G3 source-count, stellar-core or morphology heuristic rejecting a
  frame that PL3 formally solves within the commissioned optical envelope.
  Local morphology is telemetry and recovery classification, not a second
  plate solver.
- Missing/disconnected/metric-incomplete Safety Monitor, Weather, Roof or Cover
  capabilities in explicitly selected weak supervision. Each capability
  degrades independently; any connected adapter's explicit danger still
  blocks.
- High humidity, including 100%, without independent rain/unsafe evidence at
  this near-coastal site.
- N.I.N.A. not exposing pre-capture ATR ROI/readout telemetry. The requested
  settings are rechecked on the immutable captured FITS rather than guessed at
  startup.
- Expected short transitions such as cooler convergence or tracking enable
  while their bounded wait is still in progress.

## 4. Automatic recovery before an error

An error is reported only after the relevant bounded recovery has been tried:

- N.I.N.A. tracking-enable request followed by driver readback wait;
- G3 exposure ladder, with every valid immutable frame offered to PL3 before
  local morphology is considered;
- direct inverse-WCS correction when a formal G3 solution exists;
- overlapping neighbouring-field search when the target field is sparse;
- fresh target-field/slit revalidation after a neighbouring-field return;
- PHD2 native star selection/calibration/settle and fresh lock residual;
- ATR exposure reprobe/backoff after clipping; and
- revalidation after pause or a detected stale action context.

Recovery remains bounded by the locked single-step, cumulative-motion,
attempt, elapsed-time and horizon limits. Exhausting those bounds is a real
error; refusing to enter them because an advisory image heuristic is uncertain
is not.

## 5. Errors that deliberately remain

- duplicate ownership or an exact ATR, G3/PHD2, mount, UVEX or commanded
  environment-adapter identity mismatch;
- explicit Safety Monitor unsafe, rain, commissioned cloud/wind limit, roof or
  optical-cover closed/error state;
- disconnected/parked/untrackable mount, rejected motion, protected-horizon
  violation, exhausted recovery/motion budget or an ambiguous crash-recovery
  ledger;
- changed or missing immutable commissioning/Night Setup/action evidence when
  it authorizes a physical action;
- UVEX COM5/service, slit, grating or M2 mismatch for the selected Night Setup;
- no formal/bounded G3 acquisition result after all applicable recovery, no
  valid physical slit geometry, failed PHD2 exact lock/settle, or guide loss
  before a new ATR exposure;
- ATR identity/setup/cooling/capture failure, or clipping after all safe tiers
  and bounded attempts are exhausted; and
- inability to attest a configured required PHD2 stop, cover close, mount park
  or roof close during terminal finalization.

These errors are not claims that the observatory is fragile. They are the
smallest set for which continuing would change the requested physical system,
cross an explicit safety boundary, lose target/slit provenance, or knowingly
produce invalid new science data.

## 6. Review rule

Every new gate must be added to this classification in the same change. Its
test must prove either that a warning advances without pausing or that an error
prevents the specific unsafe/invalid next action. A diagnostic metric with no
defined adverse next action defaults to telemetry, not a blocker.

