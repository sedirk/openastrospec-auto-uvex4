# Supervised multi-target frontend commissioning — 2026-09-07 night

This is a separate observing session from the early-morning
[.137/.138 closeout](commissioning-night-2026-09-07.md). Times below use UTC+8;
run identifiers contain UTC. Status at 04:06 on September 8: **three target types
completed on earlier versions; .151 is installed, but its regression stopped on
an old installed UVEX service component; 10 Lac has not completed**. A failed run is
never relabelled after a later repair or successful retry.

## Scope and acceptance

The owner authorized real multi-target testing, necessary bounded homing and
N.I.N.A. restarts, and the explicit supervised slit-precision warning/probe policy
in [ADR-0013](adr/0013-supervised-slit-quality-warning-probe.md). The owner reported
an open roof, clear sky and no person operating the equipment nearby. This is
weakly supervised operation, not an unattended-weather/roof certification.

All observing runs used `invoke-model-frontend-closed-loop.ps1`, the current-user
named pipe, and the same visible Dockable `ICommand` objects as manual operation.
The controller did not run a second acquisition state machine, issue equipment
RPCs for individual stages, or use computer-use automation.

A successful row below requires the bound production run to finish 11/11 stages,
durable `RUN_COMPLETED`, a successful controller result, accepted science counts,
and independent read-only FITS SHA-256 and target/run/capture/role verification.
Actual spectral signal is inspected separately. A solver success, preview image,
lock readback or simulated run alone is not acceptance.

## Verified results

| Target / test class | Installed version | Production run | Science accepted | Pre-exposure slit residuals |
| --- | --- | --- | --- | --- |
| Deneb / bright A-type baseline | .142 | `UVEX-20260907T150423Z-d0fd1dfbadbd4a2` | 3 × 3 s | 2.2348, 0.9098, 1.9361 px |
| Gamma Cas / emission-line star | .143 | `UVEX-20260907T154051Z-ed4255e3173447c` | 3 × 3 s | 0.2210, 2.7412, 0.3211 px |
| Scheat / cool giant | .145 | `UVEX-20260907T162141Z-7fcef995e4e3410` | 3 × 15 s | 3.1994, 1.2064, 0.9203 px |
| 10 Lac / fainter O-type star | .150, restored 2 s guiding | `UVEX-20260907T191857Z-5990bef3af05495` (horizon-window stop) | 0; not completed | Earlier runs retained; see below |

Associated controller IDs:

- Deneb: `frontend-model-loop-20260907T150423Z-6351d7fb`.
- Gamma Cas: `frontend-model-loop-20260907T154050Z-90405829`.
- Scheat: `frontend-model-loop-20260907T162141Z-7eceb1a6`.

Each completed run required zero external per-stage interventions. Repairs,
configuration changes and maintenance occurred between distinct runs.

All accepted science FITS retain `SLITWARN=True`,
`PHD2GRD=DegradedSupervised`, and `PHD2EV=READONLY-WINDOW`. Residuals above the
strict 2 px criterion are not called precise placement. Three corresponding
uncalibrated extractions show absorption structure for Deneb, strong emission
features for Gamma Cas, and broad absorption structure for Scheat. Throughput
varies; no wavelength/response calibration or absolute-flux result is asserted.

Deneb and Gamma Cas each retained two probe files before science; Scheat retained
three (0.1 s and 10 s rejected for selection, 15 s accepted). All five, five and
six respective FITS inputs were independently verified without modification.

## Repairs distinguished from their on-sky evidence

### .141: exhausted WCS return budget

A safely returned WCS failure could fall through into another local acquisition
branch even when the unchanged global budget could not afford the smallest
outbound/return pair. `G3WcsRecoveryPolicy` preserves the original clock and
counters and reports `G3_WCS_CENTERING_BUDGET_EXHAUSTED_RETURNED` in that case.
It does not reject every returned failure or allocate a new budget.

### .142: recognition envelope versus PHD2 handoff envelope

The broad 100 px recognition window was incorrectly also treated as an affordable
fine-placement handoff. A 71 px handoff could not fit both approach and return
within the existing 100 px cumulative budget. `Phd2CoarseHandoffPolicy` derives
the affordable handoff from action, time and round-trip motion limits, capped by
the commissioned 20 px acquisition window. Recognition remains separate.

