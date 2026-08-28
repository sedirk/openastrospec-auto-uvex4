# G3 WCS acquisition and deterministic recovery

Status: PlateSolve3 and bounded real-hardware recovery exercised on 2026-08-24;
the corrected projection still requires one installed-runner replay before
unattended qualification.

## Purpose and authority boundary

This path recovers the G3M2210M slit field without compiling a QHY-to-G3 optical-axis offset into the plugin. PHD2 remains the sole owner of G3M2210M; N.I.N.A. remains the sole mount-command owner. The plugin requests a PHD2 full-frame FITS, asks the configured N.I.N.A. plate solver for WCS, and sends only bounded absolute coordinates through the N.I.N.A. telescope mediator.

The WCS and catalog target are the only identity authority in this path. A solve-only image is never promoted to slit or target-placement evidence. Slit geometry and target morphology always come from a fresh detector-fixed OFF/ON/OFF sequence. No image-language model, learned interpretation, fixed two-telescope offset, or ghost position may establish target identity or authorize mount motion.

### N.I.N.A. 3.2 PlateSolve3 orientation convention

N.I.N.A. 3.2's `Platesolve3Solver.ReadResult` copies the second PlateSolve3
result value directly into `PlateSolveResult.PositionAngle` and does not
populate `PlateSolveResult.Flipped`. By contrast, its other solver adapters
normalize their orientation before `Coordinates.XYProjection` consumes it.
Passing the raw PlateSolve3 angle to that projection mirrored the catalog target
to the wrong side of the G3 detector during the 2026-08-24 Deneb run.

For an exact PlateSolve3 solver identity, the runner therefore applies
`projectionRotation = normalize(360° - PositionAngle)` before projecting the
catalog target. The current commissioning preset remains the parity authority;
PlateSolve3's default `Flipped=false` is not represented as a measured parity.
The immutable Deneb sidecar regression maps the old `(1738.9, 600.0)` result to
the corrected `(293.4, 947.3)` detector location. Other N.I.N.A. solvers retain
their already-normalized position angle. A solver change, detector flip, ROI,
binning or camera rotation still invalidates the relevant commissioning
fingerprint and requires fresh evidence.

## Locked configuration

All fields below are copied into `RealRunConfiguration`, included in its action SHA-256, and recorded in the immutable run metadata. Zero or missing commissioning values block real mode.

- `G3PlateSolveExposurePresetSchemaVersion`, `G3PlateSolveExposurePresetId`, and `G3PlateSolveExposureMillisecondsCsv` define a versioned, strictly increasing exposure ladder. The current Dafeng site template is `2000,5000,10000,15000`; the earlier 30 s terminal tier was removed after field use showed that a 15 s frame plus PL3 was the better latency/solve tradeoff. The program still contains no generic site exposure fallback: only the exact retired site ladder is migrated, while operator-authored ladders are preserved.
- G3 acquisition remains at the commissioned hardware `1x1` binning because PHD2 owns G3M2210M and the slit geometry, guide calibration and detector coordinates are all defined at 1920×1080. For roles beginning `PHD2/G3`, the N.I.N.A. plate-solver input uses a minimum software downsample factor of 2. QHY/GS350 centering keeps the active N.I.N.A. profile value unchanged.
- `G3Wcs*` values define independent single-command, radius, cumulative-motion, action-count, elapsed-time, and usable-field-margin limits.
- `G3MotionWorstCaseActionSeconds` is the conservative duration charged for every outbound or return action. It must exceed the post-slew settle duration.
- `G3MotionPostSlewSettleSeconds` is a positive commissioned wait between a completed tiny slew and fresh G3 evidence. It is deliberately not hard-coded to a value observed on one installation.
- G3 focal length, pixel size, gain, binning, camera/profile identity, and PHD2 Windows profile-evidence SHA-256 must already be locked. Plate scale is derived from those locked optics/solver values; no current-installation scale is compiled into source.

The legacy PHD2 `set_exposure → loop → stop_capture → save_image` route applies exposure but cannot apply per-request gain or binning. Ordinary solve/selection captures therefore still require `ExposureApplied`, check FITS exposure, use the validated hash-locked Windows PHD2 profile as gain/binning authority, and cross-check FITS `GAIN`/`XBINNING`/`YBINNING` metadata whenever N.I.N.A. exposes it. `RequestedParametersApplied=false` and `GainAndBinningApplied=false` are recorded honestly on that route.

