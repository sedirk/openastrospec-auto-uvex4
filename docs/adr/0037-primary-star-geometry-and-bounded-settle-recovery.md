# ADR-0037 — Primary-star geometry and bounded same-lock recovery

- Status: Accepted; real-sky acceptance remains separate.
- Date: 2026-10-14
- Authorization: the owner explicitly approved one native settle recovery after
  confirmed primary/multi-star disagreement, inside the original time and motion
  budgets, without another lock displacement or recalibration.
- Supersedes: only ADR-0029's prohibition of another native settle for this
  specific, measured failure. Normal post-lock continuation remains read-only.

## Evidence

KY And on October 13 reached its first 25-pixel lock shift, then repeatedly
waited with a roughly 35–40-pixel target/slit residual. At 23:55:03 PHD2 reported
the primary centroid at (454.75, 948.66), while the plugin derived (469.48,
955.27) from lock plus GuideStep dx/dy. In multi-star mode those offsets describe
the combined correction, not necessarily the primary centroid. The resulting
coordinate error changed the inferred remaining lock destination and consumed
the completion window. This is not evidence that the selected source was a ghost.

## Decision

1. Never use lock plus GuideStep dx/dy as a physical centroid or a return receipt.
   Retain that value as a diagnostic effective guide position. Read PHD2's native
   `get_star_image` primary centroid and pixels, require its frame number to match
   the fresh GuideStep, and uniquely match the exact pixel crop in the immutable
   `save_image` FITS. A race, malformed crop or nonunique match grants no geometry;
   use the existing bounded fresh-window rejection route, without selecting a
   substitute source. Both RPCs are read-only and do not start another exposure.
2. An off-slit post-lock window with at least three measured frames may identify
   a stalled primary when every primary is more than 3 pixels from both the
   current lock and the effective multi-star position. This threshold admits
   recovery; it is not a new science rejection threshold. Direct-target mode,
   missing primary proof and a single bad sample do not admit recovery.
3. Precharge one recovery in the durable lock lineage before dispatch. With the
   same connected Guiding owner, unchanged lock, current calibration and full
   immediate safety chain, call native `guide(recalibrate=false)` once to enter
   PHD2's primary-based settling. Never change exposure, select a new star,
   mutate the lock, clear calibration or edit the PHD2 Profile. Preserve all
   movement counters, limits, the original clock and the full return reserve.
4. Only the locally attested settle operation may rebind the guide epoch. Retain
   the real native result, including timeout/failure. Require a new complete
   primary/target/slit window afterward; do not reuse the pre-recovery FIFO.
   Native success is not target/slit acceptance. Real star loss, failed output,
   changed ownership, unexpected calibration, cancellation and unconfirmed stop
   retain their independent handling. A locally exhausted recovery carries a
   target-quality outcome and can skip only after checked stop/ledger retirement.

## Related accounting correction

A completed ATR shutter interval at an already settled endpoint is passive
acquisition time, not another G3 correction. Apply the existing fine-motion
exposure-clock rule consistently to the coarse G3 ledger: credit only the minimum
of requested exposure, monotonic elapsed capture time and wall-clock interval,
with identical validated settled ledgers before/after. Keep the original start,
origin, debits and ceilings; store passive seconds separately. No credit for
pauses, interrupted captures, processing, unfinished motion or a changed ledger.
Do not change the observing window, dawn cutoff, weather response or target
deadline. Existing exposures and raw data are never retroactively relabelled.

## Verification

Exercise actual protocol frame/crop matching and adverse races, primary-near and
multi-star-near return cases, single recovery persistence, native timeout versus
owner change, fresh post-recovery windows, unchanged budgets and cancellation.
Replay KY And's contradictory coordinates and HD 18552's completed-exposure
clock at the recovery boundary. Test an exhausted recovery through two actual
native target containers, including negative weather/cleanup cases. Installation
does not establish a successful observation.

PHD2 source: [multi-star corrections](https://github.com/OpenPHDGuiding/phd2/blob/master/src/guider_multistar.cpp)
and [read-only star-image RPC](https://github.com/OpenPHDGuiding/phd2/blob/master/src/event_server.cpp).