Deneb provides the live regression: a fresh 20.824 px residual retained WCS
ownership; a second correction reached 19.485 px before PHD2 took over. It then
completed the real science sequence.

### .143: PlateSolve3 received stale FITS coordinate hints

The native solver adapter saved an input whose FITS target/telescope coordinates
still described a prior pointing. Inspection of the installed solver confirmed
that these header coordinates take precedence over its command-line hint.
Changing only the requested solver parameter therefore did not apply the intended
fresh wide-field hint.

`PlateSolveHintImage` supplies an isolated pixel/metadata copy with consistent
J2000 target and telescope hints, no inherited WCS coordinate cards, and
`HINTONLY=true`. Original observation bytes and metadata are not rewritten;
the source FITS hash is checked before and after. Hints remain non-authoritative;
formal WCS plausibility and motion/identity gates remain required.

Gamma Cas had previously spent about 19 minutes in acquisition and returned
without science. On .143 its first 5 s G3 exposure solved with the corrected hint,
and the production frontend completed. Scheat subsequently exercised bounded
neighbour solving plus measured-target handoff when target-centred solving failed.

**Neither the proposed 5°→2° one-time QHY Sync trigger nor the proposed enlarged
coarse motion budget was applied.** The original thresholds remain in force.

### .144/.145: detector-fixed slit stacking, not yet a faint-target solution

10 Lac reached the PHD2 handoff but successive 2 s guiding frames did not reliably
recognize the dark physical slit. A bounded three-frame detector-fixed mean was
added for slit detection only. Target and guide positions still come from the
newest individual immutable frame. Same-run LED identity, unchanged guide
epoch/lock/exposure, detector geometry, mount binding, saturation masks and the
original contrast threshold are required. No extra capture budget is allocated.

.144 incorrectly applied the newest-frame 5 s age limit to every member of a
three-frame window. .145 keeps the newest target/guide age at 5 s and bounds older
slit-only members by the existing 10 s capture window. Regression tests cover the
ordinary 2 s cadence plus transfer time.

The .145 run `UVEX-20260907T161103Z-cce772fcdd1f411` still paused at 5/11 with
zero science: its last stack measured only 1.45 sigma against the unchanged
3-sigma requirement. This is evidence of a remaining limitation, not proof that
stacking solved the problem. No remembered midpoint was promoted to a fresh
detection to bypass the failure.

## Ordinary guiding exposure trial

After Scheat completed, a new immutable trial definition/preset was generated
through the normal commissioning tool. Only ordinary off-slit guiding exposure
changes from 2000 to 4000 ms. Direct-target exposure remains 10 ms. Geometry,
target identity, contrast, action, motion, return and elapsed-time limits are
unchanged. Provenance explicitly labels this as a parameter trial, not a new
optical or calibration measurement.

- Preset: `DF-UVEX4-FIELD-20260908-GUIDING4S-SUPERVISED-TRIAL`.
- Preset SHA-256: `7C772D93E3B322023005F4904AE13B6240ADA5DC69F57098F500A7FF2DFDAB9F`.
- Definition SHA-256: `4FF64FC058AD8722E0695BBC6138C1C16DBB400A0042D3839423E3337D37E9A0`.
- Profiles, installed plugin and PHD2 registry configuration were backed up.
- N.I.N.A. home was confirmed twice at `nina-home-20260907T163358Z-20ce309d`,
  with tracking and slewing off. N.I.N.A. was closed normally and visibly restarted;
  no roof action or raw-data modification occurred.
- .145 remained installed; the visible startup preparation loaded the new package.
- Trial production run: `UVEX-20260907T163641Z-88d09d146b0f4d7` reached genuine
  4 s slit measurements at 3.50, 4.06 and 3.08 sigma and started fine placement.
  It still paused at 5/11 with zero science after a different timing failure.

### .146: leave time for the mandatory fresh optical window

The .145 4 s trial exposed a redundant wait: post-lock GuideStep observation
could consume approximately 15 seconds before starting the three required fresh
residual exposures, all within the unchanged 30-second stage. One rejected slit
frame plus transfer time then made completion impossible. Cancellation by that
deadline was also incorrectly described as a completed guide window failing its
precision threshold.

