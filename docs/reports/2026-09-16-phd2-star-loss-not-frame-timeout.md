# 2026-09-16 — PHD2 lost star misreported as frame timeout (.208)

## Recorded failure

The .207 V460 Cyg run `UVEX-20260916T133651Z-0b4412b9f2a547a`
reached the ATR probe stage, but did not capture an ATR probe/science frame.
PHD2's debug log shows a normal exposure and guide-star SNR 66.9 at 21:41:10
local time. Frames 47–50 completed at 21:41:13–20, while native star detection
reported low-SNR/low-HFD failures (SNR 2.9 on several frames). The owner confirmed
substantial cloud. This is consistent with obscuration, but the logs alone do not
uniquely identify the physical cause.

The coordinator waited only for `GuideStep`, ignoring `StarLost` as a terminal
result of that wait. After ten seconds it displayed `PHD2_GUIDING_FRAME_TIMEOUT`,
despite completed exposures and LostLock evidence. Cleanup then sent stop_capture;
the subsequent interrupted exposure is not proof of an earlier USB/camera fault.

Pre-probe optical windows also need careful interpretation: at 21:40:42 a window
was accepted with an explicitly enabled supervised precision warning, with
16.22/18.30/17.89-pixel target-to-slit residuals, not the 2-pixel exact-placement
criterion. Reaching stage 8 does not prove exact slit placement. Immediately
re-measuring after in-place recovery added another window before exposure.

## Implementation

- The shared PHD2 client reports a typed lost-star exception on `StarLost` or a
  queried LostLock state. A pause/stop/epoch change ends the wait; real silence
  retains its bounded timeout. No old image is accepted and no capture command
  is resent by this reader.
- A status-RPC continuation cannot overwrite a newer loss/pause/stop event with
  an older Guiding response. A full-suite race test exposed this second defect;
  it was fixed in production code rather than hidden by extending test timeouts.
- The shared pre-ATR production path publishes structured loss evidence and
  enters the existing `GUIDING_LOST` bounded recovery policy (one attempt), with
  inherited movement/return/time limits, checked stop and fresh acquisition.
  Unknown errors, transport timeouts and identity/safety failures do not gain
  recovery authority. Persistent cloud or exhausted limits can still stop it.
- A successfully measured in-place recovery window can be consumed once by the
  immediate next exposure. The immutable frame must be at most five seconds old
  and match the run, frame hash, connection/guide epochs, lock and configuration.
  Otherwise another fresh window is required. This is not cross-exposure caching.
  The actual guide state/output is checked again immediately before ATR capture.

The Dockable and Advanced Sequencer retain the same plan/runner/client route.
No UI-only acquisition branch, new owner, motion limit relaxation, frozen design
change or observing-data rewrite is introduced. The earlier autofocus sampling
issue is separate and is not declared fixed by this release.

## Verification scope

Fake-owner tests cover StarLost before/during the wait, immediate recovery after
loss, stale status replies, genuine silence, pause/stop, and absence of image-save
or capture-changing requests on rejection. Receipt tests cover one-shot use,
age/run/frame/lock/epoch mismatch and state invalidation. Existing policy tests
retain one recovery attempt and prohibit recovery for generic timeouts.

This change does not claim that software can overcome opaque cloud, nor that the
new route has completed a real-sky observation. Installation checks must not start
new acquisition, guiding, slews, focus, roof or cover motion.

## Release verification and installation

- Final `scripts/build.ps1`: **2,141 tests passed**, no failures; final log
  `output/guiding-208-final-build-2.log`. The earlier full-suite run exposed the
  stale-status race described above. A subsequent run also hit two existing
  wall-clock-sensitive fake-owner tests (short-exposure pipeline and guide-output
  reconnect); the focused PHD2 suite (208 tests) and final complete build passed
  without modifying those tests or production timeouts. Earlier logs are retained.
- Independent reduction checks: Ruff passed; **66 tests passed**. Public-release
  audit reports zero findings/data candidates. `git diff --check` passed.
- All **91** offline UI scenes rendered in `output/guiding-208-ui/`; eight contact
  sheets reviewed, including narrow layouts, focus, failure, recovery and progress.
- Before deployment, the blocked run was cancelled through the production UI
  bridge. Readback was `Cancelled`, focus idle, camera not exposing, mount not
  slewing/pulsing, and focuser not moving. A read-only PHD2 `get_app_state` confirmed
  `Stopped`. No mechanical recovery was requested by deployment.
- Profile and .207 plugin backup plus normal shutdown/install proof:
  `output/deployment-backups/guiding-208-20260916/`.
- N.I.N.A. **3.2.0.9001**, PID **14648**, restarted on 2026-09-16 at approximately
  **22:03:35 UTC+8**. Bridge verified .208, matching artifact hash, idle state and
  backend real control disarmed. Loaded plugin and PHD2 module hashes match the
  published artifacts.
- Installed plugin SHA-256:
  `AD503A5E38E25CEE46EC191281577368DD3B98A3532B163D3E6835E14E6E5EFB`.
- Actual observation, embedded ATR inspector, and calibration-library panels
  activated and screenshots inspected at 22:04:00–03; local proof:
  `output/target-strategy-193-native-20260916T140400Z/` (legacy script prefix).
  New N.I.N.A. log: `20260916-220335-3.2.0.9001.14648-202609.log`; no observed
  XAML/Binding/dispatcher exception or unexpected exit during this UI check.

At 22:04:13, **after** the idle installation and panel checks, a separate new real
run appeared. Deployment sent navigation commands only, not start/resume or
equipment motion; that subsequent run was not interrupted for further UI checks.
Its device connection logs include an ASCOM shutter-state-5 error and a rejected
temporary camera setpoint of 1000 before the requested cooling target of -10.
Those are not a claim of a clean real-sky run or a diagnosis of the earlier PHD2
loss. Frontend real-sky acceptance of .208 remains pending.
