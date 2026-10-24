# ADR-0044 — Continuous wide/narrow acquisition state and reachable recovery

- Status: Accepted by the owner on 2026-10-24; implementation, offline regression,
  installation and on-sky commissioning are distinct acceptance stages.
- Authority: explicitly implement the October 24 whole-path audit, first fixing
  target-local failure, mosaic reachability and timing, then continuous wide-field
  measurements, target tracks and dual-optics cold start; install without observing.
- Supersedes the mandatory three-different-run cold start and two temporary QHY
  witness limit in ADR-0043 **only through the bounded continuous route below**;
  refines ADR-0030/0040/0043 temporal and coarse/fine handoff semantics.
- Does not supersede device ownership, weather, physical limits, cancellation,
  unknown-stop handling, original budgets, or the independently saved quality mode.

## Why

The [causal audit and pixel experiments](../reports/2026-10-24-wide-narrow-fusion-audit.md)
show useful independent image information lost between stages, unreachable mosaic
fallbacks and optical-quality failures escaping as whole-night failures. More
thresholds or repeated full solve ladders cannot repair those control-flow gaps.
Wide-field pixels can measure common sky motion despite inaccurate polar alignment;
they cannot alone observe differential flexure, primary-mirror shift or slit motion.

## One measurement path, existing action owners

Use the existing configured QHY owner and job transport. ADR-0014's worker remains
the accepted ownership target; installations still on its explicitly retained
legacy route require a separate idle-boundary migration, never automatic failover.
The coordinator may start an acquisition-only continuous stream after the initial
safe, owner-bound wide-field acquisition and before G3 placement. Loading a plan,
starting the plugin or installing this release does not start that stream.

The stream uses the already locked camera/filter/geometry/exposure settings,
original target/night deadline and live permission/cancellation chain. Acquisition
frames are not accepted science photometry. Retain every saved raw frame; analysis
uses a bounded latest-wins queue so a slow solver cannot build an obsolete backlog.
There is at most one active QHY acquisition job/owner. Explicit witness requests,
science photometry, pause, recovery, flip and cleanup join/retire that job at the
existing frame boundary before changing its role. Unknown termination remains a
physical-stop responsibility; optional analysis failure is not a new night stop.

Formal WCS keyframes establish absolute orientation/position; independently held-out
pixel registration supplies relative motion between keyframes. Re-solve on loss of
registration, context change or expired authority, not on every useful intermediate
frame. PHD2 alone supplies G3 images/guide corrections; the master alone issues
coarse corrections through its original durable ledger. Measurements do not create
a second mount controller or grant exposure/motion authority to an analysis task.

## Time is part of each measurement

Retain actual exposure duration, native request/completion bracket, original raw
hash, owner/geometry/focus/pier context and solve completion. The capture midpoint
is an interval, not the time processing finished. Delayed results describe their
exposure time; consumers include age/smear and measured relative motion before use.
Mount coordinates or tracking=true are not zero-drift evidence. Missing drift
permits a bounded search hint and fresh pixel confirmation, not invented current
position or measured slit arrival. Never refresh an old prediction by replacing
its timestamp. UTC discontinuity, owner/focus/pier changes invalidate the relevant
track; PC elapsed budgets and original night deadlines are never restarted.

Use the existing 45 s current-age, 60 s adjacent-gap and 120 s connected-span
quality windows for sparse inference. A recent independent formal anchor can
re-root a graph and discard no-longer-required old dependencies; required parents
cannot be removed while still claiming their authority. Retain discarded evidence
on disk. Bound memory and active dependency depth, not raw retention.

## Reachable overlapping fields and cold start

Already acquired, validated immutable G3 frames from acquisition, selection and
residual measurement may feed the same evidence store when geometry and owner
context match. Observing a guiding frame is not stopped-mount evidence. Candidate
routes still validate the appropriate current boundary before using that evidence.
An unusable anchor is not grounds to suppress a new formal solve of actual connected
pixels. Try useful existing registration/mosaic evidence before another redundant
full-frame solve, at most once per unchanged source set. All capture, solve, compose
and registration time remains inside the original return reserve and target budget.

Choose a dual-model training subset by query sky/context/time **before fitting**;
do not remove a disagreeing holdout to pass. Independence means different immutable
exposures/solves and held-out information, not necessarily different run IDs.
Same-run independent pairs may populate a model. A single applicable formal pair is
a labelled local boresight/search prior, not a validated multi-parameter model or
fine-motion calibration. Its larger uncertainty restricts it to acquisition/search
and current-frame confirmation. Multi-pair validated predictions retain independent
holdouts and empirical error; old schema-1 data are never silently upgraded.
Keep source age/model applicability separate from short-lived current sky pose;
a fresh stream does not rejuvenate an old optical calibration.

## Unified target continuity and handoff

Keep last-trusted target/component identity separate from tentative detections.
Ordinary stars as well as catalogue-geometric targets may use independent field
registration to predict a search position. Reuse the coarse registration evidence
contract (distributed fit stars, independent holdouts, ambiguity/parity checks,
target exclusion, immutable inputs and pinned extraction implementation).

Only compatible fresh measured target evidence updates a measured track. Forced
aperture signal or a predicted position is not a centroid. A brighter halo feature,
ghost or neighbouring component must not overwrite the reference merely because
it has higher SNR. Preserve the owner's explicit multi-lobe rejection and existing
component ambiguity rules; do not silently switch ordinary targets to faint mode.
Unmeasured positions can guide the next original-budget observation, not prove
slit placement. Coarse handoff carries its uncertainty/time/provenance and requires
fresh independent target confirmation before the existing PHD2/ATR acceptance.

## Quality failure versus physical responsibility

Prediction outside the recognition window and explicitly identified pure HDR
pixel/edge failures are target-quality outcomes. Preserve their underlying reason
through wrappers. A saved skip-target policy applies only after the actual run joins,
LED/acquisition/guiding stop is checked and outstanding ledger responsibility is
reconciled. It does not assert successful return, placement or accepted science.
Wrong physical slit slot, changed device/calibration, corrupted evidence, unsafe
weather, cancellation, unknown stop and original dawn/cutoff still veto continuation.

No new actions, distance, time, exposure attempts, retry counters or return reserve
are allocated by fallback, role transition or track refresh. Existing warning-probe
choice remains effective only through its real target/slit/guide and spectrum gates.
ATR active throughput peak-up and an additional blind solver are not part of this
change; neither has the required historical action samples or commissioned limits.

## Required verification and deployment

- Drive production frame ingestion/dispatch/registration and handoff with finite
  ports and immutable historical successful and failed frames, in causal order.
  A helper-only success or hypothetical missing frame is not closed-loop evidence.
- Validate delayed/duplicate/out-of-order frames, long exposure/solver latency,
  unmeasured drift, wrong field/companion/ghost, geometry/focus/pier/owner changes,
  ambiguous matches and raw corruption. Show negative examples, not only success.
- Test early continuous job start, bounded analysis backlog, capture-role transition,
  joined stop/cancel and unchanged budget. No production fixture may open a camera.
- Generate each repaired optical failure at its producer, execute two real native
  target containers, and assert the second reaches FinalizeObservation. Unsafe,
  unknown stop, cancelled/end-night and depleted deadlines must prevent it.
- Run frozen-hash verification, full repository build/tests, pinned Python checks,
  offline UI/runtime binding checks, exact-artifact backup/install and normal restart.
  Confirm safe idle first; do not interrupt a new operator-started observation.
- Installation authorizes no new exposure, autofocus, slew, roof movement or resume.
  Report actual verified offline and runtime coverage separately from future sky work.