For the already-authorized supervised outbound path, .146 confirms the first
valid post-lock GuideStep and immediately yields to fresh optical acquisition.
It does not claim tracking is within tolerance or synthesize native settle
success. All queued loss/pause/epoch/lock transitions still reject continuity.
The new optical window must contain three distinct newer immutable frames and
pass the same target/slit/guide and current-lock checks. Strict-mode/native
settling, all motion accounting and the original stage deadline are unchanged.

`PHD2_FRESH_GUIDE_WINDOW_DEADLINE` now distinguishes an incomplete timed-out
window from a complete measured precision failure, including a specific Chinese
explanation. Tests use a proxy that forbids every device RPC. The full .146
build passed 1,233 tests and was installed between verified-home boundaries.

The 4 s run `UVEX-20260907T165102Z-aa8071700893466` failed initial slit
recognition before reaching this timing branch. Its stack also exceeded the
unchanged 2 arcsec mount-binding span, but a separate diagnostic mean still
failed the contrast threshold; increasing that bound would not establish success.

A second immutable trial changed only ordinary guiding exposure to 6000 ms:
`DF-UVEX4-FIELD-20260908-GUIDING6S-SUPERVISED-TRIAL`, preset SHA-256
`526F2C6EC3ED32365BDB50C2F6BD2F05235A71D3657C55B34F1B140CCEF640BC`,
definition SHA-256
`652DAC4DF770217266C648C3CE0C3F72372FC817B7DEBB63D4FBD3DD44F215CE`.
The former definitions/presets and PHD2 configuration backup remain intact.
Three 6 s frames cannot use the older 10 s slit-stack window; this limitation is
explicit in the trial provenance, not bypassed by increasing frame ages.

Run `UVEX-20260907T170256Z-b4a6a5da17014f8` passed initial single-frame slit
recognition and one complete post-lock optical window, but still timed out in
other post-lock windows after one low-confidence frame. It returned its runtime
lock origin, stopped guiding and paused at 5/11 with zero science. A further
unsaved GuideStep still consumed approximately 7 s before the mandatory FITS
window. The failed runs remain failed.

### .147: let the mandatory FITS window establish guide continuity

The supervised branch now establishes an **unaccepted** exact-lock/epoch/event
baseline and starts fresh-FITS acquisition immediately. The existing sole-owner
`SaveCurrentGuidingFrameAsync` already waits for a newer GuideStep, binds its
immutable FITS to that step/lock and rejects guide-epoch discontinuity. There is
no extra unsaved exposure first. Acceptance still requires three distinct newer
optical frames; zero, one or two frames grant no continuation authority.

This does not fabricate native settling, claim precise tracking, change the
strict/native path, reduce the number of required frames, or extend the 30 s
stage deadline. Same-frame target/slit/guide evidence, all motion/return budgets
and the original native settle outcome remain intact. .147 was installed after
verified home and a normal N.I.N.A. restart. The next 6 s trial is
`UVEX-20260907T171900Z-1721f25a65644cf`. It completed one post-lock optical
window in 28.115 seconds but a subsequent window still obtained only 2/3
frames after dark-slit rejection. It paused at 5/11 with no science, was cancelled
through the frontend and returned home with receipt
`nina-home-20260907T172813Z-b02938a0`. The proposed 45 s stage trial was not
generated into a loadable package or applied.

### .148: correct the slit-position authority, not the exposure deadline

The operator identified the underlying design error: an unilluminated stellar
frame need not show the slit, especially for a faint target. Increasing ordinary
exposure or stacking dark stellar frames only treated that symptom. ADR-0003/0006
already assign physical-slit measurement to the run's LED OFF/ON/OFF sequence.

The shared production fine-placement and pre-spectrum verification routine now
uses **fresh target/guide positions plus the same run's LED-measured slit**. It
does not run a dark-slit detector or a slit-contrast retry/stack on stellar frames.
The retained LED evidence must pass aperture/width identity, source FITS and
identity-evidence hashes, run/configuration/Night Setup/preset identity,
camera/binning/full-frame geometry and connection-epoch checks. UVEX state and
LED-OFF readback are checked before/after each fresh exposure; focus must remain
valid and unchanged across that exposure. A changed configuration, sensor or
optical state invalidates the reference rather than authorizing a remembered
pixel. A mount pointing change alone does not move the physical slit on its
sensor.

