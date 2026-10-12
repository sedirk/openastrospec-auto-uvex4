# ADR-0035: Continue the same target after verified clock repair

Status: Accepted; real-sky recovery commissioning remains separate.

Date: 2026-10-12

Authorization: the owner requested reacquisition, slit placement and guiding of
the current target after successful clock repair, preserving saved frames and
the original budget, and skipping only when that recovery cannot finish.

## Decision

Supersedes ADR-0034's mandatory skip after successful repair. Its sole-owner,
stopped/joined boundary, durable precharge, one repair per native night, bounded
reconnect and consecutive UTC/identity/pier/tracking postchecks are unchanged.

- The first failed segment remains failed and immutable. After its coordinator
  and native markers join, independently confirm stopped owners and retire its
  control handles through the existing checked terminal boundary. Other unresolved
  mechanical obligations still prohibit continuation.
- After affirmative clock repair, durably record a same-target continuation
  intent, parent/segment IDs, accepted frames/integration, consumed attempts and
  numeric movement debits. Do not count this as a skipped/completed target card.
- Start at most one clock continuation of that target through the same native
  markers, configuration capture, factory and production runner. Reacquire fresh
  catalogue pointing, WCS/target/slit and PHD2 guide evidence, then re-evaluate ATR
  exposure selection before taking only the missing science frames. Do not rerun
  completed frames, re-credit an old final probe, force a second operator-requested
  calibration or reset the logical target's adaptive guiding allowance.
- Inherit science/probe attempts, coarse/fine action counts, cumulative distance,
  original PC-clock motion timestamps (including previously attested ATR credits),
  ceilings and the original target start-plus-duration and night deadline. Clock
  repair time is not credited back. The expired/consumed budget may therefore make
  continuation impossible; record that and skip after confirmed stop.
- Explicitly extend ADR-0027's terminal archival boundary for this one clock
  continuation: old vectors/locks/epochs are abandoned, not imported or declared
  returned. Numeric debits fund a new immutable, fresh-optics-bound ledger; its
  local origin comes from new readback, its single/radius ceilings do not grow,
  and original cumulative/time/attempt ceilings remain capped even through a
  subsequent motion-family handoff. No unbounded search or hidden second retry.
- Target-local optical/budget failure after reacquisition uses the existing saved
  skip policy and independent idle/retirement checks. Unknown stop/identity, unsafe,
  cancellation, dawn, night deadline or still-invalid clock retain their separate
  stopping/closeout semantics. A target-duration deadline is not a night deadline.
- Shared target accounting serves both clock and meridian segments; this does not
  create another acquisition route or authorize automatic observation on install,
  process restart or replay of a journal after a crash.

## Verification

Drive the actual native night and target containers with non-hardware boundary
adapters: successful repair resumes the same card before the next target, fresh
canonical acquisition/placement/science markers execute, already accepted frames
are counted once, and a failed reacquisition skips to the next card. Also cover
repair failure, unknown stop, unsafe/cancel during repair, depleted budget, fixed
target deadlines versus dawn/night deadlines, probe debit and fresh-ledger numeric
inheritance. Preserve meridian and historical productive-run regressions. Tests
and installation do not demonstrate an actual OnStep clock or sky recovery.
