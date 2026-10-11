# ADR-0027: Retire guide recovery handles at a checked terminal target boundary

Status: Accepted; physical commissioning is separate.

Date: 2026-10-10

Authorization: the owner requested automatic clearing of old guide-camera state
on blockage/completion, and asked that ended targets not repeatedly block the
night. A failed target's “return” is its old centering/lock position, not mount
Home, Park or roof closure.

## Decision

This narrowly supersedes the unresolved-target-return prohibition in ADR-0019,
ADR-0024 and ADR-0026 **only at a terminal logical target boundary**:

- After the target stage stack and terminal manifest have joined, while the
  existing exclusive real-run/night lease is still held, stop acquisition by
  its existing owners and independently check ATR idle, photometry tasks joined,
  stationary connected mount, no guide pulse/focus move, and original PHD2 owner
  identity reporting Stopped. Failure to prove these leaves the records pending.
- Authenticate each G3 acquisition/PHD2 lock control envelope against its original
  manifest. Archive its discovery handle byte-for-byte, retaining every original
  counter, endpoint, time and hash. Write an immutable audit first. Archival does
  **not** assert return, target-on-slit, accepted science, calibration validity,
  or physical closeout. A failed target remains failed/skipped.
- Discard target-local cached frames, guide epochs and placement session only
  after that boundary. PHD2 still owns calibration; do not reset or reconnect the
  camera, start exposures, shift a lock, Home/Park or operate the roof to clear
  state. The next target performs normal fresh acquisition.
- Pause/Resume, same-target retries and meridian segments are not terminal target
  boundaries. Their budgets and unresolved motion records remain in force.
  Stale in-memory evidence is still invalidated by the existing pause policy.
- For pre-upgrade terminal records, a newly authorized run may retire them after
  fresh G3 field/mount binding and the same stopped-owner proof. Original record
  hashes must match their original manifest; a changed new-target action hash is
  not a reason to replay an old pixel return vector. Active/paused/crashed old
  manifests, unreadable/ambiguous state and other mechanical/meridian obligations
  are not automatically cleared.
- Known target-local guide settle/epoch/lost-star failures may be skipped only
  under the saved night quality-skip policy and independent successful terminal
  cleanup. Identity, safety, ownership and unknown failures remain hard stops.
- Roof decisions remain ADR-0026: ordinary keep-open standby stays monitored;
  weather, dawn, deadline and explicit end-night still request checked closure.

## Verification

Test terminal vs active/paused state, fresh vs stale/unknown owner readbacks,
envelope/manifest corruption, archival byte preservation, repeated cleanup,
terminal callback before lease release, and no callback on a resumed stage or
meridian segment. Real-sky success is not inferred from tests or installation.