Residual evidence explicitly records `slitGeometryAuthority=RUN_LED_OFF_ON_OFF`,
the original LED measurement time, source paths/hashes and current-state binding.
Target/guide positions still come from each distinct immutable fresh frame;
neither an old target coordinate nor a requested PHD2 lock substitutes for an
observed star. Three fresh residuals, identity, guide-epoch and motion/return
budgets remain unchanged. The old dark-frame helpers remain diagnostic/test
utilities, not the production slit authority.

The .148 build passed 1,258 tests with zero warnings/errors. It was installed at
a verified home/idle boundary and N.I.N.A. restarted normally at 01:57:19 CST,
PID 26764. Plugin SHA-256:
`686AF7AE90025489998E319FE5A8204BEF6205BAF70C1A7AE79B3633EBEB3209`.
The initial on-sky regression retains the 6 s package to isolate this correction;
its completion is not inferred from the earlier three targets or unit tests.

The first .148 regression, `UVEX-20260907T175832Z-c5fc43540b084d0`,
passed fine placement using this run's LED-measured midpoint and distinct fresh
stellar frames. Its LED width identity was 2.75 ± 0.5 px for the selected 15 µm
slit; measurement contrast was 8.69 sigma. No dark stellar-frame slit detection
was required. A 600 s probe was accepted with continuum SNR 224.605 and no clipped
columns. Nevertheless, the run paused at 9/11 before accepting any science frame:
`G3_MOTION_LINEAGE_AGGREGATE_INVALID` incorrectly applied the spent acquisition
clock to the already-settled downstream science stage. This is a failed run, not
a completed observation. It was cancelled via the frontend and home verified at
`nina-home-20260907T182446Z-6353a1be` before deployment.

### .149: settled accounting and the spectroscopy preview layout

`G3SettledScienceContinuationPolicy` permits an already-settled acquisition ledger
to retain expired elapsed-time accounting only in the **same run's** exposure
selection, science and finalization stages. State validity, identity checks,
outstanding physical obligations, original counters and limits are unchanged.
Acquisition, fine placement, other stages and cross-run adoption still require
their original time authority. This does not reset a movement budget or allow a
new correction after the acquisition deadline.

The spectroscopy preview now uses the full page width. Manual single-frame tools
are collapsed by default and unavailable while the automatic owner is busy;
cached camera status is refreshed with the dashboard rather than preserving a
startup "disconnected" label. Display controls occupy an optional separate row.
When a valid measured trace is available, a reversible display-only fit centres
its spectral band, retaining the full image and unchanged FITS. The independent
1D diagnostic curve is not stretched with the image or confused with calibrated
spectroscopy. Chinese/English captions are separated through the normal UI
language catalog.

The earlier 6 s guiding setting had been introduced to make the inappropriate
dark-slit detector work. After the LED-authority correction, a new immutable
commissioning package restores the original 2 s expected/off-slit guiding
exposure. PHD2 settings were backed up while stopped. No time, motion, identity,
science-count or quality threshold was changed by that package.

The final .149 build passed 1,275 tests with zero warnings/errors. All 26 offline
UI scenes were rendered and visually inspected, including ATR live/levels/narrow/
manual layouts and Chinese/English master/worker views. This is not a claim that all real N.I.N.A. panels were opened
or that offline renders replace installed Dockable verification. The exact .149
artifact was installed after verified home and a normal close, and N.I.N.A.
restarted visibly at 02:38:30 CST, PID 34932. The live backend reports version
and SHA matching the artifact; startup loaded the restored-2 s package with
verified hashes. No XAML/dispatcher/binding failure was present in the checked
startup log. Its first new frontend run is the 10 Lac row above; completion
remains to be demonstrated.

At 02:59:43, its 600 s probe passed (`ATR_PROBE_TIER_VALIDATED`, high percentile
33,005.456 ADU, continuum SNR 241.243, zero clipped dispersion columns). The
same single-start frontend run entered `RunScienceBlock` and began its first
600 s science exposure. This establishes progress beyond the prior .148 stop,
but accepted science and finalization are still pending at this checkpoint.

