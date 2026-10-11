# ADR-0026: Protected open-roof standby after a stopped target failure

Status: Accepted; real-hardware acceptance remains separate.

Date: 2026-10-10

Authorization: the owner requested repair of the failed-target behavior and explicitly stated that the selected failure/keep-open option must not close the roof.

The owner subsequently requested a selectable closeout interface in the sequence, explicitly separating single-run and sequence-run closure.

## Decision

This narrowly supersedes immediate final closure in ADR-0019 when the saved native night option `HoldOpenAfterTargets` is enabled. It does not change target acceptance, movement reconciliation, equipment ownership or weather authority.

- With the option disabled, existing checked final closure remains unchanged.
- With it enabled and acquisition/motion independently confirmed idle, both completed/skipped targets and a stopped, blocking target may enter protected standby. A blocking target prevents all later targets. An unresolved return ledger remains unresolved and preserved; idle is not proof of return.
- Standby retains the same night reservation, AIWeather unsafe subscription and immutable deadline. It adds read-only polling of connected/safe monitor status and locked environment selections. It does not acquire frames, reselect stars, calibrate, move, clear debt, close or reopen equipment.
- Explicit end-night, cutoff, dawn, unsafe or loss of safety-monitor/binding proof ends standby and invokes existing checked closeout. Ordinary cancel still sends no mechanical closeout command and hands responsibility back to the operator. Unconfirmed physical stop cannot enter standby.
- Exceptions and failed-target outcomes remain in the night journal and are reported after standby ends. Open-roof standby is never reported as successful completion or as physical closeout.

## Explicit terminal instruction and single-run separation

- `UvexObservatoryCloseoutItem` is an optional, serialized terminal instruction accepted only as the final child of the spectroscopy night, at most once. Its `CloseAfterTargets` switch is locked at night start and overrides legacy `HoldOpenAfterTargets` when present. The night honors the choice even if a failed target prevents reaching the instruction. Disabled ordinary closure means protected standby, not a completed/unmonitored open-roof night.
- The instruction is declarative: it never owns or directly commands a roof. The reserved night calls the existing checked `CloseSequenceNightAsync` interface exactly once when closure is due. That interface performs full cover/park/roof closure independently of single-run completion/failure preferences. Unattended prerequisites, physical idleness, roof clearance and readbacks still apply.
- Dockable/standalone run completion and ordinary-failure roof choices remain the existing immutable Profile snapshot settings, now explicitly labeled as single-run only. A disabled standalone closure is an operator handoff, not unattended standby. An active full-safety-chain unsafe event overrides ordinary-failure closure preferences. A sequence's emergency/deadline/dawn closure is never disabled by either single-run preference or the terminal ordinary-closure switch.

## Verification requirements

Native sequence tests must cover failure with and without unresolved ledgers, no next target, reservation retention, cancellation, explicit closeout, deadline and unsafe exits. The guide-epoch bug that exposed this branch is fixed separately by binding the locally issued settle terminal before testing its success; a failed settle remains failed.

Installation or simulated closeout does not prove real-sky acquisition or weather-triggered roof movement. No maintenance operation automatically starts a sequence or operates the roof.
