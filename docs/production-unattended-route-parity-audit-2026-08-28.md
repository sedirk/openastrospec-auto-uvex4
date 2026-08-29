# Production unattended-route parity audit — 2026-08-28

## Scope and evidence boundary

This audit compares the production N.I.N.A. panel route in
`RealObservationStageRunner` with the deterministic one-start field harness that
completed the Mirfak, Algol, and M76 observations on 2026-08-24/25. It is a
source, manifest, and immutable-evidence review. No camera, focuser, filter
wheel, PHD2 session, mount, roof, cover, COM port, or running N.I.N.A. process
was touched while the operator continued manual observing.

The reference implementation is
`tmp/live-unattended-loop/Invoke-LiveUnattendedLoop.ps1`. The strongest complete
reference manifests are:

- `output/commissioning/2026-08-24-night/mirfak-unattended-20260824T193125Z/manifest.json`;
- `output/commissioning/2026-08-24-night/algol-unattended-20260824T193919Z/manifest.json`;
- `output/commissioning/2026-08-25-night/m76-unattended-20260825T195239Z/manifest.json`.

All three manifests record `Succeeded=true` and
`NoManualOrModelCorrectionAfterSingleStart=true`. They therefore really are
single-start backend commissioning successes, not manually corrected or
large-model-steered placement results. They were, however, launched by
`tmp/live-unattended-loop/Invoke-LiveUnattendedLoop.ps1`, not by the installed
N.I.N.A. production panel. The correct claim is “three backend core-loop
commissioning successes”; none is evidence that the production front-end had
already passed. Earlier wording that omitted this entry-point boundary was too
strong and is superseded by this audit and ADR-0009.

The production failures reviewed here are the 18 local manifests under
`%LOCALAPPDATA%\UVEX-ADV\observations\UVEX-20260827*`. Raw FITS and local
evidence remain immutable and outside Git.

## Standing policy adopted from this audit

This audit is no longer a one-off comparison. [ADR-0009](adr/0009-single-production-observation-route.md)
makes route parity a release gate:

- an independent backend/field harness success is commissioning evidence only;
- reusable behavior must be promoted into the shared production runner rather
  than copied into a panel command or another script;
- the Dockable and Advanced Sequencer entry points must capture the same locked
  configuration, build the same canonical plan, create the same real runner and
  execute through the same coordinator;
- replay must start at both front-end boundaries, and any real acquisition,
  motion, guiding or recovery change still requires an authorized, installed,
  front-end-started run through `FinalizeObservation` before production success
  is claimed.

Every future successful field harness must append or create a route-comparison
matrix equivalent to the one below, including stage ordering, configuration,
timeouts, fallback branches, recovery, cleanup and final manifest semantics.
Unpromoted differences remain explicit release blockers; they are not deferred
until the operator discovers them during an observing window.

## Route comparison

