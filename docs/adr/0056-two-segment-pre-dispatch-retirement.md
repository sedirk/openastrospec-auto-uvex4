# ADR-0056: Authenticated two-segment pre-dispatch retirement

Status: Accepted by the owner, 2026-11-20. Installation and on-sky validation are separate.

## Context and narrow supersession

ADR-0050 correctly retires an ended, unissued meridian intent after fresh
owner/stop/safety verification. Its "one original segment" qualifier assumed the
target had not already produced a segment for another bounded recovery.

On 2026-11-20 the target HD 26380 first ended a segment for mount-clock repair,
then completed one same-target continuation segment. A subsequent meridian
boundary wrote `OwnersIdle` with both authenticated segment IDs, but the owner
stopped the sequence before dispatch. The next explicitly started target could
not retire that record because the old implementation accepted only one segment,
so Night Setup remained blocked by `NATIVE_FLIP_UNCONFIRMED`.

## Decision

This ADR narrowly supersedes ADR-0050 only where it requires one original
segment. It does not create a third validation, motion or observation segment.

- A qualifying `OwnersIdle` or scheduled `WaitingForMeridian` intent may contain
  exactly one or exactly two segment IDs. IDs must be safe file-name segments,
  distinct, ordered and terminate with the journal's `RunId`.
- For a two-segment intent, the first ID must equal the immutable
  `nativeTargetRootRunId` in the current segment manifest. The manifest's locked
  `nativeTargetPriorSegments` value must exactly equal the ordered segment prefix.
  The current segment itself must still be a durably completed native meridian
  segment with the explicit passed boundary gate.
- All ADR-0050 checks remain mandatory: original ATR/G3/mount/QHY/PHD2 bindings,
  complete safety chain, fresh remote idle proof, current stationary frame,
  ordered control-retirement audit, final flip-handle move and rechecks after an
  interruption. No target motion, return, focus, calibration, roof action or
  same-target continuation is granted by archival.
- Three or more segments, wrong order, duplicate IDs, a forged prefix, wrong
  root lineage, changed owners, unknown stop, command-pending or corrupt records
  remain hard blocking conditions.

The existing one-handoff/two-segment target budget is also enforced before a new
intent is persisted. If the current in-flight segment plus a requested handoff
would exceed two total segments, the stage stops with target-quality code
`NATIVE_MERIDIAN_SEGMENT_BUDGET_EXHAUSTED`. It must not create another
`OwnersIdle` journal, send a flip command or manufacture a third segment. Once
the owner boundary attests that acquisition and motion stopped, the saved
skip-target policy may run the next native target; accepted frames, ledgers,
counters, deadlines and all safety gates remain unchanged.

## Verification

Use finite owner/device ports and the real native target containers to prove:

- a two-segment clock-continuation lineage is discovered, byte-identically
  archived and followed by ordinary acquisition in the next logical target;
- the next native target actually executes after the documented stop boundary;
- wrong/forged/reordered/duplicate/three-segment prefixes, changed owners,
  unknown stop, corrupt records and safety/cancellation remain blocking;
- the segment-cap gate produces no new meridian journal or flip command and
  reaches the next native target only after checked stop.

Installation, replay and UI tests do not prove a new real-sky meridian flip.