### .150: transient Windows file sharing must not poison the run writer

The .149 run did not complete. At 03:07:28 its manifest writer encountered a
Windows sharing violation and latched a persistence failure. It requested pause
at the current action boundary; N.I.N.A. finished saving the first 600 s LIGHT
FITS at 03:09:43, then the frontend paused at 03:09:47. The last durable manifest
therefore lagged the visible state and did not accept that science frame. It must
not be "repaired" by editing the old observation or counting the saved FITS as a
completed run. The controller correctly refused generic resume of `Paused`.

The read-only polling script used a default PowerShell file handle that could
temporarily deny atomic replacement; this reproduces the same sharing failure.
No operating-system handle trace established the unique process responsible for
the original collision. Both the live observer and completion reader now use
explicit `FileShare.ReadWrite | FileShare.Delete`.

The writer retains its exclusive writer lock, frozen bindings, revision checks,
temporary-file fsync, atomic replace and committed-file fsync. Windows sharing/
lock violations alone receive up to 40 × 50 ms retries. The replacement and final
flush are retried independently so a completed replacement is not reissued and
no revision advances twice. Persistent contention, access denial, disk errors and
cancellation remain failures; the writer failure latch is not bypassed. The
visible error now identifies `RUN_MANIFEST_WRITE_FAILED` and explains that this
run cannot resume/finalize, rather than showing a generic optical quality issue.

The old run was cancelled through the frontend; native home was confirmed by
`nina-home-20260907T191523Z-012cca56`. Version .150 passed 1,279 tests, zero build
warnings/errors, and was installed with a normal visible restart at 03:18:23 CST,
PID 19848. Its artifact, installed DLL and live bridge SHA match. The 2 s package
and all hardware/quality/motion settings are unchanged. The new frontend trial
was `UVEX-20260907T191857Z-5990bef3af05495`. It stopped at 4/11 with
`HORIZON_BLOCKED`: at the end of the commissioned observation/recovery window
(04:36:27 CST), 10 Lac was predicted at 29.76°, below the unchanged 30° horizon.
Its current height was still above the horizon; this was a remaining-window
check, not a claim that the star had already set. An implausible G3 solver result
was also rejected rather than authorizing a motion. No science frame was taken.
The run was cancelled through the frontend and home was confirmed by
`nina-home-20260907T192550Z-d3b76ed4`; neither the planned duration nor the
horizon/recovery limit was shortened to manufacture a pass.

A higher Gamma Cas regression started at 03:27:26 CST as
`UVEX-20260907T192726Z-d0007dcef6af4da`. It passed the new LED-derived slit
authority and fine-placement path and reached exposure selection (8/11).
Before the first probe, the real camera temperature remained at 0°C despite
a -10°C setpoint and approximately 69% cooler power. No probe/science exposure
was accepted from that state. An exact-owner cooling-session recovery was
selected for investigation; this checkpoint is not a completed .150 sky test.

The run was explicitly cancelled before any camera maintenance and home was
confirmed (`nina-home-20260907T194042Z-f4a33c35`). The older maintenance script's
conservative SDK-loader check first refused action because the stopped PHD2
process also loaded `toupcam.dll`; this alone does not prove duplicate ownership
of the physical ATR camera. That first maintenance attempt issued no camera
mutations. PHD2 settings were backed up and the stopped application closed
normally, without changing its camera selection or guiding parameters.

The existing exact-owner recovery then performed one N.I.N.A. disconnect, one
exact-DeviceId reconnect and one direct -10°C command. Real temperature readings
resumed (0.8°C, then a downward trend), and the recovery passed its consecutive
target-temperature checks at 03:42:53, last sample -10.3°C. The SDK/driver was not
replaced and no zero/unknown reading was accepted as science-ready. PHD2 restarted
visibly at 03:43:27. A new complete frontend run, not a stitched continuation,
started at 03:44:16 as `UVEX-20260907T194416Z-cdb4fec17b0248b`.

### .151: a narrow handoff window must not truncate target recognition