PHD2 2.6.14 also exposes the owner-native `capture_single_frame` operation, which accepts exposure, binning and gain in one request and emits `SingleFrameComplete`. The LED slit-identity path uses this strict operation at PHD2 gain `0%` (the commissioned G3M2210M native minimum 100) without mutating the guiding profile or Windows registry. It waits for the matching completion event and readable destination FITS, requires `RequestedParametersApplied=true`, and never falls back to the profile's gain 100%. The ordinary guiding profile remains unchanged. This separation is necessary because the corrected-SDK 10 ms LED-ON frame at profile gain 100% saturated about 30.6% of the detector, while the 20 ms frame saturated about 99.998% and could not carry slit-edge identity.

The 15 µm slot is explicitly commissioned as a sub-resolution shared-PSF
family. A runtime frame is not allowed to invent a fresh width from the bright
reflection ridge. When the two-edge fit stays below the shared-PSF separation
floor, production may instead return
`SLIT_DARK_APERTURE_COMMISSIONED_TRANSFER_REGISTERED`, but only if the fresh
paired-reflection geometry, long-frame dynamic range, requested camera
parameters, immutable calibration hash, installation epoch, detector geometry,
live wheel position and signed edge direction all agree. The fresh detector-normal
translation is applied to the previously star-validated edge-to-midpoint offset
and commissioned `3.50 +/- 0.50 px` width; the along-slit coordinate remains the
low-aberration central anchor. The evidence explicitly says that width was not
freshly remeasured. Slots commissioned as direct two-edge families cannot use
this degradation and continue to require both physical edges.

Short exposure duration is not the full-frame delivery time. The commissioned G3/ToupTek path can spend several seconds on sensor readout, USB transfer and PHD2 `LoopingExposures` dispatch even for a 10–20 ms exposure. When exposure changes, the client deliberately discards one possibly buffered frame and waits for the next one. Each required event therefore receives the larger of `exposure + event margin` and a 20-second full-frame delivery floor; two required events receive twice that allowance. An event arriving earlier continues immediately, so this is a timeout ceiling rather than a fixed delay. The 2026-08-27 on-sky run measured approximately 4.4–4.9 seconds per 10 ms saved full frame and demonstrated a sporadic delivery later than the former 10.04-second two-frame ceiling. Failed sequence evidence records the requested exposure and HDR role even if no frame was retained.

The 2026-08-28 G3M2210M investigation established a separate transport/SDK failure signature. With PHD2 2.6.14's bundled x86 ToupTek SDK `59.29465.20250907`, a 10 ms, PHD-gain-100% LED frame repeatedly returned exactly 139 full-width rows clipped to the 12-bit value 4095, followed by an abrupt horizontal boundary. ToupSky using the current SDK, and the same PHD2 executable with only its x86 SDK replaced by `59.30701.20260128`, preserved the optical illumination gradient and returned no fully clipped rows under the same camera, profile, exposure, gain and LED state. The camera already reported FPGA `1.6`, matching the vendor's current G3M2210M package, so no blind firmware reflash was performed. A detector-edge-connected, full-width constant plateau with an exact row boundary is therefore classified as a readout/SDK defect rather than ordinary optical overexposure; it must not be accepted, normalized away or used as slit evidence. Production deployment uses an in-place x86 SDK update inside the formal PHD2 installation, retains the previous DLL as a rollback backup, and requires one fresh OFF/ON/OFF recapture after either an update or rollback.

The executable itself is not customized. Exact executable/SDK hashes, rollback
steps, and the official PHD2 slit-overlay limitation are recorded in
[PHD2/G3 runtime reproducibility](phd2-g3-runtime-reproducibility.md).

## State machine

1. At the current reported mount position, acquire a fresh G3 full-frame at each configured exposure tier.
2. Validate PHD2 runtime identity, locked Windows profile evidence, optical-cover state, slit LED off state, FITS metadata, and image content.
   Every ladder frame is also bound to its immutable FITS SHA-256 and a capture-completion mount RA/Dec/epoch/pier readback. A field older than, topologically different from, or more than the commissioned arrival tolerance from a fresh pre-intent/pre-dispatch readback is discarded without motion.
