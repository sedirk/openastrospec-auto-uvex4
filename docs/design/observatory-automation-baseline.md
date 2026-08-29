# UVEX-ADV observatory acquisition automation baseline

**Status:** FROZEN / accepted design target  
**Decision date:** 2026-08-16  
**Scope:** acquisition and equipment-side software; the independent reduction pipeline is a downstream consumer  
**Authority:** the observatory owner’s stated observing workflow and decisions

This document is the canonical design target for turning the existing UVEX workflow into a N.I.N.A. Advanced Sequencer-style pipeline. It records intended behavior, including functionality that does not exist yet. README files and implementation comments may summarize it but must not contradict it.

The file is hash-protected. Ordinary implementation work must not modify it. A deliberate change requires the owner’s explicit request, a superseding ADR, a review of safety and data consequences, and an intentional hash-manifest update.

## 1. Mission

Automate target acquisition, slit placement, guiding, spectral exposure selection, simultaneous guide-scope photometry, calibration acquisition, and recovery while preserving four properties:

1. one long-lived owner per physical device;
2. bounded and inspectable equipment actions;
3. immediate visual evidence plus operator pause, resume, cancellation and intervention;
4. reproducible raw data, metadata, and decisions suitable for professional-style reduction.

Automation must reduce repetitive manual work without hiding uncertainty. A failed solve, uncertain target identity, low-confidence slit placement, lost guide star, or incompatible calibration must stop or degrade explicitly rather than invent success.

## 2. Fixed observatory context

### Optical and mechanical equipment

- Main telescope: Celestron C11 with CCDT67 reducer. The reducer was not necessarily operated at its nominal reduction factor; the true effective focal length remains a measured configuration value and must not be hard-coded from the product name.
- Spectrograph: motorized UVEX 4i, single long slit / Czerny-Turner architecture, connected on COM5.
- Slit wheel positions: position 1 = 300 µm, position 2 = 15 µm, position 3 = 25 µm, position 4 = 35 µm.
- Slit, grating angle, telescope focus, and UVEX internal M2 focus may differ between nights. In normal observing, one optical configuration is usually chosen and retained for the night.
- The current calibration module is incomplete. A bright calibration star or a compact emission-line object such as a planetary nebula supplies wavelength references. The LED panel supplies a candidate flat but cannot by itself establish spectrophotometric response.
- No long-wavelength order-sorting filter is installed. Second-order blue contamination becomes a growing risk somewhere in the red, approximately near but not proven to begin at 6800 Å. Software must retain the data, report a wavelength-dependent risk/quality flag, and never claim the affected region is rigorous without a measured test.

### Cameras and fields

- ATR585M is the spectral camera. Its commissioned normal setting is gain 100, offset 256, 1×1, High Conversion Gain, and −10 °C unless an explicitly versioned equipment preset says otherwise.
- G3M2210M images the slit field and supplies the guide stream.
- A roughly coaxial GS350 guide scope (350 mm, f/6) carries the photometric QHYminiCam8M. It is used first for a wide-field plate-solve witness, then remains active for simultaneous time-series photometry during spectral acquisition. In the production route validated on sky, QHY WCS does not directly authorize arbitrary mount motion: fresh G3 WCS owns large acquisition corrections and post-move verification. The explicit exception for a mount without a mechanical home or absolute encoders is one evidence-gated coordinate `Sync` from fresh QHY/PL3 WCS, followed by one reissued planned catalogue slew and fresh G3 verification as specified below.
- The GS350, main telescope, and slit camera are not assumed to be perfectly rigid or exactly coaligned. Their transformations are calibrated measurements that can depend on pier side, orientation, temperature, and flexure.

### Local horizon

The observatory walls obscure the sky to roughly 40° altitude around the platform. Until an azimuth-dependent horizon profile is measured, 40° is the conservative obstruction limit. The scheduler must use configurable start/continue margins above that limit and prove the target remains observable through acquisition, exposure, calibration, recovery, and any meridian flip. Clear weather or a visible Milky Way does not override the horizon or safety conditions.