That .150 Gamma Cas regression stopped at 5/11, without ATR frames. The
post-WCS optimization passed a 20 px search radius into target recognition, so
the actual saturated core around (812.46,373.10) was excluded. A clipped halo
candidate at (823.46,417.07), saturated fraction 0.03047, appeared only 14.09 px
from the slit and incorrectly skipped the target-field solve. The subsequent
guide-selection frame searched the full region and correctly found the actual
core about 60 px from the slit. The real source had not jumped between those
two immutable images; the measurements used inconsistent search scopes.

The fine-placement attempt returned, then further G3 recovery was rejected by
`G3_MOTION_FRESH_BINDING_HANDOFF_LIMIT` (12.84 arcsec continuity delta versus
9 arcsec limit). That limit was not increased. After frontend cancellation,
home was confirmed by `nina-home-20260907T195604Z-c70b88d7`.

Version .151 uses the full commissioned target-recognition radius before the
independent small handoff-window check. Ordinary clipped centroids cannot skip
the formal solver; clipped sources require explicit filled-core topology. Four
additional regressions cover clipped/invalid ordinary-source saturation and a
real core outside the handoff window. Full build: 1,283 passing tests, zero build
warnings/errors. It was installed and N.I.N.A. restarted visibly at 03:58:55 CST,
PID 5016, with matching artifact/installed/live plugin SHA.

### Installed UVEX service mismatch — deployment is not source correctness

The new Gamma Cas run `UVEX-20260907T195954Z-fe932ed32d8c48d` stopped at 4/11
before exercising the .151 handoff repair. Its short LED sequence completed,
but the long sequence's ON command could not be verified; no ON frame from that
failed phase was acquired. Cleanup confirmed LED OFF.

The service serial log at 20:01:13 UTC records `SLON` and `ITEM` transmitted
only 7 ms apart, followed by interleaved replies. The temperature query timed
out, causing the old service to clear LED authority and reconnect COM5. This
explains the missing LED command timestamp; no unknown state was relabelled ON.

Read-only reflection of the **installed** `UvexAdv.Protocol.dll` confirmed that
both LED commands still have `ExpectsResponse=false`. The current source and
built artifact require `true`, with protocol tests. Installed protocol SHA:
`B57AD53BD1035C9AA60E7FE762AB723A56C031843CCC8C39351CD6AF4942D97F`;
built protocol SHA:
`6217DB4A57140049278BAB1EA7539FBCF1F24091912E969A43F16D4E14C440C3`.
Updating only the N.I.N.A. plugin did not update the independent Windows service.
The failed run was cancelled; service deployment requires an administrator/UAC
confirmation that the current backend session cannot supply. No service update
or successful .151 sky regression is claimed at this checkpoint.

The subsequent safe-home receipt `nina-home-20260907T200704Z-a586df7f`
contains two successful confirmations at 04:07:35/37 CST. A fresh 04:12 status
check confirmed the frontend was Cancelled (4/11), mount AtHome, not slewing,
tracking disabled, and the spectroscopy camera idle at -9.9°C with its -10°C
cooling setpoint retained. PHD2 was Stopped in both home confirmations; the
UVEX service reported LED OFF. The roof was not moved. The operator was asked
to confirm the administrator installation prompt; service deployment and
further .151 on-sky regression remain pending that response.

### Operator-requested end-of-night closure

The operator subsequently ended the observing session and explicitly requested
mount home, flat-panel cover closure, and roof closure through the already
configured RRCI replica. This superseded the request to keep testing; no further
observing run or service installation was started.

- `nina-home-20260907T201557Z-659c4991` freshly confirmed the mount was already
  AtHome, stationary and tracking off, with PHD2 stopped. No duplicate home move
  was necessary, and the failed observation remained Cancelled.
- The exact Gemini cover was closed through N.I.N.A. and confirmed Closed with
  LightOn=false in `operator-cover-20260907T201721Z`.
- N.I.N.A. connected the configured `RRCIAdvanced.Dome` adapter at 04:20:09 CST.
  Its actual driver identity was **RRCI Advanced network replica**, with fresh
  ShutterOpen state. The existing replica configuration and primary interlocks
  were not changed; no helper opened the legacy roof driver or sent direct
  roof-network commands.