3. The local coherent-source detector is a recovery classifier and telemetry source, not a pre-solver authority: every otherwise valid immutable ladder FITS is submitted to PlateSolve3 even when the local detector reports zero stellar cores. Only after every formal solve fails does the accumulated ladder content help choose the recovery route.
4. A coherent source may be unsaturated, broad, or saturated. Each structured tier is plate-solved. A saturated structured field may proceed to the deterministic bright-target branch; it is never used for focus. If no tier contains locally recognized structure, a direct stellar plan pauses rather than starting blind motion; an explicitly non-stellar/invisible-target plan may enter the same tightly bounded overlapping-field search because absence of a visible target at the declared coordinate is expected.
5. If WCS succeeds and the catalog target projects inside the configured usable detector margin, capture a fresh OFF/ON/OFF slit sequence.
6. If WCS succeeds but the target projects outside, compute the fresh solved-field-center to catalog-target tangent correction. Reserve both the outbound correction and an adversarial segmented return, atomically persist the precharged intent, then send the bounded absolute coordinate through N.I.N.A.
7. After `WaitForSlew`, verify reported epoch, pier side, horizon, and command residual. Wait the commissioned settle interval, repeat all immediate physical-action gates, read the mount again, and reject excess drift or arrival residual. Only then may a fresh G3 ladder be captured.
8. If every ladder tier fails WCS, run the full deterministic bright/sparse analysis. A stable Night-Setup-bound Star Focuser position, valid immutable captures, independently measured paired-LED slit geometry, and either structured ladder content or an explicitly invisible-target plan may enter the bounded local search. Each search point repeats the same durable intent, settle, fresh-position, and fresh-frame rules.
9. If WCS centering stops, fails to improve target-center residual, or exhausts its limits, return to its saved reported origin before local search. A blocked return pauses automation.

Once PlateSolve3 has formally solved a fresh G3 frame and projected the catalog target inside the usable detector area, that result is carried through the immediately following no-motion LED slit sequence. Carry-forward is allowed only while the solve FITS and WCS evidence retain their recorded SHA-256, detector dimensions are unchanged, coordinate epoch and pier side are unchanged, and the LED reference remains within the commissioned mount-arrival tolerance of the solved frame. The 10–20 ms LED frames then provide slit geometry and short-exposure telemetry only: a low source count in those frames cannot demote the already accepted PlateSolve3 WCS or trigger a return/search route. This is trust in a specific immutable formal solve, not a relaxation of solver success, source binding, topology, or mount-continuity checks.

A failed or diagnostic structured PL3 probe remains solve-ladder evidence only. It is never passed to the LED stage as `trustedSolveProbe` and cannot trigger trusted carry-forward validation. Only a formally accepted, target-inside solve may populate that authority; otherwise the LED sequence and sparse-field recovery continue from diagnostic evidence without pretending that a formal WCS was accepted.

The plugin's local G3 FWHM/ellipticity/core-coherence calculation is likewise diagnostic during acquisition. It may positively verify a stable focus and may warn about broad, elongated, saturated, or unrecognized sources, but it cannot by itself relabel a sparse field as C11 defocus or veto PlateSolve3/target-not-found recovery. The physical C11 focus remains owned by N.I.N.A.'s Star Focuser Pro/Gemini binding; a changed or mismatched focuser position still blocks, while an unchanged locked position lets the formal solver and bounded neighbouring-field route decide acquisition.

A fresh WCS that improves the target-center residual but still leaves the target outside, and every local-search frame that does not identify the target, remain `AwaitingFreshSolve`. They do not settle the ledger. Thus a crash or cancellation retains the obligation to return to the durable origin; only positive target identification or an attested return may write `SettledBudgetLedger`.

`PlateSolveEvidence.ResidualArcseconds` is the catalog-target to solved-field-center separation, not solver RMS. Evidence labels use “target-center residual” to avoid confusing those quantities.

## Durable motion ledger

The canonical file is `%LOCALAPPDATA%\UVEX-ADV\observations\<run>\control\g3-acquisition-motion.json`. Its envelope includes a SHA-256 of the canonical state and is replaced atomically with write-through semantics.

