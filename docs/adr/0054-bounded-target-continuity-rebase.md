# ADR-0054 — One bounded rebase from repeatable fresh target measurements

- Status: Accepted; real-sky verification remains separate.
- Date: 2026-11-06
- Authorization: the owner reported the third target's repeated failure and asked
  for a repair that continues acquisition instead of adding another avoidable
  quality stop.
- Supersedes: ADR-0040's treatment of every fresh target/primary discontinuity as
  an unconditional rejection. Its budgets, ownership, identity, morphology and
  checked-stop requirements remain unchanged.

## Evidence

On 2026-11-06, HD 220539 reached PHD2 slit placement with stable guide SNR
156–170 and a stable primary lock. Three fresh frames measured the target at
`(787.006, 388.723)`, `(787.705, 390.282)` and `(787.569, 388.839)` pixels;
their largest pairwise separation was about 1.71 pixels. The old target plus
primary-shift predictions were about nine pixels away. The existing six-pixel
continuity limit therefore rejected all three repeatable measurements and
exhausted the bounded optical window.

The repeated measurements do not prove sky motion. They show that the earlier
target/primary baseline no longer represents the recognisable target centroid,
for example after a saturated or defocused core settles between estimates.

## Decision

1. A target/primary discontinuity remains the default rejection. Only an
   off-slit guide star with a fresh pixel-matched native primary crop in the same
   immutable frame can enter the rebase route.
2. Require three consecutive fresh frames from the same observation target,
   PHD2 connection/guide epoch, selected primary, profile, calibration and
   runtime lock. Each frame must already pass target identity, catalogue
   association, morphology, companion/multi-lobe, ownership, weather, stop,
   exposure and slit checks. Predictions and tracking registrations are not
   samples.
3. The three measured target centroids must have maximum pairwise spread no
   greater than three pixels. Use their component-wise median as the replacement
   search baseline. An ambiguous or unrecognised frame breaks the run; frames
   cannot be cherry-picked around a rejection.
4. Permit this rebase at most once for the same target. A new connection or guide
   epoch invalidates an unfinished candidate window. A changed lock starts a new
   candidate window only when the target has not already consumed its one
   rebase; it does not replenish the rebase allowance.
5. The rebase changes only the search prediction for the current target. It
   grants no motion, skips no identity or slit evidence, resets no action,
   distance, time, return or exposure budget and does not directly authorize
   placement or science. The normal full fresh measurement window must still
   complete after the rebase, and every subsequent move still requires fresh
   destination-image confirmation.
6. Persist warning/evidence containing the old prediction, three measured
   centroids or their stable result, spread, guide identity, lock, frame hashes
   and the explicit `budgetReset=false` and `predictionUsedAsMeasurement=false`
   claims. Unknown guide ownership, missing primary, an unstable spread,
   explicit companion/multi-lobe ambiguity, weather, cancellation or checked-stop
   failure remains hard.

## Verification

Replay the actual HD 220539 coordinates to prove the old six-pixel prediction
fails while the repeatable sequence establishes the median baseline. Cover a
three-frame outlier, consumed-rebase reuse, a later guide epoch, missing primary,
ambiguous target and source-hash changes. Integration tests must prove that the
rebase path does not reset budgets, issue motion, bypass fresh post-move
confirmation, or convert a target failure into a whole-night blocker.