- A single N.I.N.A. close-shutter request was issued at 04:21:34. The first
  observer lost fresh replica state during the operation and did not assume
  success or resend. Later read-only verification, with no second close command,
  confirmed fresh **ShutterClosed** twice at 04:23:16/18 CST, together with
  mount AtHome and cover Closed. Receipt:
  `operator-roof-close-20260907T202314Z`.

These are explicit operator maintenance actions, not a retroactive successful
`FinalizeObservation` or unattended-route acceptance. The mount remained
AtHome=true, AtPark=false; those two states were not relabelled or fabricated.
All prior raw frames and failed-run evidence were retained unchanged.

Camera shutdown then completed at 04:26:22 CST. The legacy QHY owner confirmed
all recent jobs terminal and disconnected its camera. N.I.N.A. executed native
zero-ramp warming, reported the actual spectroscopy camera temperature rising
from -9.9°C to 21.6°C, confirmed CoolerOn=false while still connected, and then
normally disconnected the camera. Receipt:
`operator-camera-shutdown-20260907T202510Z`. Disconnected zero/default telemetry
was not used to prove physical temperature. A 04:26:49 check still showed the
mount at home without tracking/motion, cover Closed/light OFF, fresh RRCI replica
ShutterClosed and UVEX slit illumination OFF. Software service maintenance and
remaining sky tests were deferred, not restarted during shutdown.

### UVEX service deployment after closure

The operator separately requested service installation after the end-of-night
closure. At 07:37:40 CST on 2026-09-08, the existing administrator installer
completed the service and companion manager update. The exact old binaries and
machine configuration were hash-verified into a local backup; the service was
stopped before copying its database for a consistent rollback copy.

All 345 installed service files and 11 manager files matched their respective
built artifacts. Both the existing `config.json` and `uvex-adv.db` remained
byte-for-byte unchanged. The Windows service restarted as LocalService, Automatic,
with PID 32636 and a healthy loopback endpoint. It stayed **Disconnected** from
COM5; no connect, LED toggle, UVEX motion or acquisition was issued. Historical
positions remained explicitly LastKnown, not fresh live authority.

Read-only reflection of the newly installed Protocol DLL confirmed that both
`SlitIlluminationOn` and `SlitIlluminationOff` now have `ExpectsResponse=true`.
Its SHA-256 is
`6217DB4A57140049278BAB1EA7539FBCF1F24091912E969A43F16D4E14C440C3`.
This closes the installed-component mismatch described above; it is not a new
on-hardware LED or on-sky regression result.

N.I.N.A. remained PID 5016 without a restart. Final readback still showed the
mount at home without tracking/motion, cover Closed/light OFF and fresh RRCI
ShutterClosed. Deployment evidence and rollback files are in the local
`maintenance/service-update-20260908-led-ack` directory. The remaining .151
target-handoff sky regression and 10 Lac result remain pending.

## Device and scientific limits

This machine used the explicitly selected **legacy QHY service** for the wide-field
camera and photometry. N.I.N.A. owned the spectroscopy camera and mount; PHD2 owned
the spectrograph guide camera; the UVEX service owned its fixed serial device.
The successes above do **not** certify a second photometry N.I.N.A. worker or the
dual-N.I.N.A. takeover/yield path.

Deneb/Gamma Cas QHY totals include two accepted acquisition frames; their retained
photometry frames had `STAR_DETECTION_CAPPED` and were not quality-accepted.
Scheat's 17/17 QHY frame-quality acceptance includes acquisition and photometry,
but is not an independent differential-photometry precision measurement.

## Verification and retained evidence

The .151 full build completed with zero warnings/errors and 1,283 passing tests.
Frozen design hashes and `git diff --check` passed. These checks support the source
changes; they do not replace the on-sky results and pending limitation above.

Installed .151 plugin SHA-256:
`F63A9F6EDDC375A21DE6813E34859777762A6903D6CCF989A40E1AADA4DC1F00`.

Production manifests/evidence remain under `%LOCALAPPDATA%/UVEX-ADV/observations`,
controller receipts under `automation/runs`, and verified-home receipts under
`maintenance`. Original N.I.N.A. FITS and QHY data stay in their configured local
stores. Independent review plots, full local timelines, backups and machine
trial configurations are not committed to the repository.
