# G3 WCS acquisition and deterministic recovery

Status: source-complete for simulator/build validation; real-hardware commissioning is still required.

## Purpose and authority boundary

This path recovers the G3M2210M slit field without compiling a QHY-to-G3 optical-axis offset into the plugin. PHD2 remains the sole owner of G3M2210M; N.I.N.A. remains the sole mount-command owner. The plugin requests a PHD2 full-frame FITS, asks the configured N.I.N.A. plate solver for WCS, and sends only bounded absolute coordinates through the N.I.N.A. telescope mediator.

The WCS and catalog target are the only identity authority in this path. A solve-only image is never promoted to slit or target-placement evidence. Slit geometry and target morphology always come from a fresh detector-fixed OFF/ON/OFF sequence. No image-language model, learned interpretation, fixed two-telescope offset, or ghost position may establish target identity or authorize mount motion.

## Locked configuration

All fields below are copied into `RealRunConfiguration`, included in its action SHA-256, and recorded in the immutable run metadata. Zero or missing commissioning values block real mode.

- `G3PlateSolveExposurePresetSchemaVersion`, `G3PlateSolveExposurePresetId`, and `G3PlateSolveExposureMillisecondsCsv` define a versioned, strictly increasing exposure ladder. `2000,5000,10000` is only a UI example; the program contains no site exposure fallback.
- `G3Wcs*` values define independent single-command, radius, cumulative-motion, action-count, elapsed-time, and usable-field-margin limits.
- `G3MotionWorstCaseActionSeconds` is the conservative duration charged for every outbound or return action. It must exceed the post-slew settle duration.
- `G3MotionPostSlewSettleSeconds` is a positive commissioned wait between a completed tiny slew and fresh G3 evidence. It is deliberately not hard-coded to a value observed on one installation.
- G3 focal length, pixel size, gain, binning, camera/profile identity, and PHD2 Windows profile-evidence SHA-256 must already be locked. Plate scale is derived from those locked optics/solver values; no current-installation scale is compiled into source.

PHD2 JSON-RPC applies exposure but cannot apply per-request gain or binning. The frame gate therefore requires `ExposureApplied`, checks FITS exposure, uses the validated hash-locked Windows PHD2 profile as gain/binning authority, and cross-checks FITS `GAIN`/`XBINNING`/`YBINNING` metadata whenever N.I.N.A. exposes it. `RequestedParametersApplied=false` and `GainAndBinningApplied=false` are recorded honestly and do not by themselves reject a valid frame.

## State machine

1. At the current reported mount position, acquire a fresh G3 full-frame at each configured exposure tier.
2. Validate PHD2 runtime identity, locked Windows profile evidence, optical-cover state, slit LED off state, FITS metadata, and image content.
   Every ladder frame is also bound to its immutable FITS SHA-256 and a capture-completion mount RA/Dec/epoch/pier readback. A field older than, topologically different from, or more than the commissioned arrival tolerance from a fresh pre-intent/pre-dispatch readback is discarded without motion.
3. A frame with no spatially coherent source yields `G3_CLOUD_OR_TRANSPARENCY_INVALID`. Cloud, poor transparency, and a genuinely empty field cannot be distinguished safely, so the mount does not move.
4. A coherent source may be unsaturated, broad, or saturated. Each structured tier is plate-solved. A saturated structured field may proceed to the deterministic bright-target branch; it is never used for focus.
5. If WCS succeeds and the catalog target projects inside the configured usable detector margin, capture a fresh OFF/ON/OFF slit sequence.
6. If WCS succeeds but the target projects outside, compute the fresh solved-field-center to catalog-target tangent correction. Reserve both the outbound correction and an adversarial segmented return, atomically persist the precharged intent, then send the bounded absolute coordinate through N.I.N.A.
7. After `WaitForSlew`, verify reported epoch, pier side, horizon, and command residual. Wait the commissioned settle interval, repeat all immediate physical-action gates, read the mount again, and reject excess drift or arrival residual. Only then may a fresh G3 ladder be captured.
8. If every structured ladder tier fails WCS, run the full deterministic bright/sparse analysis. Only a quality-valid structured/sparse result may enter the bounded local search. Each search point repeats the same durable intent, settle, fresh-position, and fresh-frame rules.
9. If WCS centering stops, fails to improve target-center residual, or exhausts its limits, return to its saved reported origin before local search. A blocked return pauses automation.

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

The exposure-ladder payload remains schema 1 because its exposure-only semantics did not change. Schema 2 applies only to the motion ledger whose offset meaning changed to TAN geometry.

If an outbound call is rejected or the process exits after intent persistence, the conservative charge remains. Automatic recovery returns from fresh reported coordinates; it never assumes whether a prior asynchronous command executed.

## Evidence and operator diagnosis

The run evidence directory contains the original immutable FITS plus JSON records for each ladder tier, content assessment, WCS result, projected target, centering declaration, precharged motion ledger, post-slew settle/drift result, fresh validation, local-search point, return, and final summary. Content evidence includes coherent/usable source counts, median SNR, robust background/noise, dynamic range, and sampled saturation fraction. UI preview text distinguishes solved-inside, solved-outside, structured-no-WCS, and cloud/transparency-invalid states.

The durable ledger must be inspected together with the immutable run manifest. Deleting or editing it to “unstick” a run discards safety provenance and is not an authorized recovery procedure.

## Commissioning checklist

Use simulator and replayed FITS first. Then, under a separately authorized hardware session, measure and lock the exposure preset, optics/profile provenance, WCS parity, field margin, centering envelope, local-search envelope, worst-case action time, and post-slew settle time. Test wrong-way arrival error, pier/epoch change, horizon failure, cloud/featureless frames, saturated structured targets, solver failure, process exit after intent write, command rejection, and restart under the same and a different immutable context. Do not deploy the plugin while N.I.N.A. is running.
