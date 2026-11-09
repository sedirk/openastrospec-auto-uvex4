# ADR-0051 — Verified return waypoints during formal G3 WCS approach

- Status: Accepted by the owner, 2026-10-30; deployment is not on-sky acceptance.
- Authority: explicit approval to advance the return point after fresh formal
  WCS verification, retaining every original budget and initial-origin radius.
- Narrowly supersedes the fixed initial return point in ADR-0045 and the coarse
  acquisition return aspects of ADR-0030/0040. Device ownership is unchanged.

## Evidence

In front1, HD 220539 and HD 221662 had agreeing formal QHY/G3 solutions but a
mount Sync that did not change the reported coordinates. Reaching the target
required about 3.3 degrees. The original 6-degree cumulative envelope could not
include an eventual return to the very first field. A successful intermediate
formal solution did not previously reduce that obsolete return obligation.

## Decision

Keep the immutable initial reported origin as the tangent-projection origin and
radius boundary. Add a separately authenticated optional return waypoint to the
same durable ledger. Missing waypoint means exactly the old initial-origin rule.
This is not a new motion ledger, optical calibration, Sync or catalogue GOTO.

Only a charged WCS-centering move starting from a formal solution of its current
raw image may promote its endpoint. Before promotion, another distinct immutable
destination image must have a formal solution, verified live frame/mount/owner/
epoch/pier binding, and measured progress toward the same target. Preserve the
existing stable-arrival and safety/horizon gates. Predicted, registered, mosaic,
local-dual and short-centroid-only arrival evidence cannot promote a waypoint.
Bind the waypoint to its frame hash, formal evidence hash, capture time and
charged action index; persist atomically before any further outbound intent.

Every outbound step must already reserve a complete segmented return to the
PREVIOUS waypoint, even if the next solve fails. An unsuccessful, stale, corrupt,
non-improving or cancelled verification leaves that previous waypoint intact.
Reaching and verifying a waypoint changes no cumulative distance, charged action,
earliest clock, exposure/continuation accounting, global radius or numerical limit.
After promotion, subsequent return/time/handoff calculations use that waypoint;
blind search cannot advance it and retains its own local search envelope. The
initial origin continues to bound every actual/commanded position independently.
At the initial formal field, the optimistic total-route check may compare the
one-way approach, rather than insisting on a future full return to that first
field. Actual moves still pass the unchanged per-leg return reservation.

The authenticated waypoint is a historical safe return coordinate, not current
target/slit evidence. Every return retains fresh motion, owner, side, horizon and
safety checks. No movement is replayed automatically on restart; existing startup,
terminal archival and native skip rules remain. A quality miss after checked stop
must still let the next native target actually run. Unknown physical stop, weather,
identity or command ambiguity is never downgraded. No extra Sync or firmware/limit
change is permitted by this decision.

## Compatibility and validation

Optional fields are omitted for old ledgers, preserving their canonical hashes.
New waypoint-bearing ledgers are hash-authenticated; an old executable that ignores
the new payload cannot reproduce its canonical hash and therefore cannot adopt it.
Rollback must respect outstanding records, not delete them or pretend a return.

Tests must replay the historical required offset and raw FITS read-only, then use
explicit finite synthetic destination measurements to exercise the production
planner: one-way approach can fit while the old full round trip cannot; every leg
still has return reserve; failed destination solve returns to the prior waypoint;
radius, distance, count, time, epoch, pier, source and cancellation limits remain.
Cover persistence/tampering, old ledgers, post-guide handoff, return time, and actual
native next-target execution. Synthetic destination solves are not historical
observations or proof that these stars will succeed on sky.
