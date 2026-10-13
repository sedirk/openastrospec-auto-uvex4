# ADR-0036: Native preparation cards and an explicit native end area

- Status: Accepted; offline verification and deployment are recorded separately.
- Date: 2026-10-13
- Owner request: Audit duplicate N.I.N.A. advanced-sequence options, reuse compatible
  native cards and provide a clear, editable end-instruction group.
- Supersedes: Only ADR-0026's requirement that the optional closeout choice be the
  final direct child of the spectroscopy night. All ownership, stop, weather and
  checked-closeout requirements remain in force.

## Context

The installed N.I.N.A. 3.2.0.9001 supplies `ConnectEquipment` (including
`Safety Monitor`), `WaitUntilSafe`, native start/target/end areas and sequential
containers. Maintaining another production implementation of safety-monitor
connection is unnecessary. Conversely, native `StartGuiding.ForceCalibration`
does not implement the spectroscopy target's once-only calibration accounting,
post-calibration field acquisition, measured slit placement or motion ledger.

The root end area is not a `finally` block: cancellation and some native error
behaviors can bypass it. Arbitrary equipment actions there must also not compete
with the independent open-roof safety standby introduced by ADR-0033.

## Decision

1. New complete templates use native **Connect Equipment → Safety Monitor** and
   **Wait Until Safe** in their preparation group. Connection is not safety or
   device-identity proof; the locked Night Setup still validates those before
   observation. The former plugin startup card remains loadable under a legacy
   category. Do not replace this with Connect All or connect unassigned devices.
2. Distributed examples remain hardware-free simulations. The native preparation
   group's loop count is saved as **0**; an operator setting up a real sequence
   changes it to **1**, selects real mode and real target configuration. Native
   disabled status is not relied on as persistent authorization.
3. A complete root with exactly one direct spectroscopy night may place its one
   `UvexObservatoryCloseoutItem` directly in the native **End** area. The choice
   is resolved and locked before that night starts, not when the marker is
   reached. Legacy final-child placement continues to work, including reusable
   standalone night blocks. Duplicate or ambiguous choices are configuration
   errors, not permission to pick an arbitrary closure policy.
4. The marker remains declarative. The reserved night's `finally` path or its
   independently owned standby performs the existing bounded, verified closeout
   once. The native end area neither replaces emergency closure nor adds a
   second roof, mount or cover controller. Home-before-Park still uses the
   station-attested non-interfering-roof path and remains opt-in.
5. Custom additions use a standard native **Sequential Instructions** container
   in End, with the small `UvexNightClosedCondition` condition. Compatible native
   cards such as camera warm-up can be dragged into that group. It executes at
   most one pass per group per night after normal verified closeout, or the
   equivalent simulation terminal result. Quality-skipped targets do not forbid
   the group if the remaining night finishes normally with settled boundaries.
6. Open-roof standby, cancellation, emergency/deadline/dawn termination, blocked
   targets, unsettled physical responsibility and failed closeout skip optional
   end actions. Direct unguarded End equipment instructions or End-level
   triggers are rejected; put additions and their triggers inside the guarded
   native group. Native device ownership still applies: never use master-camera
   cards for the PHD2- or worker-owned cameras.
7. Permission for optional End instructions is transient, not serialized or
   copied with a night. Starting or resetting the night clears it. A group's
   attempt is charged before its first action; resetting only that group cannot
   repeat already attempted terminal actions. Ordinary native card error/retry
   behavior remains visible and operator-configurable within that attempt.
8. Keep custom per-target forced guiding calibration, ATR auto-exposure/probe
   acceptance, acquisition continuity, shared-device orchestration and bounded
   guiding recovery where native cards do not implement those contracts. Do not
   add native Start Guiding, Slew or Take Exposure inside the internal target
   workflow as a substitute for its owner-coordinated stages.

## Verification and rollout

Use the installed native assembly types with fake mediators to verify successful,
already-connected, failed and unsafe safety-monitor cases. Exercise the actual
native root engine, including target outcomes, End instructions and negative
boundaries; testing only a boolean condition is insufficient. Round-trip the
distributed JSON with N.I.N.A.'s converter and check legacy block/clone behavior.

No physical observations or movement are authorized by these tests. New complete
templates require plugin 0.4.0.289 or later. Do not hot-rewrite active sequences or
copy the new condition into a machine still running an older plugin. Deployment,
normal shutdown, hardware idleness and weather coverage remain separate checks.