## 3. Non-negotiable device ownership

| Physical device | Sole owner | Responsibilities |
|---|---|---|
| UVEX4 / COM5 | `UvexAdv.Service` | Protocol, state, slit, grating, M2, leases, limits, audit and emergency stop |
| ATR585M | N.I.N.A. | Probe and science spectrum exposures, raw FITS and camera state |
| G3M2210M | PHD2 | Slit-field frames, guide-star tracking and mount guide corrections |
| QHYminiCam8M | planned QHY acquisition/photometry service | Wide-field acquisition, plate-solve frames and calibrated time-series photometry |
| Telescope/mount | N.I.N.A. telescope mediator, coordinated by the plugin | Slews, bounded acquisition corrections and sequence state |

The invariant is one owner **per physical device**, not one process for all cameras. The three cameras are intended to run concurrently. In particular:

- PHD2 must not switch between G3M2210M and QHYminiCam8M for this workflow.
- The UVEX plugin must not load ToupTek or QHY SDKs directly into N.I.N.A.
- The QHY service must not open G3M2210M or ATR585M.
- Windows QHY drivers and SDKs are installed as one complete official QHYCCD
  AllInOne distribution. The QHY service loads the hash-bound x64 SDK directly
  from that vendor installation; it must not maintain a second private SDK copy
  under the UVEX-ADV service directory.
- No helper may scan camera or serial-device lists and select by ordinal position. Persist and validate stable identity.
- A device hand-off, if ever required for diagnostics, must be explicit, logged, disconnected, confirmed, and outside an active science run. It is not part of normal acquisition.

## 4. Night setup contract

The first implementation does not attempt to automate the difficult physical choice of slit, wavelength region, grating setting, or initial optical focus. The observer may perform those tasks manually with daylight, a bright star, or another suitable source.

Before the first target, the software creates and locks a **Night Setup** record containing at least:

- expected slit position and width;
- grating position/nominal wavelength and direction convention;
- M2 position and confidence/verification time;
- telescope focuser position if available;
- ATR585M identity, gain, offset, temperature, binning, ROI and readout mode;
- G3 and QHY stable identities and acquisition settings;
- dispersion orientation and expected wavelength range;
- selected calibration strategy and reference object;
- local horizon policy and safety-monitor state;
- UTC timestamp and a unique setup identifier.

The sequence validates actual device state against this record. It must not silently change the optical configuration between a standard and science target. An intentional change creates a new setup identifier and a separate calibration context.

Bias and dark masters may be reused across nights when camera identity, gain, offset, readout mode, binning, ROI, temperature tolerance, exposure policy, and provenance are compatible. “Cooled camera” permits cross-night reuse; it does not permit incompatible calibration frames.

## 5. Canonical per-target workflow

### 5.1 Wide-field acquisition with GS350/QHY

1. The QHY service remains the camera owner from acquisition through the end of simultaneous photometry.
2. It captures a raw, full-field solve frame and supplies it to the configured plate solver.
3. The coordinator retains the formal QHY WCS, target residual, immutable frame identity, mount readback and solver evidence as a wide-field witness. For a mount without a mechanical home or absolute encoders, a fresh, stationary, hash-bound QHY/PL3 WCS is also the absolute sky-coordinate authority: when its separation from the mount-reported coordinate exceeds the configured G3 sky-hint trust radius, the coordinator may issue one N.I.N.A.-mediated coordinate `Sync` per run and must verify the new readback. Sync itself does not slew the mount. After successful verification, the planned catalogue slew is reissued once and optical arrival still requires fresh G3 WCS. Smaller differences do not Sync, so ordinary QHY/G3 optical-axis separation cannot cause coordinate ownership to oscillate.
4. A failed QHY solve advances the commissioned QHY exposure ladder. It does not invent a minimum-star-count veto after the configured solver has returned a physically plausible formal solution.
5. The coordinator then hands acquisition to a fresh G3 frame. An independently activated, versioned QHY-to-G3 transfer may remain an optional optimization under ADR-0004, but it is not the production default and never replaces the fresh G3 verification.
6. QHY frames taken for acquisition are tagged `ACQUISITION` and are never mixed into scientific photometry.

