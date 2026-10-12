# ADR-0034: Sole-owner clock recovery after a failed target

Status: Accepted; real-device recovery remains a commissioning check.

Date: 2026-10-12

Authorization: the owner requested automatic resynchronization/reconnection for
recurring OnStep UTC errors instead of immediately abandoning the entire night.

## Decision

Extends the existing Night Setup clock recovery to a joined, stopped target
boundary. It does not weaken UTC validity, alter device ownership, or authorize
in-place reconnection while a target still owns acquisition or guiding.

- First retain the existing bounded read-only confirmation of suspect telemetry.
  The old target remains failed if time does not recover during that window.
- After the coordinator/child task joins, use the existing no-mechanical cleanup,
  owner idle proof and checked guide-state retirement. Unknown stop or unresolved
  independent motion debt prohibits clock repair and subsequent targets.
- An active native night configured to continue after failed targets may precharge
  and durably journal one terminal-boundary clock-recovery attempt for the night.
  The existing per-run startup calibration of UTC remains unchanged. Only exact UTC
  unavailable/offset/recheck failures qualify; clock policy errors do not.
- Reuse the same master N.I.N.A. telescope mediator and selected Profile TimeSync
  connection route used by startup. Do not create another ASCOM instance, scan a
  port, send serial commands, restart PHD2 or change firmware. Keep weather, cover,
  roof, horizon, configuration identity, deadline and cancellation checks active.
- Bound disconnect/connect and recovery lifetime. Require two consecutive valid
  UTC samples and unchanged device identity, pier side, tracking and stationary
  state after reconnect. Failure/timeout/cancellation stops further targets; an
  outstanding owner operation is not assumed cancelled or idle.
- Confirmed repair permits skipping the failed target, not declaring it completed
  or resuming its old pointing/slit/guide state. Preserve existing FITS and counts.
  The next target runs full canonical startup and fresh positioning. A second
  persistent in-target error in the same night is not another boundary reconnect attempt.
- The original failure is not put into the ordinary target-quality whitelist.
  Continuation requires an affirmative recovery receipt. A stopped old sequence
  is not automatically resumed by installation, restart or this implementation.

## Verification

Test precharge/journal before reconnect, exactly once, known codes only, idle and
settled boundary prerequisites, cancellation, failed persistence, rejected or
timed-out recovery and strict clock postchecks. Preserve historical acquisition,
science, same-route frontend and native skip regressions. On-sky commissioning
must separately demonstrate real owner reconnect/time readback and fresh next-target
positioning; offline tests are not evidence that the physical RTC was repaired.
