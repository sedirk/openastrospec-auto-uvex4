# Frontend-loop closeout — 2026-09-07

This report separates the **installed 0.4.0.137 supervised on-sky result** from the
**0.4.0.138 source follow-up**. It does not certify unattended operation, precision
slit placement, photometric stability, or every historical recovery branch.
All times below are UTC+8 unless an identifier explicitly contains a UTC timestamp.

## Scope of the source closeout

- A current-user named-pipe controller invokes the same visible N.I.N.A. Dockable
  `ICommand` objects on the UI thread. There is no second acquisition state machine,
  computer-use dependency or controller-to-device shortcut. Real control and
  slit-precision-warning probing require separate, visible, process-local consent.
- G3/PHD2 recovery now handles selection/capture lifecycle, bounded native reselection,
  fresh target/slit evidence, near-neighbour WCS handoff, guide-epoch continuity and
  durable return accounting. The default G3 solve ladder is 5/10/15 seconds; the
  unproductive 2-second first tier is removed. Recovery does not erase old debt,
  invent target identity or reset action budgets.
- [ADR-0013](adr/0013-supervised-slit-quality-warning-probe.md) records the owner's
  explicit supervised-quality exception. Actual ATR spectral signal and trace
  clipping still decide frame acceptance. Device safety, identity and ambiguous
  movement remain hard boundaries.
- ATR image pixels, the 1D curve, annotations and WPF text are separate preview
  layers. Stretch affects only pixels; image zoom does not resize the toolbar or
  spectrum panel. Embedded and pop-out viewers use the same separation.
- FITS provenance schema 2 uses stable bounded ASCII identifiers plus lossless
  UTF-8 Base64 chunks for original Unicode/long labels. `NINATYP` preserves the
  requested N.I.N.A. type; native `IMAGETYP=LIGHT` is not a substitute for
  `UVEXSTG=SCIENCE`. Saved files are verified read-only and never patched afterward.
- The QHY first-frame gate no longer waits forever for a first *quality-accepted*
  photometry frame. A real first frame can produce a visible quality warning;
  retained/rejected/accepted counts remain distinct.

The [frontend interface and dated implementation history](model-frontend-closed-loop.md)
describe command semantics, commissioning evidence and limits in more detail.

## Installed .137 real frontend result

| Item | Verified result |
| --- | --- |
| Production run | `UVEX-20260906T163102Z-edeab79147c5486` |
| Controller run | `frontend-model-loop-20260906T163102Z-ccb41ffb` |
| Start / completion | 00:31:02 / 00:36:24 |
| Frontend / durable terminal | `Completed`, 11/11; revision 176 `RUN_COMPLETED` |
| Target | 天津四 Deneb |
| Probe sequence | 0.1 s rejected for low signal; new 3 s probe accepted |
| ATR science | Three 3 s frames, all three retained and accepted |
| Mid-run repair / manual Resume | None |
| Loaded plugin DLL SHA-256 | `5F9BE4A6CA8A3C84D6BD669C9956071511013874A6A6BBFB9BDDB70601D20FD1` |

The controller used the visible `arm-real-control`, `arm-slit-quality-warning`,
`select-real` and `restart-real-run` commands. The production runner performed QHY
wide-field witnessing, fresh G3 WCS correction, PHD2 placement/guiding, QHY
photometry, ATR exposure selection, science and finalization. Controller success
required the bound run ID, all 11 gates, real-adapter provenance, the durable
terminal journal, accepted science counters and file SHA-256 verification.

| Science time | Exposure | Continuum SNR proxy | Clipped trace columns | Pre-exposure slit residual |
| --- | --- | --- | --- | --- |
| 00:35:21 | 3 s | 395.93 | 0% | 2.8516 px |
| 00:35:31 | 3 s | 264.40 | 0% | 3.2801 px |
| 00:35:42 | 3 s | 212.46 | 0% | 3.5794 px |