Random unbounded mount nudging is not an automated strategy.

### 5.2 Slit-field acquisition with G3/PHD2

1. PHD2 remains the only G3 owner.
2. A full slit-field frame is retained and solved. At an unchanged pointing, the most recent fresh QHY/PL3 WCS is the preferred G3 solver hint and may first repair a grossly incorrect mount coordinate under the one-Sync rule above. A formal, physically plausible fresh G3 WCS remains the authority for a large acquisition correction through the N.I.N.A. telescope mediator; the commanded endpoint is not optical success evidence.
3. After every large correction, the coordinator waits for stable mount readback and captures another immutable G3 frame. Only the fresh post-move G3 WCS may prove response and arrival. If direct G3 solving fails, the coordinator uses a bounded overlapping neighbouring-field search; an applicable QHY-to-G3 transfer remains optional. It must retain every attempt and confidence score.
4. The expected target is identified using catalogue coordinates, WCS, temporal continuity, and stellar morphology. Brightness alone is insufficient.
5. The target centroid is moved to the calibrated slit locus using a selected,
   versioned fine-motion authority. The preferred authority is a current,
   quality-classified PHD2 calibration followed by bounded runtime lock-position
   shifts; an independently commissioned four-direction G3 pixel-to-mount model
   remains a fallback. Plate-solve rotation alone is not motion authority.
6. Every fine-motion stage requires operation-bound settle evidence and a fresh
   immutable G3 residual before another stage or science exposure. A lock-position
   readback alone does not prove that the star reached the slit.
7. The software continuously displays the target, slit, predicted location, centroid, residual and confidence. A passing quality gate advances automatically; a failed or indeterminate gate enters `PausedNeedsAttention` without starting another motion or exposure.
8. A successful acquisition saves an immutable evidence frame with WCS, overlay coordinates, target/slit residual, setup ID and timestamp.

### 5.3 Guiding

1. Exclude the slit and a configurable guard region from ordinary guide-star candidates.
2. Reject saturated, blended, edge-adjacent, elongated, unstable, or low-SNR stars.
3. Prefer a suitable independent off-slit star. When no independent star exists
   around a uniquely identified ultra-bright target, an explicitly configured
   degraded mode may guide directly on that target with the shortest commissioned
   stable-centroid exposure; the frame is not accepted as focus evidence.
4. Retain and rank PHD2 calibration candidates with a versioned multi-metric
   policy. Orthogonality error is one quality term, not a universal binary
   threshold. Exact profile/equipment/topology, axis rates/parity, pier side,
   age, settle and fresh post-movement residuals remain mandatory.
5. When the selected fine-motion authority is PHD2 calibration, start and settle
   the guide epoch before final placement, then use bounded exact runtime-lock
   stages to bring the measured target to the measured slit. Re-measure after
   every stage; never infer arrival from lock readback alone.
6. Do not begin a science spectral exposure while guiding is unconfirmed or unstable, or while the fresh target/slit residual is outside tolerance.
7. PHD2 remains connected to G3 for the entire science block. It never takes ownership of QHY.

### 5.4 Simultaneous QHY photometry

After slit placement and guide settling, the QHY service changes from acquisition mode to photometry mode without disconnecting the camera. It records raw time-series images with fixed settings, comparison-star information, WCS, FWHM, ellipticity, background, saturation, transparency and quality flags.