| Route segment | Validated one-start harness | Production panel after this audit | Result |
| --- | --- | --- | --- |
| Initial target motion | N.I.N.A. catalogue slew; no custom mount driver | N.I.N.A. telescope mediator catalogue slew | Aligned |
| QHY wide-field use | Fixed-axis R witness only; QHY target residual never drives the mount | Formal QHY WCS is retained as a no-motion witness and fresh G3 owns acquisition | Aligned |
| G3 exposure ladder | Direct PHD2-owned full frames, exposure ladder, every valid FITS offered to PL3 | Same ownership and ladder; local source morphology is telemetry/recovery classification and never vetoes PL3 first | Aligned/fixed |
| PL3 acceptance | Formal success accepted inside scale and optical-axis envelopes; source/match counts are telemetry | Same 0.70–1.30 scale envelope and configurable optical-axis envelope; no project minimum-star/match gate | Aligned |
| Sparse target recovery | Bounded overlapping neighbouring fields; first plausible PL3 WCS becomes the return authority | Durable bounded search; a solved neighbour is handed directly to WCS centering without blind return/rebase | Aligned/fixed |
| Large correction | Inverse G3 WCS computes the detector centre that puts the catalogue target on the slit; N.I.N.A. moves the mount | Production now solves to the fresh runtime slit point, not merely the detector centre; target-inside-but-far-from-slit also enters this loop | Aligned/fixed |
| Sparse target after return | A neighbour WCS plus measured mount response can return an invisible target to the slit without demanding a target-field solve | Production carries a bounded motion prediction through a fresh target-field LED/slit sequence and accepts it only inside the commissioned uncertainty/window | Aligned/fixed |
| PHD2 calibration order | PHD2 native selection/calibration, then all pre-calibration G3 coordinates are discarded and reacquired | Forced calibration now invalidates `lastG3Field`, stops PHD2, reruns the complete fresh G3 route, and only then permits exact lock | Functionally aligned; may repeat one coarse acquisition when an old calibration is invalid |
| Guide selection | PHD2 `find_star`; coordinator does not rank an alternative | Normal PHD2 fine-motion route uses PHD2 native full-frame selection and reuses the resulting operation-bound guiding session | Aligned |
| Fine slit placement | PHD2 exact lock to the fresh black-aperture midpoint; PHD2 left guiding | Same destination and same PHD2 ownership, with additional durable lock ledger and fresh residual qualification | Aligned/hardened |
| Slit identity | One calibrated dark-line check, commissioned midpoint retained | Two-exposure OFF/ON/OFF HDR identity and runtime midpoint, optical wheel identity checked | Intentionally stronger than harness |
| ATR exposure selection | Short-to-long ladder; low/no signal advances to the next tier; every chosen tier is remeasured; clipping backs off | Production now also advances one tier and remeasures for `TARGET_SKY_CONTRAST_LOW` and `NO_POSITIVE_SPECTRAL_SIGNAL`, but only if the current immutable probe is not clipped | Aligned/fixed |
| QHY during science | One QHY frame explicitly started around each ATR frame | One continuous QHY photometry/filter-sequence job runs in parallel and is health-checked before every ATR frame | Scientifically useful but not one-to-one paired in the manifest; see remaining work |
| Failure pause | Harness records failure and leaves the optical cover unchanged | Recoverable stage exceptions now stop PHD2/QHY and force the slit LED off without silently closing the C11 cover. Only terminal coordinator faults retain mechanical failure-close policy | Aligned/fixed |
| Normal finalization | Harness deliberately leaves PHD2 guiding for commissioning inspection | Production stops QHY and PHD2 and optionally closes the optical cover | Deliberate production cleanup difference |

## 2026-08-27 blocker audit

Every distinct blocker observed before the last manual run was mapped to an
implementation outcome rather than waiting for it to recur:

| Observed code/message | Cause | Current source outcome |
| --- | --- | --- |
| `NINA_PROFILE_OWNER_MISMATCH` | ATR/QHY selection crossed the single-owner binding | Front-end profile import and exact owner preflight identify the mismatch before physical connect; no relaxation of ownership |
| `GS350_FOCUS_METRIC_UNAVAILABLE` | QHY fixed-axis witness was incorrectly required to supply a stellar FWHM | A formal immutable QHY WCS can be accepted as a no-motion witness while GS350 focus remains at its locked position |
| non-finite JSON `STAGE_EXCEPTION` | Diagnostic metrics contained `NaN`/infinity | Diagnostic evidence serializes non-finite doubles as JSON `null`; canonical motion ledgers remain strict |
| repeated `G3_MOTION_RECOVERY_CONTEXT_CHANGED` / elapsed lineage | An old durable action context was intentionally changed by a new target/form | Audited **清除旧 G3 状态** remains available without starting hardware. **按当前设置新开一轮** now performs that explicit retirement first and only then captures a new action hash/run from the current form. It never mutates the old hash or hot-swaps a running route. |
| 2.24-arcsec G3 family handoff failure | Normal settled readback variation was treated as an attempted rebase | Same-origin charged handoff accepts only bounded current-position continuity and preserves origin, cumulative motion, attempts, and original clock |
| PHD2 two-frame 10.04-second timeout | Millisecond exposure was confused with multi-second full-frame delivery and stale-frame discard | Per-required-event full-frame timeout floor is 10 seconds; two events receive two event budgets and still return immediately when frames arrive |
| target formally solved but driven only toward frame centre | Production WCS goal differed from the successful slit-centred inverse solve | Production inverse WCS now computes a detector-fixed slit destination |
| target field fails PL3 after a solved-neighbour return | Production demanded a solve in the field specifically chosen because it may be sparse | Bounded neighbour WCS + charged N.I.N.A. response + fresh runtime slit can attest final target placement without a target-field WCS |
| forced recalibration followed by stale placement coordinates | Production could continue with geometry measured before calibration pulses | One mandatory post-calibration G3 reacquisition cycle precedes exact lock |
| faint ATR probe paused immediately | Production treated underexposure as terminal | Safe one-tier advance/reprobe now matches the successful harness |
| next retry unexpectedly dark | recoverable `STAGE_EXCEPTION` cleanup closed the C11 optical cover | Recoverable pause is stop-only for data owners/LED; terminal fault policy remains fail-safe |

## Remaining differences and next validation

The following are not hidden preemptive blockers, but should remain explicit:

