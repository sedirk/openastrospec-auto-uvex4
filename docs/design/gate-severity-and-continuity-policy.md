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

### ADR-0031: independent science consent and target scope (0.4.0.275)

The saved precision-warning probe policy is independent of WeakSupervisionEnabled.
Full environment safety and a measured, honestly labelled degraded probe can coexist.
No weather, roof, dawn, ownership or physical-action gate is weakened by this choice.
The runner's same-frame identity/geometry, current guiding output, finite slit,
acquisition envelope and actual ATR quality remain required.

`GateResult.FailureScope = TargetQuality` rejects the target, not the whole night.
Only reviewed producers set it; exception strings and unknown failures cannot infer
it. The native container still requires saved SkipQualityFailure, coordinator join,
matching terminal code, confirmed idle/retired motion, and no cancellation/unsafe/
night-end condition. Notifications use that same classification and wait for the
durable boundary result. Existing reviewed codes remain backward compatible.

New explicit target-local outcomes are fresh optical window deadline, bounded fresh
slit reacquisition exhaustion, no-motion completion-window exhaustion and pre-ATR
quality-window exhaustion with unchanged epoch and valid identity. Lost/changed
owner, invalid output, identity change, unknown exceptions or unconfirmed checked
stop remain separate non-advancing faults. A rolling optical rejection clears the
previous FIFO and reacquires a complete window under the unchanged deadline.

In 0.4.0.286, slit reacquisition adds context without replacing the original gate,
scope, metrics or evidence. ATR exhausted safe exposure tiers and checked-stopped
strict off-slit native selection are target-quality outcomes. A complete G3 ladder
whose bound, valid frames all report zero coherent sources remains eligible for
the existing three bounded fresh retries and then target skip. Unsupported pixel
buffers, capture/solver exceptions, missing binding or incomplete/mixed ladders
are not evidence of an empty sky and remain run faults. No skip classification
attests physical stopping by itself; the native night owner must still join and
verify its independent terminal boundary before actually running the next target.

In 0.4.0.287, a valid, same-owner G3 guiding-recovery return that cannot fit the
original action, cumulative-motion or elapsed budget is also target-local. This
does not execute a return, change the original clock, persist a new allowance or
claim arrival. The native boundary must confirm stopped owners and retire the old
ledger before a skip. Changed owner/epoch/pier, pending motion, invalid geometry,
position limits and unconfirmed stopping remain run faults, even with an expired
budget. Recovery-declined evidence records the required and available limits.

In 0.4.0.288 this contract applies across G3 centering/search/return, independent
slit corrections, PHD2 placement/rebuild and science continuation. Validate ledger,
binding, finite geometry and clock before classifying ordinary time/action/travel
exhaustion. A spent budget rejects the proposed command; it neither resets that
budget nor asserts physical arrival. Only subsequent independent checked-stop and
authenticated retirement can permit the next native target. Preserve typed gates
through return results and nested recovery exceptions; never replace a device or
ledger fault with a generic, skippable recovery-exhausted/position-timeout code.

Return-window observations distinguish bounded non-arrival or same-owner StarLost
from changed ownership, interrupted/stale frames or missing measured provenance.
All reject further exposure; only the former are target-local. Native terminal
classification reads the current failed stage and matching terminal event, not the
last failed entry in a historical gate dictionary. Recheck cancellation, Unsafe,
night end, dawn and cutoff after coordinator join/stop confirmation. These checks
are shared by both ordinary skip and mount-clock continuation; they cannot revive
a stopped night. See the [cross-layer audit](../reports/2026-10-13-failure-chain-audit.md).

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

### Neighbour-field continuity and target-local failure (ADR-0030 / 0.4.0.270)

A formal neighbouring-field WCS and measured mount arrival may seed fresh target
pixel confirmation without solving the target field again. Do not block entry to
that confirmation because the candidate is beyond the old 20-pixel coarse
circle; use the configured recognition region and remaining fine-motion envelope.
The proposal itself does not prove identity, slit arrival or authorize exposure.
Try short-frame measurement before repeating longer full-field solve tiers.
The purpose at this development stage is useful, honestly flagged experiments,
not absolute scientific certainty before every attempt.

After checked stop and terminal reconciliation, explicit verified-return
`G3_WCS_CENTERING_BUDGET_EXHAUSTED_RETURNED` and
`G3_WCS_CENTERING_RESPONSE_NOT_CONVERGED_RETURNED` are eligible to skip under the
native target's saved policy. This supersedes the blanket wording below that
budget exhaustion always pauses the night. It remains the end of that target's
budget, never a reset or authorization for unlimited movement. Unknown return
codes, failed stop/cleanup, explicit EndNight, and later Safety Monitor Unsafe
remain distinct. Test actual next-target execution and adverse cases.

Same-run QHY/G3 pairs may refine a current QHY-based solver search hint; they do
not become an Active global transfer or an independent pixel-to-mount model.

### Measured post-lock continuation (ADR-0029 / 0.4.0.269)

