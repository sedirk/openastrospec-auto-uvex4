# Automatic recovery matrix — 2026-08-29 commissioning closure

Status: implementation and commissioning note. This document does not change
the frozen device-ownership or physical-action invariants.
Updated through [0.4.0.300 / October 15](closeout-2026-10-15.md); ADR-0040 supersedes
the original once-per-observation settle-recovery limit from ADR-0037.

## Why this exists

The 2026-08-28/29 real runs exposed a repeated failure pattern: a local PHD2,
G3, QHY or UVEX evidence problem was often converted into a generic stage
failure. In particular, the PHD2 placement catch treated almost every exception
as a lost guide session, discarded the valid G3 state and ran the full G3/PL3
acquisition again. That created long solve loops, repeated optional QHY pairing
and enough elapsed time for the target to drift away from the slit.

Automatic recovery is now whitelist-only. An unlisted code never inherits a
retry merely because its text looks transient. Every automatic attempt is
bounded, audited and preserves durable movement/action counters.

## Implemented automatic paths

| Failure family | Automatic response | Bound | Important invariant |
| --- | --- | ---: | --- |
| Fresh target position disagrees with a search window carried across a lock shift | Translate the search position using same-frame measured primary-star motion; retry discontinuous peak changes through the existing fresh-frame path | Existing placement clock and movement ledger | Commanded lock displacement and multi-star average are not measured target motion; no fabricated centroid or automatic switch to a nearby bright source |
| Three fresh frames show primary-star / multi-star correction mismatch at an already-charged exact-lock stage | Persist the stage before one native settle recovery at the same lock, then measure again | Once per charged stage, within original deadline and full-return reserve | No extra offset, reselection, recalibration, profile change or retry-credit reset across reload/return/handoff |
| Fresh measured coarse/fine handoff exceeds the old continuity-only distance cutoff | Require checked guide stop, quiet same-side/epoch readbacks and current image binding; conservatively debit the displacement and one action | Original action, movement, time and complete-return budgets | Valid budget exhaustion is typed target-quality; unknown owner, position, ledger or stop is not downgraded |
| Identified filled-core geometry overlaps or approaches the finite physical slit but misses preferred midpoint precision | Under explicit warning consent, enter the shared diagnostic ATR probe route with retained quality flags | Complete fresh window and existing placement/probe budgets | Not exact arrival or guaranteed science acceptance; strict mode and independent ATR quality checks remain unchanged |
| Fresh identified target has positive finite integrated flux outside the reference interval | Preserve the actual sum as a warning when primary and target positions are measured in the same frame; a typed saturated core with a retained catalogue/neighbour identity chain uses explicit non-photometric N/A | Existing placement/return ledger only | No extra movement credit; missing/non-finite evidence, identity, topology and weather are not downgraded. Without measured support, the non-dispatched flux-range refusal is target-quality and the next native target requires independently confirmed stopping |
| PHD2 native off-slit selection returns no candidate, or the selected point intersects detector edge, target halo or physical slit | Only a successful `find_star` null result or reviewed geometry rejection consumes one attempt; save/wait for another fresh full-frame native selection. When an `AutoPreferOffSlitThenDirectTarget` route exhausts, checked-stop and enter the explicitly commissioned, supervised direct-target route | 4 native selections; one route transition | RPC, connection, protocol and malformed-point failures do not inherit no-candidate retry; the coordinator never substitutes a locally ranked ordinary star |
| Confirmed PHD2 disconnect or `LostLock`, with no outstanding unreturned lock mutation | Stop the stale session, acquire fresh G3/PL3/slit evidence and establish one new guide session | 1 | Semantic FITS/WCS errors, native selection rejection and same-epoch settle timeout are not lost lock |
| `G3_TARGET_REQUIRED`, `PHD2_SLIT_TARGET_REQUIRED`, or `G3_FIELD_REQUIRED` | Run the existing earliest-safe resume dependency chain | 1 | G3, slit and guide authority are regenerated; no motion budget is reset |
| `GUIDING_LOST`, `GUIDING_UNSTABLE`, or `GUIDING_NOT_STABLE` before science | Fresh G3 → replace target on slit → restart guiding → restore optional photometry, as required by the interrupted stage | 1 | No new ATR shutter action occurs before a fresh guide epoch passes |
| Disposable G3 binding/carry-forward/slit-geometry evidence | Discard derived field and acquire one independent immutable evidence set | 1 | Cached detector-fixed slit geometry may remain; sky frames never cross mount motion |
| Tracking-enable timeout before initial catalogue slew | ADR-0038: one original-target native GOTO after full safe/idle checks; verify stable tracking afterward | 1 durable initial GOTO; no generic timeout retry | No home, forced flip, sync, limit edit or old optical reuse; native rejection/unknown stop retains safety standby |
| `FINALIZE_INCOMPLETE` | Retry checked stop/OFF/close terminal actions | 2 | Finalization never reopens or resumes a data owner |
| Optional QHY health, preview, pause or resume failure | Disable scientific use of QHY, retain its owner-job handle for final cleanup and continue spectroscopy | no QHY restart loop in the plugin | QHY failure cannot block ATR/G3/UVEX spectroscopy |
| UVEX slit LED is verified OFF but lease release fails | Record a warning and reacquire/revalidate the lease before a later command | no output retry needed | Lease cleanup failure is not reported as unknown LED output |
| QHY WCS to mount Sync | Convert the formal QHY coordinate into the mount readback epoch before the single Sync; verify using same-epoch angular separation | one Sync | An ambiguous Sync is never resent |
| Bright saturated target versus annular ghost | Reject annular topology before wing-centroid target acceptance | per fresh immutable frame | Morphology cannot fabricate catalogue identity or movement authority |
| Transient identity-pinned N.I.N.A./PHD2/UVEX connection failure during Night Setup | Repeat the complete owner/profile/identity connection boundary | 2 attempts; 1 s delay | No movement, exposure or output command is authorized; mismatch/safety codes never inherit the retry |
| G3 frame has no coherent source and cloud/transparency cannot be distinguished from an empty field | Wait and recapture at the unchanged durable position | 3 fresh evidence sets; 5 s delay | No mount search motion; durable acquisition lineage and clocks are not reset |
| Coarse centering lost its accepted QHY source frame/solve | Re-enter the resume chain and acquire one new immutable QHY field before centering | 1 | Old source/solve is discarded; no stale frame can authorize a correction |
| StartGuiding sees a stale placement settle epoch or missing graded G3 field | Fresh G3 → placement → one new guide epoch | 1 | Stale target/slit evidence never restarts guiding directly |
| A failure occurs inside an automatic dependency-rebuild chain | Classify the inner stage/code through the same whitelist and re-arm the outer dependency chain | exact stage/code bound plus an 8-attempt session fence | Nested failures can recover, but cannot skip a failed prerequisite or reset a budget |
| Disposable G3 mount binding becomes stale/missing/unreadable at slit placement | Re-enter the existing dependency chain and reacquire G3 before placement | 1 | Hash/context/epoch/pier/topology changes remain hard stops |
| PHD2 lock position is temporarily absent during durable recovery/return | Repeat only `get_lock_position` while the same connected Guiding epoch is current | 3 production reads | LostLock, disconnect, pause or epoch change stops the read loop immediately; no mutation is emitted |
| `PHD2_LOCK_FAILURE_RETURNED` after fresh proof of the durable origin, settled-lineage persistence and successful checked-stop | Rebuild the existing fresh G3 → placement dependency chain under the same ledger | 1 dependency rebuild | Consumed attempts, pixels and elapsed-time budget are preserved; any unconfirmed stop, failed origin proof or epoch change remains a hard stop |
| Fresh durable origin is proven but the first PHD2 checked-stop/readback is unconfirmed | Retry only the idempotent checked-stop/readback | 1 additional stop attempt | No guide, lock-position or mount command is resent; two failures retain `PHD2_LOCK_ORIGIN_REACHED_STOP_UNCONFIRMED` |
| Durable lock-return dispatch discovers pre-command lock drift | Preserve the precharged attempt/pixel budget, persist the fresh actual lock and replan | existing return envelope | The stale exact-lock vector is never sent or retried |
| A locally issued exact-lock/settle legitimately advances `GuideEpoch` | Rebind only after same-connection fresh lock/settle attestation | existing stage bound | Durable lineage, debt, origin, attempts, pixels and elapsed-time origin are preserved |
| Reviewed G3 capture, FITS/SHA source-read or configured-solver transient inside the PL3 ladder | Abandon the reserved path and advance to the next commissioned exposure tier using a new immutable path | finite ladder; one outer fresh-ladder retry after pure-transient exhaustion | Cancellation, disconnect, identity, access, cryptographic/hash and configuration failures remain hard; pure transient exhaustion never authorizes search motion |
| Optional QHY job readback fails during finalization | Retry status read twice, then record a warning and continue required cleanup | 2 read-only attempts | No QHY reconnect/start/resume; UVEX OFF, PHD2 stop, cover and roof cleanup cannot be bypassed |
| Locked optical-cover adapter is disconnected during cleanup | Revalidate the Profile selection, connect that exact adapter once, then reread identity/state before the existing idempotent close | 1 read-only reconnect | Identity mismatch, Error/NotPresent or ambiguous close state remain hard stops |