QHY photometry and ATR spectral exposures share an observation run identifier and UTC timing. Each spectrum can therefore be associated with overlapping photometric samples and transparency measurements. Frames affected by slewing, guiding loss, clouds, saturation, or reacquisition remain preserved but are flagged.

### 5.5 ATR585M spectral exposure selection and science block

1. Use fixed commissioned gain, offset, readout and cooling settings from the Night Setup.
2. Select exposure from a discrete, configurable ladder using the extracted spectral ROI—not the whole-frame histogram.
3. Evaluate bias-subtracted high percentiles, saturated-pixel fraction, line and continuum SNR per resolution element, sky/target contrast, and clipping risk.
4. Retain the probe frame and the metrics/reason for the chosen tier.
5. During the science block, monitor PHD2, QHY transparency, camera state, UVEX state and the target-on-slit proxy.

## 6. Calibration workflow

- A wavelength reference must share the science setup: slit, grating, M2, camera, binning, ROI and dispersion direction unless a measured transfer model explicitly covers the difference.
- A compact emission-line source is normally a stronger wavelength anchor than broad stellar absorption, but a known bright star may provide a lower-precision model when the quality gate reports the limitation.
- Whenever practical, bracket a science block with wavelength references or use suitable night-sky lines to measure target-specific zero-point drift.
- LED exposures can address pixel response, dust and slit-illumination structure only when their quality and geometry gates pass. They do not create an atmospheric/instrument response curve by themselves.
- Relative response requires a suitable spectrophotometric standard and must preserve slit-loss and atmospheric limitations. Absolute spectrophotometry is outside the first acquisition milestone.
- The uncertain red second-order region is retained and flagged; it is not silently cropped or corrected without measured evidence.

## 7. Required sequence model

The N.I.N.A. plugin is the orchestration layer. The planned top-level `UVEX Target Observation` container composes reusable items rather than putting all logic in one command:

1. Validate and lock Night Setup.
2. Start/verify QHY service, acquire the wide field and retain its formal WCS as a sky-coordinate witness. On a commissioned no-mechanical-home mount, use that fresh stationary WCS for at most one gross-coordinate Sync with readback verification; otherwise issue no QHY-derived mount command.
3. Acquire/solve a fresh G3 slit field through PHD2; when the target is outside the field, issue a bounded N.I.N.A. WCS correction and prove its response with another fresh G3 solve. If no direct solve is available, use the bounded overlapping neighbouring-field search.
4. Confirm that the catalogue target is inside the usable G3 field from fresh evidence.
5. Select the versioned fine-motion authority. With PHD2 calibration authority,
   select the guide source and establish a current settle epoch; with an
   independent transform, retain the original acquisition-first order.
6. Place the target on the slit through bounded stages, automatically evaluating
   target identity, centroid, guide lock, residual and confidence after each
   fresh G3 frame. Ensure PHD2 is settled before progression.
7. Start QHY photometry job.
8. Capture ATR probe spectra and select an exposure tier.
9. Run the ATR science block while QHY photometry and health monitoring continue asynchronously.
10. Stop/finalize QHY photometry, close the selected main-optical-path cover,
    park the mount and close the selected N.I.N.A./RRCI roll-off roof when the
    run is in the commissioned full-unattended mode; emit an observation
    summary containing every checked terminal state. Weak supervision leaves
    roof motion to the operator.

Conditions and triggers include UVEX readiness, device identity, camera temperature, Night Setup match, local horizon, safety state, plate-solve confidence, target/slit residual, PHD2 settle, guide loss, transparency change, spectral saturation/SNR, target drift, meridian flip and cancellation.

Normal successful execution does not contain per-stage operator confirmation prompts. The dockable workflow panel exposes `Pause`, `Resume`, `Cancel` and `Take over` throughout the run. Every transition rechecks any gate that may have become stale. A failed or indeterminate gate enters `PausedNeedsAttention`, preserves its evidence and reason, and waits for recovery or operator intervention.

