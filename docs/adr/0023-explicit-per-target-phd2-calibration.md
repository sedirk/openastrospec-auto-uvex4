# ADR-0023: Explicit per-target PHD2 calibration request

- Status: Accepted
- Date: 2026-10-10
- Authorization: the owner requested an editable native sequence with a force-guiding-calibration switch on each target.
- Supersedes: only the absolute prohibition on explicit forced calibration in ADR-0020 decision 1 and baseline §5.3.4. PHD2 ownership and advisory metadata remain unchanged.

## Decision

Each native UVEX4 target saves `ForceGuideCalibration`, default `false`, alongside its coordinates, frame count, exposure and failure policy. The option participates in the locked sequence configuration hash; it does not change the shared Profile or PHD2 settings. Old sequences remain unforced.

At the first motion-free, identity-verified PHD2 guide boundary, a checked option permits one `guide(recalibrate=true)` request. Authorization is consumed before dispatch, including failure, timeout or cancellation. Recovery, pending/inherited movement, post-calibration reacquisition and meridian continuation cannot create another request. A new logical target has its own choice. Independent-transform routes cannot silently ignore the option or use it to acquire calibration authority.

PHD2 performs the calibration and supplies operation-bound completion/failure events. After calibration, the coordinator stops the owned guide operation and reacquires fresh field/slit evidence before placement. Old optical geometry cannot authorize movement. Duplicate plugin geometry/age/guide-rate quality gates remain warnings; star loss, output failures, current device identity, physical limits, motion budgets and return responsibilities remain mandatory.

Following the owner's 0.4.0.259 UI revision, each native target card presents its own fields and calibration switch, alongside native coordinates and an altitude chart. The night container holds night settings only; there is no central target selector/editor. Name-only editing is not catalog resolution. Replacing a target clears stale catalog provenance; serialization and clone preserve provenance for an unchanged identity. The sequence is not editable during its active lifetime. Internal stage ordering remains fixed.

## Verification and commissioning

Cover default-off and per-target clone/JSON round trips, distinct target edits, stale metadata invalidation, runtime edit locking, one-shot consumption, recovery denial, meridian continuation and the existing operation-bound PHD2 forced-calibration client tests. Render the production editor and check the installed native sequence panel.

Software installation does not authorize calibration pulses or an observation. A real-sky test requires an explicitly authorized observing run; the current maintenance verifies software without issuing a calibration, exposure or mechanical command.
