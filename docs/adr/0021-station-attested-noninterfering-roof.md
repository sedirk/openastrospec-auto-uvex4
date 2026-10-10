# ADR-0021: Station-attested non-interfering roof

Status: Accepted; software and real-sky acceptance recorded separately.

Date: 2026-10-09

Owner authorization: the owner confirms that this station's roof clears the mount
in **every position**, and explicitly authorizes the corresponding station/design
change, verification, installation and continuation of tonight's native sequence.

## Scope and decision

Supersedes only the unconditional Park-before-roof requirement in ADR-0010,
ADR-0019 and baseline sections 7/9. The default remains **ParkRequired**.
ADR-0001 ownership, full unattended prerequisites, actual stopped-state evidence,
weather gates, bounded actions and terminal readback remain unchanged.

- `RoofNeverInterferesWithMount` defaults to false. True requires schema-1 owner
  clearance attestation in the SHA-256-bound commissioning preset, including
  evidence ID, confirmer, UTC, basis, exact mount/roof adapter IDs and an explicit
  all-mount-positions-clear statement. Blank, future, missing or mismatched proof
  fails closed. Importing an older package resets the exception to false.
- The mode and package hash are captured in immutable run configuration. Changing
  the Profile during execution cannot alter the captured mode. No current camera
  view, `AtHome`, `AtPark`, restart or failed Park automatically enables it.
- In this mode, after fresh full safety/weather/identity/horizon checks and a
  stopped mount (no slew, pulse guiding or tracking), the roof may open directly.
  PHD2 must be stopped; no pre-opening Park/slew is sent. RRCI/controller locks
  remain authoritative and are never overridden by this policy.
- At normal completion or commissioned Unsafe, stop acquisition/guiding and
  mount motion, close the roof and verify Closed **before** independently trying
  Park. A rejected/timed-out Park cannot prevent closure or reopen the roof, and
  remains an explicit closeout failure. Stop or roof failure is not success and
  cannot authorize the later Park. Cover closure retains its existing route.
- Single-target and native-night execution use the same production runner and
  adapters. Ordinary cancel still performs no mechanical recovery or roof motion.
  Weak supervision never acquires automatic roof authority.
- This is station-specific evidence, not a machine-independent default. Geometry,
  installation or adapter changes require a new owner confirmation/package.
  The software does not certify clearance or infer a correct Park position.

## Acceptance

2026-10-09 owner-authorized native-night extension: an explicitly saved, default-off
`HomeBeforeParkOnFinalize` option may insert one bounded N.I.N.A.-owned Home
**after verified roof closure and before independent Park**. It requires the same
attested all-position-clear mode, current idle owners and identity checks. Home
must complete and have two stopped AtHome readbacks; failure stops and prohibits
the following Park. Already parked mounts are not unparked for this option.
It does not change the opening policy, ordinary-cancel semantics, old ledgers,
mount limits, Park definition, or grant startup/reconnect homing authority.

Tests cover defaults, absent/invalid/mismatched attestations, mode hashing/drift,
stopped-state checks, stop → close → park order, Park failure/timeout, cancellation,
and common production entry points. UI shows the saved roof mode without an
unattested bypass switch. Installation uses confirmed idle, backups, normal exit,
exact artifact checks and actual three-panel checks. Formal native start provides
opening evidence; full-night/Unsafe hardware acceptance must be stated separately.