The QHY job must be startable and stoppable by sequence items while continuing in its isolated service process; it must not rely on replacing N.I.N.A.’s ATR camera selection.

## 8. Failure and recovery rules

- A cancellation request prevents new motion/exposure promptly and leaves all services in an inspectable state.
- A solve or identification failure invokes only a bounded recovery plan, then enters `PausedNeedsAttention` with the retained evidence and failure reason.
- Guide loss prevents new ATR exposures. The current exposure is retained and flagged or aborted only according to an explicit threshold policy.
- After significant guide loss or mount movement, revalidate target-on-slit before resuming.
- A QHY failure does not crash N.I.N.A., PHD2, or the COM5 service. The sequence decides whether spectroscopy may continue without simultaneous photometry and records the downgrade.
- A PHD2/G3 failure cannot be bypassed silently for a slit spectrum.
- A UVEX service disconnect or setup mismatch blocks UVEX motion and new science frames requiring the disputed state.
- Service restart, N.I.N.A. restart, lease expiry, network timeout, or plugin crash must not leave an autonomous movement loop running.
- No startup or reconnect path automatically homes or moves a UVEX axis.
- Emergency stop remains available independently of ordinary control leases.

## 9. Safety and horizon policy

- The sequence must evaluate a configurable azimuth/altitude obstruction model. Until it exists, use a conservative 40° minimum altitude plus explicit start/continue margins.
- It must predict target altitude through the whole requested block rather than check only at start.
- A clear sky, camera image, or CCTV image is observational context, not a substitute for a connected safety monitor or explicit roof state.
- Under ADR-0010, commissioned full-unattended execution may open and close the
  roll-off roof only through the exact hash-locked N.I.N.A. Safety Monitor,
  Weather, Dome and Cover selections. Opening requires fresh safe/weather,
  horizon, identity and parked-mount evidence; normal completion or a terminal
  safety failure closes the cover, parks the mount and then closes the roof,
  with every transition bounded and attested. An unsafe event during a run
  starts the same fail-safe closure and permanently invalidates automatic
  resume for that run.
- Weak supervision is capability-by-capability: an unselected, disconnected or
  metric-incomplete Safety Monitor, Weather, Dome or Cover produces a warning
  and degrades only that capability. It never grants unattended authority and
  never auto-opens/closes the roof. A connected adapter's explicit unsafe,
  rain, cloud/wind limit or closed/error roof remains a hard stop. A selected
  command-capable cover may still be opened on demand and closed at cleanup,
  but every transition must be attested; a missing cover is warning-only while
  a failed/error cover remains a hard stop. High humidity alone is advisory at
  this near-coastal site.
- Mount corrections, search patterns and slit-placement motions have configurable per-step, cumulative and time limits.
- Meridian-flip recovery repeats the required acquisition and slit validation rather than assuming the old transformation remains valid.

## 10. Data provenance and target identity

Every run must retain enough evidence to answer “what object was actually observed?” and “why did the software take this action?” At minimum preserve:

- requested target name, catalogue identifier and coordinates;
- solve frames, WCS solution, residuals and solver identity/version;
- QHY-to-G3 transformation version, pier side and confidence;
- target/slit overlay and residual at acquisition and after recovery;
- selected guide star and PHD2 settle/quality summary;
- stable device IDs and complete camera settings;
- slit, grating and M2 positions with units and setup ID;
- exposure-tier metrics and decision;
- UTC start, midpoint and end, plus a common observation run ID;
- QHY photometry links and quality flags;
- all warnings, downgrades, retries, pauses, resumptions, cancellations and operator interventions;
- checksums of raw inputs and generated manifests.

Raw acquisition files are immutable. Repairs or calibration produce new files with provenance; they never overwrite the source.

## 11. Staged implementation and commissioning

### Phase 0 — repository and design baseline

