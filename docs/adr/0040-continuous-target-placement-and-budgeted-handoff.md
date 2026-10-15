# ADR-0040 — Continuous measured target placement and budgeted coarse/fine handoff

- Status: Accepted; real-sky verification remains separate.
- Date: 2026-10-15
- Authorization: the owner requested the coordinated continuity, PHD2 convergence,
  coarse/fine handoff and warning-probe repair, retaining the normal stellar mode,
  complete safety chain and original budgets; install/restart without observing.
- Supersedes: ADR-0037's one-settle-per-logical-target restriction, the independent
  nine-arcsecond coarse handoff ceiling **only after an attested owned guiding
  stop**, and ADR-0031's point-only warning-probe geometry. Other rules remain.

## Evidence

HD 21455's failed October 15 run had strong guide SNR but its primary remained
off the shifted lock after multi-star reference positions were updated. A single
same-lock recovery was consumed on the first stage; a later charged stage could
not use it. HD 21641's first frame selected a small peak near a stale prediction,
then switched to the saturated core, producing a roughly 20-pixel discontinuity.
Its fresh coarse handoff was 0.13 arcsecond from current image authority, but the
9.01-arcsecond displacement from the earlier ledger exceeded the separate 9.00
limit and escaped as an untyped whole-night exception. Both targets have earlier
successful spectra obtained with explicit placement-quality warnings.

## Decision

1. During off-slit guiding, translate the last measured target search position
   by the **same-frame, pixel-matched native primary displacement** from its
   measured reference. Never translate it by the commanded lock movement or
   multi-star dx/dy. This is a search prediction, not a measured target. Require
   a fresh centroid/core with continuous target-to-primary geometry; an
   inconsistent sample uses the existing bounded optical recapture/window
   restart, not a new solve ladder, changed target mode, synthetic position or
   whole-night stop. Keep ordinary versus saturated flux authority explicit;
   resolved nearby components must not be replaced just because one is brighter.
2. Three matched primary measurements demonstrating multi-star disagreement may
   request native same-lock settling once **per already charged exact-lock stage**.
   Durably charge its stage ordinal before dispatch; preserve it across restart,
   return and handoff. It cannot authorize another shift, select another star,
   edit PHD2 settings or recalibrate. Use the original stage/time and complete
   return reserve. Timeout remains timeout; discard the pre-operation FIFO and
   require three new same-session measurements. No recovery before a charged
   shift, no repeated settling on an unchanged stage, no extra movement credit.
   Same-lock waits inspect a complete FIFO after each new frame, including
   FRESH_G3_RESIDUAL_REQUIRED, rather than unnecessarily rereading three frames.
3. After locally attested PHD2 stop and quiet mount readback, a fresh immutable
   mount-bound image may hand the current position to the existing settled
   coarse ledger beyond its former drift tolerance. Conservatively debit the
   larger spherical/tangent displacement and one action, with the same origin,
   clock, single/radius/cumulative/action limits and full return reservation.
   No actual movement is issued by this accounting step. Missing ownership,
   stop, fresh binding, same pier/epoch or settled ledger remains a run failure.
   An otherwise valid handoff that cannot fit the original budget is typed
   target-quality failure and reaches the normal checked-stop/skip boundary.
4. Explicit warning-after-placement consent may also authorize a diagnostic
   ATR probe when the fresh **filled saturated target core overlaps the finite
   measured slit aperture**, allowing the existing near-slit allowance and
   retaining uncertainty. Point-centroid and strict acceptance are unchanged.
   Each of three frames must satisfy this geometry and the original acquisition
   envelope. A core radius is measured from this frame, never an arbitrary PSF
   expansion, ghost/companion substitution or a claimed exact midpoint arrival.
   Probe permission is not an accepted spectrum: the shared ATR trace, clipping,
   contrast/SNR, cooling, guiding and accepted-frame checks still decide science.

Weather, horizon, roof/cover, dawn/cutoff, cancel, exclusive owners, output,
mechanical limits and confirmed stop remain independent. Neither mode changes
nor additional exposure/motion budgets are introduced. Raw data are immutable.

## Verification

Replay both stars' successful and failed raw/measurement records with SHA-256
checks. Cover changing estimator authority, nearby components, missing primary,
stale epochs, two distinct charged stages, duplicate recovery, budget preservation,
physical core/slit overlap versus non-overlap, strict/warning mode, and actual
next native target execution after a produced quality failure. Negative native
tests retain stop failure, weather, cancellation, dawn/deadline and unknown
ownership. Offline success never asserts new real-sky success.
