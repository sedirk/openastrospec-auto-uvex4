# ADR-0050: Retire an ended, unissued meridian handoff at explicit new startup

Status: Accepted by the owner, 2026-10-30. Installation and on-sky validation are separate.

## Context and narrow supersession

An ended native segment can leave an `OwnersIdle` control journal even though no
flip command was dispatched. ADR-0032 intentionally prevents automatic adoption
after restart, but also stranded a later explicitly requested target. The owner
approved fresh original-owner/stop/safety verification and archival of this
unissued intent, preserving observations, budgets and all other physical debt.

## Decision

- Extend the existing ADR-0027 terminal-guide startup boundary, shared by native
  and standalone targets. Installation, process startup and Resume never execute
  this recovery; only a new explicitly started logical target can enter it.
- Only one canonical global intent may qualify: `OwnersIdle`, or scheduled
  `WaitingForMeridian`, known original side, no after-side, one original segment.
  The authenticated original manifest must be a completed, durably committed
  first meridian segment with the explicit boundary gate, not an active, paused,
  ordinary completed or same-logical-target run. The intent must follow that
  segment's commit. `CommandPending`, unknown/corrupt/multiple/misplaced records
  remain blocking. All native flip commands still persist `CommandPending`
  before dispatch; the new route neither changes nor replays that protocol.
- Match original ATR, G3, mount, QHY and PHD2 owner bindings. Reuse the existing
  sole-owner connection preparation, globally idle remote photometry readback,
  current full safety chain, original target/night deadline, joined operations,
  physical stopped proof and one stationary native G3 frame. Do not open the
  roof/cover, repair clocks, change tracking, move/focus, calibrate or guide in
  this recovery. The new frame is not target identity or slit-arrival proof.
- Authenticate every outstanding G3/PHD2 envelope before archival, reject other
  mechanical responsibilities, and recheck live safety/stop and unchanged
  source records throughout. Archive original control bytes and audit hashes;
  preserve manifests, science frames, all old counters and failed-return facts.
  Archive the flip handle last. An interruption retains the uncompleted intent;
  a later new startup must repeat all fresh checks, including when guide handles
  were already archived. No successful flip or return is invented.
- After retirement the new target uses ordinary fresh acquisition, placement,
  guiding and ATR acceptance. This is abandonment of a finished logical target,
  not same-target continuation, resetting an existing budget or inheriting old
  vectors. Unknown stop/owner, cancellation, weather, dawn and cutoff retain
  precedence. No camera owner, night-closeout policy or mechanical limit changes.

## Verification, deployment and rollback

Exercise real native target containers with finite device ports: qualifying
retirement, byte-identical audit, fresh acquisition and next-target execution;
negative command-pending, malformed/duplicate journals, live/paused/same target,
changed owner, busy camera/mount, stale proof, other debt, capture failure,
cancellation/unsafe/deadline and changed records. Replay the actual historical
manifest/control bytes only in an isolated copy. Prove interruption after guide
archival cannot skip final stop proof and can be retried without old motion.

Keep complete Release/Python/UI checks and exact-artifact installation. Maintenance
does not connect equipment, clear live records, expose or start observing. Retain
the previous plugin/Profile for rollback. Old builds cannot use the new recovery;
all archived originals and failed-observation history remain inspectable.