Every outbound and return command charges `command magnitude + arrival tolerance` and one action before the command is sent. Return-attempt reservation assumes the endpoint can be one arrival tolerance in the wrong radial direction. Therefore a maximum return command of `single limit - tolerance` guarantees only `single limit - 2*tolerance` progress. The planner also reserves the worst-case duration of the outbound action and every required return action.

Motion-ledger schema 2 binds tangent coordinates to `ICRS-GNOMONIC-TAN-V1`. Standard forward/inverse TAN projection is used across RA wrap and near the celestial poles, while every command magnitude and origin radius is independently checked as a true great-circle distance. The runner repeats that finite single-command/radius check against a fresh reported coordinate both before writing each intent and immediately before dispatch. Schema 1 and any different geometry identifier fail closed; the plugin does not reinterpret an outstanding legacy offset.

Before adoption or recovery, the runner:

- rejects unreadable, tampered, or misplaced ledgers;
- reads each immutable `manifest.json` and verifies run identity, target/site/horizon/Night Setup/telescope recovery context, action-configuration SHA-256, and commissioning SHA-256;
- leaves cancellation and terminalization motion-free, but on the next explicit stage start may adopt one terminal-run outstanding intent only when its immutable manifest, hashes, context, lineage, epoch, pier side, horizon, counters, clock, and current reported position all validate; it then performs only the already-budgeted return;
- requires one unique budget lineage and at most one outstanding intent;
- validates reported epoch, pier side, horizon, and position before and after every recovery command;
- continues a settled lineage across process or run handoff without resetting cumulative distance, action count, global limits, or the earliest elapsed-time origin. A WCS-to-local-search family handoff tightens single-command/radius and also caps cumulative distance, attempts, and elapsed time at `already consumed + current-family increment`, never above an inherited limit; in particular a larger WCS return step or budget cannot leak into the smaller local-search family.

The family-handoff continuity envelope permits up to 5 arcseconds of normal mount readback/settle variation. The measured tangent offset is retained in the continued ledger and remains charged against the inherited origin, cumulative-motion budget, action count, and original clock. This envelope therefore prevents a 2–3 arcsecond readback difference from becoming a false recovery-context failure without rebasing or resetting any durable budget.

The exposure-ladder payload remains schema 1 because its exposure-only semantics did not change. Schema 2 applies only to the motion ledger whose offset meaning changed to TAN geometry.

If an outbound call is rejected or the process exits after intent persistence, the conservative charge remains. Automatic recovery returns from fresh reported coordinates; it never assumes whether a prior asynchronous command executed.

## Evidence and operator diagnosis

The run evidence directory contains the original immutable FITS plus JSON records for each ladder tier, content assessment, WCS result, projected target, centering declaration, precharged motion ledger, post-slew settle/drift result, fresh validation, local-search point, return, and final summary. Content evidence includes coherent/usable source counts, median SNR, robust background/noise, dynamic range, and sampled saturation fraction. UI preview text distinguishes solved-inside, solved-outside, structured-no-WCS, and cloud/transparency-invalid states.

The durable ledger must be inspected together with the immutable run manifest. Deleting or editing it to “unstick” a run discards safety provenance and is not an authorized recovery procedure.

When the operator has explicitly taken over and intentionally accepts that the old automatic return is no longer applicable (for example after selecting a different target), the N.I.N.A. panel provides **清除旧 G3 状态**. The command first cancels the failed run, copies the exact prior canonical ledger beside the original, records optional current mount telemetry, and closes only the outstanding recovery phase. It sends no mount command, does not claim that the old origin was reached, and does not rewrite consumed motion, attempts, limits, or the original lineage clock. After it reports success, a new observation may be started from a freshly reported mount origin. This audited operator action is the supported alternative to manually deleting or editing JSON files.

## Commissioning checklist

Use simulator and replayed FITS first. Then, under a separately authorized hardware session, measure and lock the exposure preset, optics/profile provenance, WCS parity, field margin, centering envelope, local-search envelope, worst-case action time, and post-slew settle time. Test wrong-way arrival error, pier/epoch change, horizon failure, cloud/featureless frames, saturated structured targets, solver failure, process exit after intent write, command rejection, and restart under the same and a different immutable context. Do not deploy the plugin while N.I.N.A. is running.
