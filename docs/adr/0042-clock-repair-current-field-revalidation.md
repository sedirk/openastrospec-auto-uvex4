# ADR-0042: Fresh current-field revalidation after verified clock repair

Status: Accepted; supersedes only ADR-0035's mandatory catalogue GOTO and
wide-field reacquisition before every clock-continuation segment. Real-sky
recovery commissioning remains separate.

Date: 2026-10-22

Authorization: the owner explicitly approved checking a new G3 image at the
unchanged current pointing after successful, stopped clock repair; if that
check cannot establish fresh target/slit evidence, return to the original full
acquisition route without replenishing any budget. This maintenance delivery
authorizes installation/restart, not a new observation or a real-device test.

## Context

In the recorded HD 218393 run, one 600-second frame was accepted before the
mount UTC diverged by about two days. The existing clock repair succeeded and
started the same target again, but the mandatory catalogue GOTO, QHY witness
and G3 reacquisition consumed most of the original remaining fine-motion time.
The later 2-pixel handoff envelope represented insufficient round-trip time,
not failure to recognize a target or a new astrometric accuracy requirement.
The following target actually executed; the failed segment and partial science
results remain valid historical records.

Reported pointing continuity is not optical proof. Nevertheless, a confirmed
clock repair need not discard the useful physical pointing by issuing another
GOTO before it can inspect a new G3 image. This decision changes that order,
not the time-repair implementation or its commissioning status. It neither
patches a date by adding two days nor establishes the cause in installed
mount firmware.

## Decision

### Scope and one-use continuity receipt

- Keep ADR-0034/0035's sole master owner, continuous native-night reservation,
  joined terminal segment, independently stopped owners, authenticated terminal
  retirement, durable one-repair intent, successful bounded N.I.N.A. TimeSync
  reconnect, and consecutive valid UTC/identity/tracking postchecks. Other
  mechanical or meridian responsibility is not retired by this exception.
- Only that explicitly active night's one same-target clock continuation may
  nominate a current-field check. Bind its receipt to the source and destination
  segments, logical target and catalogue coordinates, locked action/setup
  configuration, original budgets, exact device/Profile/owner identities and
  known unchanged pier side. Retain fresh mount readbacks before and after
  repair and recheck them at consumption and around the new images.
- The reconnect is the already authorized operation on the same mount owner,
  not permission to accept a replacement device, changed PHD2 session or changed
  optical setup. Scope the new connection explicitly; no old guide epoch, lock
  vector, WCS, target pixels, dynamic slit result or image is motion/arrival
  authority in the new segment. The receipt contains continuity provenance,
  not a reusable old optical field.
- Consume the receipt once, with a durable attempt record before acquisition.
  An ambiguous write, retry, Resume, another target, focus/meridian continuation
  or process restart cannot regenerate it. Missing or ineligible continuity
  evidence does not authorize a shortcut; use the unchanged canonical route
  only where that route's own safety and ownership checks permit it.
  Receipt validity ends at the earliest original target/night/G3/PHD2/fine
  deadline already computed by `NativeTargetSession.RecoveryDeadline`, not a
  new grace period measured from repair or receipt creation.

### Fresh probe inside the canonical runner

- Use the same native markers, configuration snapshot, factory and production
  runner as ADR-0035. Do not introduce another observation owner or a parallel
  mini-observing loop. Validate full live environment, horizon, clock, optical
  setup, ownership, stopped acquisition and no slew/pulse state before and after
  the probe. Pointing continuity reuses the existing G3 capture/current
  mount-binding continuity tolerance; no larger reconnect-specific tolerance
  or new setting is invented.
- Before a catalogue GOTO or QHY acquisition, obtain new immutable G3 evidence
  through PHD2 at the current pointing and run the shared formal solve and
  target measurement. Reuse the existing acquisition settings and applicable
  solve/short-frame confirmation limits. A current-field probe is one bounded
  episode, not a second uncharged exposure ladder. A catalogue position or
  mount readback may be a solve hint, never a measured target.
- Establish fresh slit evidence in this same new segment through the existing
  shared geometry preparation once. Its already supported flat-panel LED
  brightness/on/off acquisition may run and must confirm the lamp is off;
  it must not change cover/optical-axis positions or introduce a second full
  LED cycle solely because the following native marker executes. Installation
  geometry retains its ordinary applicability checks; an old dynamic slit
  result cannot be relabelled as this segment's measurement.
