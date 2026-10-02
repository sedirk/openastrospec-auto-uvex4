# ADR-0019: Native N.I.N.A. spectroscopy night sequences

Status: Accepted (implementation and simulator acceptance separate from real-sky commissioning)

Date: 2026-09-20

Owner authorization: implement the first-version native Advanced Sequencer proposal and install.

## Decision

Supersedes only the per-target normal finalization and failed-target waiting portions of
baseline section 7 / ADR-0009 when a target is executed inside the explicitly started
`OpenAstroSpec · 光谱整夜序列` native container. ADR-0001 ownership and ADR-0010
full-unattended prerequisite/park-before-roof invariants remain unchanged.

- The editable night container uses N.I.N.A. `SequentialStrategy`, ordered native
  UVEX target containers, conditions, cloning and template persistence. It is not a
  competing acquisition engine. Every target retains the canonical eleven-stage
  shared host/factory/runner route.
- A night reserves the shared host and cross-process real-observation lease through
  target gaps and closeout. Dockable starts and main-focus work cannot race into gaps.
- Each target saves its own frame count, maximum attempts, fixed exposure (zero
  means the existing probe ladder) and failure policy. Effective configuration is
  immutable and hashed without rewriting the global Profile. Fixed exposure still
  passes all probe/science quality gates; it is not a bypass.
- Target finalization stops/reconciles acquisition, illumination and PHD2 but defers
  cover/mount/roof to the night. Final closure uses the same selected-adapter methods
  and checked `AtPark && !Slewing` then closed-shutter evidence as single-target mode.
  A mechanical home is not inferred from park or vice versa.
- Quality skipping is an explicit allow-list plus confirmed idle and settled motion
  ledgers. Safety, device/identity loss, persistence failure and unknown recovery
  never become skip/success. Failed targets retain failed manifests and night outcomes.
  The existing bounded within-target retries remain; version one adds no unlimited
  full-target restart or new motion-budget reset.
- A normal deadline/morning threshold is checked at stage and saved science-frame
  boundaries. It never destroys an in-progress long exposure. The whole-night safety
  subscription remains active across gaps and requests immediate abort/stop on unsafe.
- Ordinary cancel/takeover does not grant new mechanical motion. Explicit `结束本夜并收口`,
  normal completion/deadline, or an unsafe event on the commissioned safety chain
  permits the configured night closeout after owners join and stop is confirmed.
- First version accepts sequential UVEX target children only. It rejects motion-bearing
  native triggers until meridian flip/dither/focus reacquisition is integrated. Preparation
  instructions can precede the night container in the native sequence.

## Deployment/acceptance

Installation cannot turn a weak-supervision Profile into unattended authorization.
Real night validation requires configured normal cover/roof closure and full safety
capabilities. Existing single-target weak-supervision use remains available. No roof,
mount or camera is operated merely to test the editor or install this change.

Required evidence: serialization/clone, independent target plans, reservation exclusion,
failed-target stop/skip, deadline boundaries, cancellation-vs-closeout and UI checks.
Real-sky multi-target plus fault-injected closeout remains staged commissioning work;
source tests and loaded native panels are not that acceptance.