Independent read-only checks of all five actual FITS files matched hashes, target,
run/capture identifiers, role and exposure. The original `天津四 Deneb` label and
long Night Setup identifier round-tripped through the chunked headers. All three
science frames reported a camera temperature of -10.0 °C.

### Remaining quality limits

- This is **supervised engineering closure with recorded warnings**. The science
  FITS retain `SLITWARN=True`, actual `SLITRES`, and
  `PHD2GRD=DegradedSupervised`; native settle failures were not relabelled success.
- QHY photometry retained seven frames but accepted **zero**, with
  `STAR_DETECTION_CAPPED`. Two accepted QHY acquisition frames are not two accepted
  photometry frames. Finalization stopped the optional photometry job as requested.
- A common-aperture, adjacent-background diagnostic extraction shows corresponding
  spectral structure, but relative integrated counts are about 1.00/0.66/0.50.
  Throughput stability remains open. These are not wavelength-/response-calibrated
  or absolute-flux results.
- The configured frontend finalization left tracking on. A separately authorized
  N.I.N.A. home operation then obtained two fresh `AtHome=true`, tracking-off,
  not-slewing and no-pulse readbacks. PHD2 was stopped, ATR idle and UVEX illumination
  off. No roof action was performed; this maintenance did not alter run acceptance.

## .138 follow-up: repeated native settle notifications

The outbound fine-lock loop previously issued native `guide`/settle after every
verified `set_lock_position`. PHD2 already continues guiding after the lock change;
each extra settle timeout broadcasts an unsuccessful `SettleDone` to clients,
including N.I.N.A., causing repeated notifications during intentional microsteps.

In an already-established **explicitly supervised** guide session, .138 observes
existing GuideStep updates after verified exact-lock readback instead of issuing
another native guide/settle request at each outbound step. It retains the original
bounded deadline, requires three distinct fresh optical residual frames and checks
connection/guide epochs, current lock, pause state and pending native operations.
Transient loss of continuity is rejected even if followed quickly by Guiding.

This evidence is labelled `phd2-post-lock-readonly-window`, never synthetic
`SettleDone`. Calibration authority is capped at supervised/degraded, and
`PHD2EV=READONLY-WINDOW` distinguishes it in new FITS headers. Initial guiding,
native recalibration, strict-mode steps and return recovery still use native
settling. Real loss, disconnection, calibration and safety notifications are not
globally muted. No installed PHD2 settings or binaries are changed by this fix.

**Deployment boundary:** .138 is built and regression-tested in this closeout,
but is not installed or on-sky verified here. The .137 result above does not prove
the new branch has run on hardware. Installation/restart and a supervised replay
must be separately authorized before closing that remaining verification item.

## Reproducibility and publication boundary

- Full Windows build, design/layout/coordinate checks and solution tests run through
  `scripts/build.ps1`: **1,217 tests passed**, including 507 N.I.N.A. plugin tests
  and 139 PHD2 tests. The new observation tests use a client that rejects every RPC.
- Python reduction remains pinned; `ruff check src tests tools` and `pytest -q`
  pass (**66 tests**) without scientific dependency changes.
- The four frozen-record hash checks pass. The strict current-snapshot audit has
  **zero text findings and zero binary/data candidates**; `git diff --check` passes.
- Run `scripts/audit-public-release.ps1 -Strict` and `git diff --check` before
  publication. A clean current snapshot does not certify older Git history.
- Controller manifests and maintenance proofs remain under
  `%LOCALAPPDATA%\UVEX-ADV`; observatory evidence and original N.I.N.A. FITS remain
  in their configured local locations. UI renders, test logs and diagnostic
  extraction products remain in ignored `tmp/` directories. No FITS, local
  credentials, machine configuration, SDK binaries or build artifacts are included
  in the source commit.

No real acquisition, homing, restart or device reconfiguration was performed for
the documentation/commit closeout or the .138 regression tests.