- The probe does not dispatch GOTO, Sync, pulse/lock-shift, neighbour search,
  coarse correction, guide-return recovery, calibration, focus motion or QHY
  fast-pair acquisition. Explicitly suppress the optional QHY/G3 pair side
  effect in the shared image path. Normal later guiding/placement and optional
  simultaneous photometry retain their own original authority and checks.
- Only a fresh accepted target/slit result that fits the existing remaining
  coarse/fine handoff contract may substitute for the otherwise redundant
  catalogue GOTO and QHY coarse witness. Record the skipped work as substituted
  by current G3 evidence, not as commands/frames that actually occurred. Later
  stages consume that same fresh result while its binding and freshness remain
  valid, then establish a new guide session, verify placement and re-evaluate
  ATR exposure selection before taking only missing science frames.

### Failure, budget and safety semantics

- An ordinary optical miss, failed solve or unsuitable measured handoff ends
  this optional probe and falls back once to the original full acquisition
  within the same segment and original allowance. Do not loop the shortcut,
  resurrect old pixels or carry a dynamic slit/target result into an
  incompatible pointing. Applicable fixed detector-geometry caches keep their
  original scope and cannot carry target authority across targets or movement.
- A failed/unknown physical stop, unjoined owner task, changed owner/configuration,
  corrupt input, unsafe weather, invalid clock, cancellation, dawn or night
  deadline is not an optical miss and cannot authorize fallback movement.
  Retain the original failure and independent cleanup/closeout behavior. A
  changed pier side follows the existing meridian boundary, never an old-side
  continuity receipt.
- Probe acquisition, analysis, slit preparation and fallback spend original
  elapsed time and applicable attempt allowances. Inherit accepted frames,
  integration, science/probe attempts, guiding-recovery allowance, numeric G3/
  PHD2 motion debits, action/distance ceilings and original PC-clock deadlines,
  including only already attested ATR exposure credits. No new recovery timer,
  movement reserve, calibration allowance or science credit is created.
- New movement, if subsequently needed, uses only the new segment's measured
  origin and fresh optical evidence, while retaining all inherited numeric
  debt and complete return reserve. An exhausted budget may still prevent
  completion. After independent stopped/retired proof, apply the saved target
  failure policy and actually execute the next target when eligible; never
  claim that the failed target finished or that archival proved return.
- Preserve all old raw files, manifests and original outcomes. Record receipt
  provenance, consumption, new frame/analysis hashes, fresh pre/post readbacks,
  actual branch and fallback reason, charged budgets and retained science totals.
  This optimization is not a general normal-start shortcut, a new dark-source
  mode, a guide-state waiver, automatic journal resume or installation-time
  observation permission.

## Verification

Use finite owner adapters to drive the shared canonical prerequisite dispatcher
and actual native night/target containers, not only an eligibility predicate.
Separately verify production-adapter wiring and side-effect exclusions; these
checks are not real hardware acquisition. Verify:

1. Successful clock repair, joined/stopped predecessors and fresh same-pointing
   target/slit proof lead to the same target's existing guiding/ATR stages with
   zero redundant catalogue GOTO/QHY coarse calls and no duplicate LED cycle.
2. An optical probe miss performs exactly one original full acquisition fallback;
   original frames, counters, ATR credits and all deadlines remain unchanged.
3. A failed reacquisition with confirmed stop follows the saved policy and the
   next native target actually executes; already accepted frames are not lost
   from logical totals or counted twice.
4. Changed/unknown side or pointing, stale/missing/used receipts, another target,
   changed binding/epoch/settings, tampered images, unresolved mechanical debt,
   pending capture/stop tasks, clock failure and safety/cancel/time boundaries
   cannot turn the optional probe into unauthorized continuation or movement.
5. Ordinary starts, focus and meridian recovery retain their original routes.
   Preserve historical successful/failed-run, warning-probe, stop/skip and both
   frontend parity regressions. Recorded pre-repair frames cannot prove a new
   post-repair image succeeded.

Software tests and installation must not be reported as real-sky recovery.
Separate authorized frontend commissioning must record the current-field branch,
new target/slit/guide evidence, saved spectra, fallback behavior and actual time
saved without changing the original observing or equipment limits.