The historical native post-lock settle requirement below is superseded: a
verified exact-lock mutation continues with three fresh same-session measured
frames in either mode, without restarting guide/settle per microstep. The native
result is not rewritten. Final target/slit position, lost star, failed output,
owner/epoch, physical safety and original motion budgets remain hard requirements.
`PHD2_LOCK_RETURN_POSITION_UNCONFIRMED` means fresh frames did not prove physical
return (or lost continuity/deadline); preserve debt for checked-stop archival.
It is not a synonym for a native settle timeout. Qualified post-lock continuation
does not silently enable supervised-only target/slit quality exceptions.

### Bounded optical guide-precision warning tiers (0.4.0.256)

An explicitly versioned commissioning package can set
`Phd2SlitPlacement.MaximumGuideLockWarningResidualPixels`; omitted/zero keeps the
previous strict policy. The ceiling cannot exceed either 3 pixels or 1.5 times
the preferred guide tolerance. For the owner's 2026-10-10 test, independent
complete optical windows use 2.0, 2.5, then 3.0 pixels (maximum four windows,
unchanged deadline). Every frame must pass the selected tier; no averaging,
cherry-picking or retrospective acceptance of the preceding rejected window.

A wider tier requires same-epoch/lock live guiding with output, no pause or
pending settle, measured guide positions and confirmed target identity. The
accepted warning advances the shared runner, is retained in run evidence and
the placement gate, and uses the same bounded optical ceiling in downstream
qualification/planning. Native post-lock settle still must succeed (up to the
declared ceiling, at most five stable seconds, within 60% of the original stage
time, leaving the rest for optical verification). This changes guide precision,
not target-on-slit completion, exact-lock readback, identity, star-loss/output,
motion/return accounting or observatory safety. It is not ADR-0013's permission
to probe without exact slit completion. A passing slit gate does not retroactively
claim the preferred guide precision. Initial complete-window rejection can only
be skipped after checked stop and independent outer cleanup verification.

This is an owner-authorized test policy, not a measured proof of throughput or
unattended scientific quality. Original observations remain immutable.

### QHY/GS350 focus freshness during long spectroscopy runs (2026-10-05)

Before live setup validation, the shared production runner may refresh QHY
stellar-shape evidence from an already-owned, same-run photometry job. Require
matching job/run/camera identity, no operator-intervention flag, a photometry
frame with stars, finite positive FWHM, no quality flags and a valid SHA-256
identifier. The frame must fall within the job timeline and not be in the future.
Use the frame's exposure-end timestamp plus the existing 30-minute lifetime;
polling an unchanged snapshot never extends validity. This is a read-only
quality refresh, not a WCS refresh, camera reconnect, exposure or motion request.

When optional QHY/GS350 evidence still fails, retain its original failure in a
`QHY_LIVE_SETUP_DEGRADED` warning and isolate that branch before the live-setup
hard return. Discard its quality/position authority, do not start further jobs,
and retain the existing owner-job handle for checked terminal cleanup. Isolation
does not by itself attest that a previously running job has stopped. Independent
ATR temperature/saturation, G3/slit/guide, C11/M2, identity and safety gates remain
mandatory. Cloud-associated guide discontinuity must not be accepted merely
because the guide owner is back in `Guiding` at exposure end.

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

### Cancellation and night-closeout truthfulness (2026-10-10 / 0.4.0.257)

`CANCEL_CLEANUP_FAILED` is a terminal fault, not a successful cancellation:
the stop-only callback failed to establish its required stopped/idle boundary.
Retain its failure as the last event and do not invoke a second mechanical
fault-cleanup pass with a fresh token. It cannot authorize another target,
force-closing an owner, or clearing motion responsibility. Ordinary confirmed
cancellation remains `Cancelled`.

Native night results record physical closeout and target motion/hand-off
reconciliation separately. A verified closed roof/cover and parked mount do not
settle an interrupted target's original ledger. Unsettled target responsibility
remains visible in the terminal journal and prevents a clean success claim;
it does not itself prevent emergency physical closeout once physical idleness
has been confirmed. An unconfirmed stop is never displayed as safely cancelled.

A locally completed initial PHD2 guide operation may supply **stop authority
only** if the first structured guide loss occurs before a placement session or
any exact-lock history exists. The receipt must match the current connection,
unchanged commanded lock, exactly one loss epoch, and unchanged configuration
and calibration event sequences, with no pause or pending settle. Recheck after
publishing intent, then use the existing checked stop. This does not promote
lost guiding to valid science, reset a motion budget, change quality thresholds,
or take over an external/restarted guide session.

`PHD2_PLACEMENT_GUIDE_STAR_LOST` preserves a typed native StarLost observed
during placement. Unlike a settle-only quality warning it prevents new spectra;
it grants no generic rebuild or budget reset. Its terminal record separately
states whether stopping was confirmed and retains outstanding motion debt.
Wind is not inferred from this code.

Every new gate must be added to this classification in the same change. Its
test must prove either that a warning advances without pausing or that an error
prevents the specific unsafe/invalid next action. A diagnostic metric with no
defined adverse next action defaults to telemetry, not a blocker.

