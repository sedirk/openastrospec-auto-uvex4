# ADR-0048 — Raw-verified measured-source ASTAP recovery

- Status: Accepted by the owner, 2026-10-26.
- Authority: integrate the offline-validated recovery after PL3, then simplify
  device-change handling on the startup page; no observation is authorized.
- Supersedes only the PL3-only restriction for the G3 fallback below.

Keep the configured primary solver and its successful path. A completed failed
G3 PL3 solve may use one measured-source recovery per immutable source hash in
the run. Local-background/noise extraction produces a labelled solver-only point
render, never a raw observation, restored science image or target measurement.
Use an explicitly installed ASTAP CLI and local D80 catalogue, with identities
and hashes recorded. No network queries, image uploads or device access occur.

Extraction, the owned child and catalogue verification share cancellation and
the original return/time envelope. A 30-second fallback ceiling cannot extend
any existing budget. Optional unavailable/unsuccessful analysis preserves the
original optical failure; source corruption or unjoined analysis process is
distinct and cannot invent WCS success. One fallback episode does not loop
through the offline benchmark's algorithms or exposure ladder.

Project the finite plausible WCS back into the original detector coordinates.
Require at least five unique raw-supported catalogue matches, residuals no more
than three raw pixels and support spanning 20% of both image dimensions. Retain
matches, residuals, raw SHA-256, catalogue tile hashes, extraction provenance,
solver output and shifted-coordinate negative controls. Do not refit away
residuals. The validation uses the same catalogue family as ASTAP, not an
independent-catalogue confidence probability. Clipped/noise-only inputs do not
become valid simply because the solver reported a solution.

Accepted output is a field WCS, not target, companion, slit, guiding or arrival
confirmation. Existing parity, fresh frame/device/mount/epoch, target identity
and movement checks remain. No camera ownership, Sync, GOTO, focus, lock shift,
weather action, extra exposure or budget is granted. An exhausted acquisition
uses the existing confirmed-return/stopped-owner skip policy; native regression
must actually execute the next target. Weather, cancellation, unknown stop,
ownership, night cutoff and physical limits are not downgraded.

## Startup follow-up

Show the problem and its immediate action on the startup page. A same-physical-
device driver change requires explicit operator confirmation of unchanged
equipment/clearance, a new derivative binding bundle, complete static validation
and retained original bundle. Changed physical equipment requires commissioning,
not inheritance of old geometry. No binding action connects or moves equipment.

## Verification

Replay the fixed moonlit and historical-success cohort without modifying inputs;
report unresolved frames honestly. Cover false WCS, coordinate/binning/parity,
hashes, missing catalogue, timeout/cancellation, downstream target confirmation
and next-target execution. Install only at confirmed idle; do not start observing.
Offline astrometry and panel checks do not establish real-sky slit placement.
