# ADR-0055: Terminal-target disposable frame retention

- Status: Accepted
- Date: 2026-11-19
- Supersedes: ADR-0053 eligibility and post-failure retention clauses only
- Authorization: the observatory owner explicitly requested automatic cleanup
  after every terminal target, successful or failed, for QHY continuous
  acquisition and non-science ATR trial raw frames.

## Context

ADR-0053 retained acquisition and trial frames after failed, cancelled or
otherwise incomplete targets because those runs could still be diagnostic
evidence. The number and cadence of QHY continuous-acquisition frames now make
that archive grow faster than the local disk can sustain. The immutable target
manifest, frame index, JSON evidence and logs are sufficient to explain the
outcome without retaining every disposable image pixel.

## Decision

1. Evaluate retention per target observation run. A run is eligible only when
   its immutable manifest and indexed state agree on the same real observation
   run ID and both explicitly report the same terminal state: `Completed`,
   `Faulted`, `Failed`, `Cancelled` or `Canceled`.
2. `Completed`, failed and cancelled targets are all eligible after the
   forward-only policy watermark. Active, unknown, paused, taken-over,
   incomplete or mismatched runs are never eligible. Enabling the policy does
   not retroactively select an older run.
3. Exact-file candidates are limited to:
   - QHY acquisition and continuous wide-field witness FITS used for solve,
     target tracking, handoff or placement;
   - G3 FITS used for plate solving, slit geometry, target confirmation, lock
     residual, guide selection or slit-placement trials;
   - ATR `PROBE` FITS not credited or reused as accepted science.
4. Always retain ATR science FITS, a probe credited as science, QHY
   `PHOTOMETRY` science frames, calibration data, reduction products,
   `.astroproj` files, manifests, frame indexes, JSON/JSONL evidence and logs.
5. Any file referenced by another run, project, calibration/model dependency or
   operator pick/rating remains protected. Unknown quality, identity, owner,
   filesystem state or conflicting metadata protects the file.
6. The removal worker is offline and below normal process priority. It has no
   camera, PHD2, mount, roof, UVEX or N.I.N.A. control authority. It uses a fresh
   complete index, exact path/hash/capture checks, dependency revalidation,
   locked-handle permanent deletion and durable per-file intent/result receipts.
7. The policy is versioned and opt-in. Its enable file records a forward-only
   watermark. The background worker checks at most every five minutes and any
   uncertainty defers cleanup rather than affecting acquisition.

## Consequences

- Failed targets stop consuming hundreds of megabytes to multiple gigabytes of
  disposable QHY/G3/ATR trial pixels after their terminal manifest is durable.
- Scientific spectra, credited final trials, QHY photometry and complete textual
  provenance remain available for reduction and failure analysis.
- Diagnostic image pixels may be absent after a failed target. The manifest and
  deletion receipts must be used to distinguish intentional retention cleanup
  from missing or corrupt producer output.
- Disabling the policy stops future batches while allowing an already-started
  exact-file transaction to finish with its audit.

## Verification

Tests must cover completed, failed and cancelled terminal runs; active and
unknown protection; science and credited-probe preservation; QHY photometry
preservation; unknown identities; watermark behavior; dependency protection;
CLI authorization; and an actual Windows permanent exact-file batch.