## Non-retriable actions and independent safety stops

No generic retry may bypass the following boundaries. Rejecting an unsafe or
unsupported **next action** is not automatically an entire-night stop: a reviewed
target-quality or valid-budget failure can follow the saved skip policy only after
independent checked stop and durable-state retirement. Owner, weather, cancellation,
unknown physical state and corrupt evidence are never converted into quality skips.

- camera/profile/COM-port/owner, commissioning, Night Setup or stable-identity mismatch;
- FITS, WCS, evidence, manifest or durable-ledger hash/context mismatch;
- unknown or changed pier side/coordinate epoch where a pending movement or lock
  vector could be reinterpreted;
- explicit unsafe weather/Safety Monitor, rain, roof/cover error, protected
  horizon or parked/untrackable mount;
- ambiguous movement completion without a trusted current position and a
  corresponding durable intent;
- a command that would exceed single-step, cumulative movement, attempt, return
  reserve, elapsed-time or bounded-search limits; valid target-budget exhaustion
  follows the checked-stop skip route without replenishing those limits;
- more than one outstanding movement lineage or an endpoint that fresh evidence
  cannot distinguish;
- UVEX slit illumination OFF that still cannot be command-completed and
  readback-verified after its bounded exact-COM5 reconnect attempt; and
- target/ghost candidates that remain non-unique after fresh evidence and the
  available external identity authority: do not invent target identity; a reviewed
  acquisition-quality failure may skip after checked stop rather than end the night.

## Deferred follow-up (not part of the sunny-night closure)

The audit also identified larger improvements that should be implemented and
fault-injected off sky: current-position WCS verification when an encoderless
mount settles stably but not exactly at the commanded coordinate,
current-pointing fresh LED-OFF ghost extraction without repeating LED-ON/HDR,
durable reconciliation of an interrupted post-Sync catalogue slew, provenance
for external versus locally requested PHD2 `GuidingStopped`, and separating
active UVEX transport faults from historical diagnostics. These are
intentionally not hidden behind the generic retry supervisor.
