# ADR-0041: Bounded severe-defocus recovery at a native target boundary

Status: Accepted, 2026-10-21, explicitly approved by the observatory owner.
Narrowly supersedes ADR-0018's prohibition on automatic C11 focus movement; its
immutable per-run focus lock, independent UVEX calibration and device owners remain.

## Context

The owner confirmed severe defocus during the October 21 mid0 failures. A short
exposure association defect independently jumped from a stable whole parent to a
weak adjacent patch. Fixing that association is not a focus correction. The owner
explicitly retained persistent multi-lobe rejection, then approved a bounded
automatic focus recovery instead of accepting ambiguous morphology as a star.

The existing SEP main-mirror autofocus already uses the N.I.N.A. focuser owner,
PHD2-owned G3 frames, fixed-star R50/R80 measurements, native curve fitting and
repeated candidate/origin verification. Reuse it; do not build a parallel focus
algorithm or call a manual view model that competes with the night owner.

## Decision

1. An enabled, fully supervised native night may consider recovery after an
   explicitly classified G3 acquisition/image-quality failure. It must first
   join the target and independently confirm acquisition, guiding, focuser and
   active mount motion stopped, with motion responsibility settled. Cancellation,
   unsafe weather, owner changes, unconfirmed stop, dawn, cutoff and EndNight do
   not become focus triggers. ATR cooling failures and unrelated device failures
   are not evidence of defocus. Standalone/manual observing is unchanged.
2. Acquire a bounded pair of fresh, same-setting diagnostic G3 frames through
   PHD2. Require at least three independent, isolated, unsaturated stars with
   consistent identity, morphology, position and flux in both frames. Grossly
   broadened whole-parent R50/R80 may nominate a severe-defocus candidate; a
   single hollow or multi-lobed target, low SNR, missing stars or failed solve
   alone cannot. Reject missing shape metadata and obvious trailing or changing
   transparency as triggers. This is not an assertion that image shape uniquely
   distinguishes focus from seeing or thin cloud. Retain measurements and the
   versioned conservative detection criteria; the scan/verification proves any
   actual improvement. Do not relax companion or multi-lobe target acceptance.
3. Lock the saved main-focus options with the observing configuration. Missing
   or invalid travel bounds mean no automatic motion. Intersect those bounds
   with the commissioned C11 limits, including native backlash clearance. Use
   the commissioned per-move and cumulative movement limits, precharging every
   move with its safe-return reserve; smaller scan steps do not reset that sum.
   Do not convert the saved travel range into an increased movement allowance.
   Use only the master N.I.N.A. focuser and PHD2 G3 owner; do not move UVEX M2, GS350,
   grating, slit, roof or mount as part of focusing. Keep the night reservation,
   selected connected safe monitor, full environment checks and cancellation
   alive throughout. A per-operation check plus bounded safety polling must
   cover long captures/moves, not merely the initial preflight.
4. At most one mechanical focus scan is allowed in a native night (therefore at
   most one per target). Persist its intent before the first scan movement and
   recheck the live stop/safety/budget boundary there. An unsuccessful scan still
   consumes this allowance. A no-motion diagnostic rejection does not consume
   the night scan allowance. Restart does not resume an old night or its recovery.
5. Diagnostics, scan, verification and necessary stopped/return boundary consume
   the original target and acquisition clocks. The bounded recovery deadline is
   no later than any original target, motion/fine-acquisition or night deadline,
   with time reserved for stopping and a safe return. No new exposure attempts,
   movement quota or target window is issued. Too little remaining time means
   the existing target-local skip route, not an extra recovery budget.
6. Reuse `SepMainFocusRunner` and its original curve and A/B/A/B quality rules.
   Adopt only a verified result. When improvement is not established, keep or
   return to the original focus only while ownership, position and safety remain
   trustworthy. Cancellation/unsafe events prohibit new recovery moves; an
   unconfirmed final position is a device boundary failure, not a quality skip.
7. A successful recovery retires the old optical segment and creates a new
   canonical production runner for the same native target. Never rebase or reset
   `C11RunFocusLock` in place. Preserve accepted raw frames, exposure attempts,
   reused probes, original target deadline and all charged motion/probe budgets.
   Reacquire target/WCS, slit and guide evidence at the new stopped focus before
   another science exposure. Do not resume stale optical evidence or duplicate
   accepted frames. A later failure follows the saved skip policy only after a
   fresh independent stopped/settled boundary.

## Operator control, evidence and rollback

The main-focus settings expose one saved automatic-recovery switch; loading,
saving or installing it never starts a focus scan. The owner-approved default
enables eligibility in future validated native nights, not hardware action at
startup. Existing bounds and scan settings are preserved. The manual button
remains unable to acquire devices owned by a night or protected standby.

Keep diagnostic FITS, fixed-star measurements, precharged intent, scan result,
original/new positions, budgets and segment lineage. Historical raw observations
and original Night Setup hashes are immutable. Disabling the switch restores the
prior no-auto-focus behavior at the next run boundary without changing any data.
Installation/restart is not permission for a real focus test or new observation.

The owner separately approved correcting the local historical focus-capability
conflict on October 21: its 200-step single / 1000-step cumulative allowance did
not cover the already configured 300-step native overshoot. A new, hash-versioned
commissioning package may bind C11 range 0–10000, single-command total travel 750,
cumulative travel 12500 and backlash allowance 300. These are local authorized
ceilings (including overshoot/return), not new repository defaults, a measured
best focus, or increased mount/exposure/time budgets. Keep the logical Night Setup
ID for existing cards, retain every old file and optical measurement, and record
new package hashes/provenance. The fixed scan still preflights its actual saved
settings; future edits cannot silently increase these ceilings.

## Acceptance

- Multi-star repeat, no/one-star, low SNR, saturation, elongated, ambiguous,
  inconsistent flux/position, missing metadata and normal-size counterexamples.
- Saved settings and commissioned limits, no scan before durable one-use intent,
  full connected safety, ownership, dawn/cutoff, cancellation and stop failures.
- Native first segment failure, actual same-card new runner and complete
  acquisition after verified focus; unchanged counters, raw hashes and budgets.
- Failed recovery with confirmed original position actually executes the next
  native target; unknown physical state, unsafe, cancellation and EndNight do not.
- Existing hand/manual focus tests and immutable lock tests stay valid; offline
  tests and installation do not establish real-sky autofocus success.