1. Production uses a stronger HDR slit-identity sequence and durable
   motion/lock ledgers. These checks protect physical identity and crash
   recovery and are not removed to make the route look shorter.
2. If active PHD2 calibration is invalid, production may perform an initial G3
   coarse acquisition, recalibrate, and then repeat G3 acquisition. The ordering
   is now correct and bounded, but a future optimization may qualify/recalibrate
   PHD2 before the first large G3 correction to avoid duplicate work.
3. Continuous QHY photometry is concurrent with ATR science, but production does
   not yet emit the harness's exact `ATR frame i <-> QHY frame i` pair table.
   Add midpoint/mount-span correlation to the final production manifest without
   changing either camera owner.
4. A featureless G3 ladder never authorizes blind neighbour motion from image
   heuristics alone. Non-stellar/faint plans can use catalogue-WCS authority
   without requiring a visible target peak. A direct-stellar plan may now enter
   bounded overlapping-neighbour recovery only when the full-unattended chain
   freshly attests its Safety Monitor, weather, exact open RRCI roof and exact
   open optical cover; weak supervision with a missing capability still pauses.
5. The current changes require simulator/replay and full build verification,
   then one separately authorized on-sky production-panel run through
   `FinalizeObservation`. This audit itself did not deploy or touch the ongoing
   manual observation.

## 2026-08-28 daylight component follow-up

After the roof and optical covers were closed and the operator explicitly
authorized no-motion hardware testing, the formal PHD2/G3 and UVEX LED owners
completed four bounded 18-frame slit sequences. The updated SDK removed the
full-width white readout band; gain 0 preserved dynamic range. Increasing the
long exposure from 20 to 200 ms did not independently resolve both edges of the
15 µm aperture. The production analyzer now has an explicit narrow-slot-only
fresh-edge/commissioned-midpoint result and preserves the star-validated
along-slit central anchor. The final production-setting 10/20 ms component run
passed at `(817.392, 424.536)`, with final LED OFF attested and manifest SHA-256
`DFA99CDAF113DB2D49E64F56A49D5BE1123A075D652555D1EC18216FD03D1103`.

This closes the camera transport and slit-component replay portions of
OBS-018. It does **not** close ADR-0009's installed front-end, on-sky
`FinalizeObservation` acceptance gate.

## 2026-08-28 pre-night production-front-end blocker audit

This second pass started at the two operator-visible production boundaries,
not at a helper or field harness. `ObservationDockable.StartRealAsync` and the
Advanced Sequencer `UvexTargetObservationContainer` both capture one immutable
`RealRunConfiguration`, build the canonical `ObservationPlan`, create their
runner through the same `RealObservationStageRunnerFactory`, and execute it via
`ObservationCoordinatorHost.RunAsync`. A source-level release regression now
protects those calls, their order, and the exact eleven canonical stages. The
front end does not contain a second acquisition, motion, guiding, exposure or
cleanup state machine.

Two false production blockers were found and removed before the next sky run:

1. N.I.N.A.'s ImageSolver can reflect a role-specific G3 downsample value back
   into its live `IPlateSolveSettings` object. The runner never consumes that
   mutable value after lock, but the old whole-profile hash treated its own
   solve as an operator edit and raised `REAL_PROFILE_DRIFT`. Drift recapture
   now normalizes that runtime reflection to the already locked plate-solver
   value. Deliberate action-bearing changes remain next-run changes. A real
   drift message now also names up to eight exact changed JSON fields instead
   of showing two opaque hashes.
2. Selecting **有人弱监督** already records an explicit supervised run. The
   PHD2 path nevertheless required a second hidden
   `AllowDegradedSupervisedScience` checkbox before it would use an otherwise
   usable graded calibration. Weak supervision is now the effective supervised
   science opt-in; the older advanced checkbox remains a compatible explicit
   opt-in. This does not create unattended authority, bypass PHD2's hard
   calibration ceilings, or permit a lock shift without fresh settle/residual
   evidence.

The resulting stage/gate inventory is:

| Production stage | Automatic route and recovery | Deliberate stop conditions that remain |
| --- | --- | --- |
| Validate and lock Night Setup | Auto-loads the selected hash-valid preset/Night Setup, connects only the declared owners, starts ATR pre-cooling in parallel, checks UVEX state and imports the current environment. Missing safety/roof/weather/cover adapters and high humidity are warnings in weak supervision. | Owner/device mismatch, changed immutable evidence, explicit unsafe/rain/cloud/wind, an explicitly closed/error cover or roof, invalid horizon/clock, or unhealthy QHY/COM5/PHD2 identity. These are physical/provenance uncertainties, not image-quality preferences. |
| N.I.N.A. catalogue slew | Unparks after current gates pass, sends one tracking-enable request and waits up to 15 s for driver readback, then uses N.I.N.A.'s native telescope mediator. | Mount refusal/disconnect/limit, tracking still false after the bounded wait, or target/command coordinate below the protected horizon. |
| QHY wide-field witness | Dedicated QHY owner captures through its configured exposure ladder; each immutable accepted FITS is offered to N.I.N.A./PL3. A locked-no-move GS350 does not require a fabricated FWHM or AAF motion. | Camera/transport loss, no quality-retained frame, or no formal wide-field PL3 result after the exposure ladder. QHY never moves the mount in the production route. |
| Coarse handoff | Retains QHY WCS as a no-motion witness and immediately hands acquisition to fresh G3. | Missing or provenance-mismatched accepted QHY/WCS evidence. Legacy QHY-motion limits are not selected by the production route. |
| G3 acquisition and large centring | Every valid PHD2-owned FITS goes to PL3 first on the 2/5/10/15 s ladder. A plausible target-inside solve is carried through the no-motion LED sequence; a target-outside solve uses inverse WCS directly toward the runtime slit; a structured unsolved field enters bounded overlapping-neighbour search. Faint/non-stellar plans may use catalogue-WCS projection without a visible target peak. A featureless direct-stellar field may search only under fresh full-unattended Safety/Weather/open-RRCI/open-cover authority. | Wrong PHD2 owner/profile, unresolved LED-OFF state, invalid physical PL3 scale/parity/optical-axis geometry, optical slit identity mismatch, exhausted charged motion/return ledger, or a featureless **direct-stellar** field without that independent environmental authority. There is no project-authored minimum-star or minimum-match count. Local stellar-core morphology is diagnostic and cannot veto a plausible formal PL3 result. |
| Fine placement and guiding | Uses PHD2 native star selection/calibration and exact lock toward the fresh black-aperture midpoint. One invalid active calibration may be rebuilt; all pre-calibration geometry is then discarded and the complete fresh G3 route runs once more. Weak supervision authorizes usable supervised calibration grades. | PHD2 identity/epoch change, hard calibration ceiling, failed settle/fresh residual, ambiguous crash ledger, or inability to prove the same slit/target geometry. No coordinator-written replacement star is silently substituted. |
| Parallel QHY photometry | Starts the dedicated QHY sequence only after the settled guide epoch and checks its health before each ATR exposure. | Lost/failed/taken-over QHY job or lost guide authority. |
| ATR exposure selection and science | Cooling has run in parallel and is first awaited here. Short-to-long probes are measured, not inferred; low but unclipped signal advances one tier automatically, clipping backs off and remeasures, and rejected FITS remains immutable evidence. | Cooling never reaches tolerance, no freshly measured safe exposure tier, guide loss, QHY health loss, explicit environment/cover failure, or exhausted bounded science attempts. A fixed 10 s science exposure is not used. |
| Finalization | Checked QHY cancellation/terminal state, checked PHD2 stop, LED OFF cleanup and evidence counters. In full unattended mode the shared runner then closes the selected main-optical-path cover, parks the mount and closes the exact locked N.I.N.A./RRCI roof; weak supervision never commands the roof. | A data owner cannot be proven stopped, a requested cover transition cannot be attested, the mount cannot be proven parked, RRCI rejects the command, or roof closure cannot be attested; this is reported as incomplete cleanup rather than a false successful finish. |

Daylight hardware replay was repeated after this audit. PHD2 Profile 2 was
first reconnected through its own JSON-RPC, with exact live
`G3M2210M`/`On-Step (ASCOM)` identity and final `Stopped` state; no guide or
mount-motion command was sent. The complete 10/20 ms, native-gain-0,
`OFF x3 / ON x3 / OFF x3` HDR sequence then acquired 18/18 immutable frames,
passed reflective registration and dark-aperture transfer, measured midpoint
`(817.381, 424.227)` and width `3.500 +/- 0.500 px`, and independently attested
final LED OFF. Manifest:

`output/commissioning/2026-08-28-day/evidence/g3-slit-led-native-hdr/g3-led-20260827T231110481Z-65eef9e0d0234be09bcfdec5773c7d01/no-motion-led-manifest.json`

SHA-256:
`744F15A6CF17AA51EAA87054EFCC1DCAD9B4B52DE1C2532A59B4FF4D2B64C65A`.
The covered daytime field naturally reported broad/unusable stellar focus; the
component harness records that diagnostic but does not confuse it with the
successfully measured slit. In production, a same-detector formal PL3 result
and stable Night-Setup-bound focus explicitly prevent the later short LED frame
star heuristic from vetoing the accepted WCS.

