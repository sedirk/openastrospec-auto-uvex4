# ADR-0047 — Preserve overlap only for an available registered pixel route

- Status: Accepted by the owner, 2026-10-26.
- Authority: explicitly restore the commissioned 300 arcsec neighbour step when
  no usable registration/mosaic support exists, retaining every original limit.
- Supersedes only the mandatory overlap cap on blind neighbour search in
  ADR-0043/0044/0045. Formal and inferred centering rules are unchanged.

## Evidence and decision

The .316 HD 18552 run `UVEX-20261025T191054Z-db9ce157515f4a8` reached neighbour
search, but none of its measured pixel pairs established independent registration.
The universal overlap cap reduced 300 arcsec to 116.802 arcsec. Eleven visits
covered only 261.177 arcsec radius before the unchanged return/action reserve
ended the search. Preserving hypothetical mosaic coverage consumed real attempts.

Keep the existing commissioned square-spiral destinations, origin and ledger.
Before each outbound visit, inspect the current immutable frame's retained graph.
Only a fresh, independently registered connected pair with sufficient field-star
support makes the overlap-preserving route available. Hash/owner/context corruption
is never absence of support and must not silently select a longer move.

Without that support, approach the next original spiral destination by no more
than the original configured step. With usable support, subdivide that same
approach by the existing uncertainty-aware overlap bound. Each subdivision consumes
one original action; support arriving or expiring neither restarts the spiral nor
resets a counter, timer, return debt or cumulative distance. Evidence records the
nominal destination, actual proposed visit and reason for the overlap choice.

All existing source binding, weather/roof/cover, owner, UTC, pier, cancellation,
single/radius/cumulative/action/time and full return-reserve checks still precede
the durable intent and mount call. The original 900 arcsec local radius, 3600
arcsec local cumulative distance, 12 actions and 1200 seconds remain ceilings,
not promises that every nominal destination will be reached. No new exposure,
calibration, optical-axis transfer or target identity is granted. Every arrival
needs a new G3 frame; accepted WCS/placement still uses its existing validation.

Exhaustion remains a target-quality failure only after checked return/stop and
joined acquisition. The saved native skip policy must actually execute the next
target; unsafe, unknown stop, cancellation and night cutoff must veto that route.

## Verification and release

Replay immutable .316 raw frames and registration receipts as negative evidence;
do not invent a solved image at an unvisited larger waypoint. Exercise the actual
visit planner with original ledger reservation, live support acquisition/expiry,
bounded subdivisions and native next-target execution. Retain historical positive
registration/short-position tests and all safety negatives. Run repository checks,
backup and install the exact artifact only at confirmed idle; no observation is
started by this maintenance. Better reachability is not proven real-sky success.