- Freeze this document and ADR-0001.
- Establish source/output separation, Git ignores, verification hooks and a reproducible baseline commit.
- Do not operate hardware during this phase.

### Phase 1 — QHY service in simulation/shadow mode

- Implement identity-bound QHY configuration, acquisition/photometry job state, FITS metadata and a simulator/recorded-frame adapter.
- Integrate an offline plate-solver contract and photometry quality metrics without mount commands.

### Phase 2 — wide-field acquisition

- Read real QHY frames under explicit authorization, solve without motion, then commission bounded mount corrections.
- Measure plate scale, rotation, GS350 boresight, pier-side behavior and repeatability.

### Phase 3 — G3/PHD2 acquisition and guiding

- Integrate the PHD2 service API while PHD2 remains sole G3 owner.
- Save full acquisition evidence, calibrate slit loci and implement bounded fine acquisition.

### Phase 4 — automatically gated slit placement

- Overlay target/slit geometry continuously and automatically evaluate identity, residual and confidence gates.
- Commission automatic centering first on bright stars and compact emission-line sources, then on fainter targets.

### Phase 5 — concurrent observation

- Run G3 guiding, ATR spectroscopy and QHY photometry concurrently with common timing and recovery.
- Add exposure-tier selection, calibration blocks, health triggers and complete run manifests.

### Phase 6 — qualified unattended resilience

- Demonstrate repeatable automatic gates across representative targets, pier sides and configurations.
- Retain persistent pause/resume/cancel/take-over controls and a simulator/shadow fallback.

## 12. Acceptance criteria for the eventual system

- No test or real run opens the same physical camera from two processes.
- PHD2 never becomes the QHY acquisition/photometry owner.
- Coarse acquisition has bounded retries and returns or stops predictably.
- Target/slit evidence is produced before every accepted science block and after every significant recovery/flip.
- Bright-star and planetary-nebula commissioning runs achieve repeatable automatic slit placement within the measured tolerance.
- A graded, current PHD2 calibration and operation-bound guide settle gate every
  new PHD2 lock-shift stage and spectral block; a degraded calibration is never
  represented as an excellent one.
- ATR, G3 and QHY operate concurrently for a multi-hour run without ownership conflicts or hidden disconnects.
- Every ATR spectrum maps unambiguously to QHY photometric samples and guide-quality intervals.
- Exposure selection avoids saturation and documents its metric-based decision.
- Calibration compatibility and degraded modes are explicit in both UI and metadata.
- Horizon, cancellation, crash, timeout, lease-expiry and guide-loss tests stop unsafe continuation.
- Pause prevents new motions and exposures, resume revalidates stale gates, and every automatic-gate failure enters an inspectable `PausedNeedsAttention` state.
- Simulator and recorded-data tests cover the full state machine before corresponding real-hardware actions are enabled.

## 13. Measurements still required

The design intentionally does not invent these values:

- true C11 + CCDT67 effective focal length and plate scale;
- QHYminiCam8M stable device identifier, driver path, gain/offset/readout/cooling presets and photometric cadence;
- GS350-to-main/slit-field boresight transform, repeatability, pier-side dependence and flexure;
- G3 full-frame acquisition route through PHD2, plate scale, orientation and usable solve exposure;
- pixel locus and width of each physical slit in every supported G3 ROI/binning mode;
- mount small-correction scale, backlash, settle time and safe search radius;
- versioned PHD2 calibration-grade boundaries, exact-lock stage limits, guide/lock
  and target/slit residual limits, and representative performance by pier side;
- target-on-slit tolerance and throughput-versus-offset curves for 15, 25 and 35 µm slits;
- azimuth-dependent wall/horizon profile and operational altitude margins;
- nightly calibration catalogue and same-setup transfer tolerances;
- measured onset/strength of second-order contamination;
- photometric comparison-star and transparency quality thresholds.

These are commissioning outputs stored as versioned presets with provenance, not constants inferred from memory.