This audit can eliminate known deterministic false blockers; it cannot promise
that real rain, a disconnected owner, a mount limit, an unsolved field after
all bounded fallbacks, lost guiding, clipping, or a failed terminal cleanup will
be ignored. Those stops are the remaining intentional physical/scientific
uncertainties. The new source and component replay still require installation
and one front-end-started sky run through `FinalizeObservation` before the
production acceptance item is closed.

## 2026-08-28 environment-supervision promotion

ADR-0010 promotes the N.I.N.A. environment chain into that same production
runner; no Dockable-only or field-harness roof state machine was added. The
immutable action configuration now includes the exact Safety Monitor, Weather,
Dome/Roof and Flat Device selections plus the open/close policy and transition
budgets. Both production entry points therefore capture and drift-check the
same environment authority.

The full-unattended branch auto-connects the four selected adapters without
motion, requires all four to be connected and identity-matched, parks before a
bounded roof-open command, refuses to reopen a roof that closes later in the
same run, and performs cover -> park -> roof terminal cleanup. A live Safety
Monitor unsafe event requests ATR exposure abort and starts the same serialized
cleanup path. The weak-supervision branch degrades each absent, disconnected or
metric-incomplete adapter to a warning, never commands the roof, and still
stops on explicit danger reported by an adapter that is present. Humidity by
itself is advisory at this site.

Read-only station discovery found the intended selections
`AIWeatherSafetyMonitor`, `RRCIAdvanced.Dome`, `NINA.OpenMeteo.Client` and
`ASCOM.GeminiAutoCover.CoverCalibrator`. RRCI Replica status alone does not
grant motion: its Primary must explicitly allow replica commands and separately
allow replica Open, while retaining fresh status and required-node parked
heartbeats. The runner reports a rejected command rather than bypassing that
policy. No roof or cover command was issued during this source audit. A bounded
live RRCI/AIWeather failure-injection cycle and a front-end run through
`FinalizeObservation` remain the field acceptance boundary.

## 2026-08-28 Occam gate-severity pass

The production coordinator now carries an explicit severity independently of
its advance decision. A warning is `Passed + Warning`: it is shown as
`警告/继续`, written to the timeline/manifest and included in the completion
summary, but it does not manufacture a Resume step. `Failed` and
`Indeterminate` are both errors and pause because the next action is known to
be invalid or lacks evidence that cannot safely be guessed. The complete
classification is [Gate severity and observation-continuity policy](design/gate-severity-and-continuity-policy.md).

This pass removed the following production false blockers:

1. QHY/GS350 is now an optional parallel branch after the immutable run is
   locked. Service proof, connection, exact optional-device match, acquisition,
   WCS/focus, photometry health and photometry cleanup failures disable that
   branch and leave ATR/G3/UVEX spectroscopy running. `PauseOnQualityFailure`
   is false for the parallel photometry job. Lost guide authority still blocks
   both branches before a new ATR exposure.
2. Low ATR contrast or single-frame SNR is not conflated with clipping. The
   ladder still climbs and remeasures; at the longest safe unclipped tier the
   frame is retained for stacking and the run completes with a warning.
   Clipping automatically backs off and reprobes inside the same unattended
   stage. It pauses only after the bounded safe-tier/attempt policy is actually
   exhausted.
3. The project-authored local G3 stellar-core heuristic no longer vetoes a
   formal target-inside PL3 solution and no longer prevents configured bounded
   neighbouring-field recovery. Weak supervision may run the same charged,
   horizon-limited overlapping search; missing environmental adapters remain
   visible warnings while connected explicit danger remains a stop.
4. Missing weak-supervision environment capabilities, high humidity, optional
   cover uncertainty and unavailable pre-capture ATR ROI/readout telemetry now
   use the warning severity rather than green `Passed` or a hidden pause.
5. Finalization separates required cleanup errors from optional QHY cleanup
   warnings. A failed optional photometry stop cannot turn an otherwise
   complete spectrum into a false failed observation; failure to stop PHD2 or
   to attest a configured cover/mount/roof terminal action still does.

The deliberate hard stops are consequently smaller and easier to explain:
exact owner/identity conflicts, changed motion-authorizing evidence, explicit
unsafe/rain/cloud/wind/closed-roof/closed-cover reports, horizon or movement
limit violations, exhausted bounded acquisition, loss of physical slit or
guide authority, no safe unclipped ATR tier, and unattested required terminal
cleanup. No source inspection or test in this pass connected or moved real
hardware.
