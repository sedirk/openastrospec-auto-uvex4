# Faint-target, sparse-field and adaptive-spectrum acquisition design

Status: implemented in stages and partly validated on sky. The 2026-08-25/26
M76 run validated one-node neighbouring-field recovery, formal PlateSolve3
trust, catalogue-WCS placement for a non-stellar target, off-slit PHD2 guiding
and adaptive 600 s ATR acquisition. A connected multi-node overlap graph is
still future work.

This document extends the ordinary-star route without changing the device
ownership or raw-data invariants in the frozen observatory baseline. It is
specifically intended for targets such as 3C 273, faint central stars of
planetary nebulae, compact nebulae and fields where a single G3 exposure may
contain only a few usable catalogue stars.

## 1. What the 2026-08-24/25 sky test established

The deterministic core loop completed without manual or model correction on
Mirfak and Algol. On HD 19445 (V≈8.06), a 5 s QHY witness succeeded with 32
detected stars, but three 10 s G3 frames at the target field and one 30 s G3
frame produced no formal PlateSolve3 WCS. A 5 arcmin east neighbouring field
then produced one formal solution with 22 sources and 7 matches. Two immediate
repeats degraded to 12 and 4 detected sources and did not solve. The final 5
arcmin north field reported 85 detections but zero catalogue matches. Dawn was
advancing throughout the sequence.

The half-hour challenge therefore ended because the usable dark-sky window was
exhausted; it is not classified as a program failure. The run nevertheless
exposed two design gaps:

1. a single-field, strong-solution policy is too bright-field-oriented for
   sparse targets;
2. simply accepting a low-match or high-source-count result is unsafe because a
   plate solver can return a false or geometrically inconsistent solution.

The next same-night M76 run resolved the ambiguity. The target field again did
not solve at the short tier, the automatic 5 arcmin east neighbour produced a
formal PlateSolve3 solution, and a direct WCS correction moved the catalogue
target into the slit field. Fresh post-move WCS response, dark-slit midpoint,
PHD2 exact-lock and science signal independently agreed. PlateSolve3's source
and match counts are therefore telemetry, not a second project-authored
minimum-star veto.

## 2. Non-negotiable ownership and evidence rules

- N.I.N.A. remains the only ATR585M and mount-motion owner.
- PHD2 remains the only G3M2210M camera owner and provides native guide-star
  selection. The coordinator validates PHD2's returned star; it does not rank a
  replacement catalogue of its own.
- The QHY service remains the only QHYminiCam8M owner.
- Every G3/QHY/ATR FITS is immutable. Failed, clipped and low-match frames remain
  evidence and never get overwritten by a later success.
- N.I.N.A.'s native centring machinery is preferred for ordinary wide-field
  solve/correct/repeat work. Project code supplies QHY frames, ownership checks,
  motion envelopes, durable intent and fresh WCS verification.
- A formal PlateSolve3 success is trusted when it contains finite coordinates,
  position angle and residual, has a physically plausible plate scale, and is
  within the configured two-optical-axis reach envelope. Source/match counts
  are retained as telemetry rather than re-litigated with a hidden minimum.
  Any large correction still requires fresh post-move WCS response and the
  normal bounded-motion ledger. A target-name lookup or image-language
  interpretation is not motion authority.

## 3. Target classes

The plan must declare one target-observability class. The class changes the
required evidence, not the device owner or safety envelope.

| Class | G3 expectation | Placement evidence |
|---|---|---|
| `DirectStellar` | Target has a measurable, compact peak or a commissioned saturated-core/obscured-core signature | Fresh WCS identity plus direct target-to-fresh-slit-midpoint residual |
| `FaintPointSource` | Target may be below reliable single-frame centroid SNR | Trusted WCS/catalogue projection plus off-slit PHD2 guide mapping; target disappearance at the slit is allowed |
| `CompactExtended` | Central source or compact nebular core is non-stellar | Trusted WCS and a versioned morphology/centroid policy; no stellar-FWHM requirement |
| `ExtendedNebula` | No unique point centroid may exist in G3 | WCS-defined aperture/offset and off-slit guiding; science validation comes from the expected ATR spatial/spectral aperture |
| `InvisibleInG3` | The science target is not directly detected in the guide exposure | Multi-field WCS, catalogue propagation, off-slit guide lock and ATR signal/stack evidence; a missing target peak is normal, not an automatic failure |

Focus evidence is separate. A faint target is never required to provide the
three or more stars used to qualify the C11/G3 focus domain; use a prior fresh
focus record for the same installation and optical state.

## 4. Hierarchical WCS evidence instead of one star-count gate

No universal `minimum stars` literal decides whether an observation can
continue. The solver's own extraction/match diagnostics are retained, but the
coordinator evaluates one of these versioned evidence classes:

### A. Formal PlateSolve3 solution

The fast production route accepts PlateSolve3's formal result without a
project-authored minimum source or match count when all physical checks pass:

- coordinates, position angle and solver residual are finite;
- plate scale lies in the configured optical range (the current policy uses
  0.70–1.30 of the value predicted from focal length, pixel size and binning);
- parity matches the locked installation record;
- distance from the mount hint is no more than the configurable maximum
  two-optical-axis offset plus half of the G3 detector diagonal.

The last item describes what the two telescopes can physically see, not how far
one correction may move. The motion family's much smaller single-step,
cumulative, attempt, time and return-reserve limits remain independent. After
a large direct WCS move, a fresh solve must show the expected sky response.

### B. Low-match temporal cluster

Three or more independent frames at the same reported mount position may form a
candidate cluster even when each has only a small number of matches. Acceptance
requires all of the following:

- pairwise centre, scale and orientation agreement;
- catalogue residuals consistent with the image scale and extraction quality;
- no competing cluster of comparable support;
- the same pier side, ROI, binning, camera identity and installation epoch;
- a fresh mount timestamp and no motion during any exposure.

This cluster is a fallback only when no formal solution is available. Its frame
count and geometry tolerances are configuration fields with a policy ID and
SHA-256. They are not hidden source literals.

### C. Overlapping multi-field mosaic

When the target field cannot solve, visit a bounded square/spiral set of
neighbouring pointings with a commissioned overlap fraction. Each field keeps
its reported mount coordinates, exact commanded delta, FITS hash, extracted
sources and all solver candidates. Candidate WCS results are transformed into
one common tangent plane after subtracting the commanded field offsets.

A mosaic becomes authoritative only when a connected overlap graph has:

- at least the configured number of independently solved nodes;
- mutually consistent overlap translations/rotations and sky geometry;
- no unresolved alias with a second catalogue placement;
- a bounded covariance for the inferred original target-field centre;
- enough remaining action/cumulative/time budget to return to the origin and
  perform the science placement.

The route stops immediately on a strong result. On exhaustion, cancellation,
pier change, stale mount time or worsening environment it returns to the saved
origin using the durable motion ledger, or pauses if safe return cannot be
proved. It never concatenates images and submits an unbounded giant frame to
the solver. A rendered overlap mosaic may be produced for diagnosis, while
astrometric authority comes from the graph of immutable native frames and
their solver/source correspondences.

### D. Recent QHY-to-G3 transfer

A versioned two-camera transform may seed the G3 search only when its physical
identities, installation epoch, pier side, orientation, ROI/binning, age,
temperature range, covariance and independent validation residual all pass.
The preferred commissioning procedure is nearly simultaneous G3 and QHY WCS:
as soon as the long-focus G3 frame solves, capture and solve QHY with minimum
latency, then record the simultaneous field centres and uncertainty. A stale or
inapplicable record is skipped; no fixed optical-axis offset is hard-coded.

## 5. Motion policy

Large WCS residuals use N.I.N.A. mount slews/centering moves directly. PHD2
exact-lock is reserved for the final, small detector-plane correction and
ongoing guiding; it must not slowly crawl across arcminute-scale errors.

Every field visit and return is subject to independent configurable limits:

- maximum single move;
- maximum radius from the saved origin;
- maximum cumulative outbound plus reserved return distance;
- maximum field count and solve attempts per field;
- minimum mosaic overlap;
- maximum elapsed time;
- horizon, pier-side, mount-clock and environment checks before every move;
- a durable pre-command intent and post-command reported-position record.

## 6. Guide acquisition and faint-target handoff

If a direct stellar target is visible, use a target-appropriate PHD2 exposure
and send that catalogue-confirmed target toward the fresh dark-slit midpoint.
PHD2 supports 10 ms exposures; after an exposure change the first stale pipeline
frame is discarded and a fresh frame's exposure is verified. If the target
vanishes because it entered the slit or can no longer be tracked, that is an
expected transition. PHD2 then performs native full-frame off-slit selection.

For `FaintPointSource`, `CompactExtended`, `ExtendedNebula` and `InvisibleInG3`,
the default is an off-slit guide from the beginning. The final lock epoch must
bind:

- the trusted target WCS/catalogue projection;
- the selected PHD2 guide coordinate and current calibration/topology;
- the fresh slit midpoint and allowed residual;
- any direct-target-to-off-slit epoch translation;
- post-settle samples and a post-capture target/slit proxy.

The program does not require a visible stellar target peak or fabricate target
flux in the final guide frame for these classes. The production runner records
`CatalogWcsProjection`, marks target flux not applicable, lets PHD2 perform
native off-slit selection, and closes on catalogue/WCS geometry, exact-lock
translation, fresh dark-slit midpoint, fresh guide/lock residual and settle.
If an auto guide policy falls back to direct-target guiding, the target must be
actually re-detected at that exposure; a synthetic WCS point cannot be selected
as a guide star. Durable mid-stage direct-to-off-slit handoff after a later loss
remains fail-closed.

## 7. Adaptive ATR science exposure

An exposure time such as 10 s is never a target-independent constant. The
operator-visible exposure ladder is a configurable starting grid; the default
includes `0.01, 0.03, 0.1, 0.3, 1, 3, 10, 15, 30, 60, 120, 300, 600 s` so the
10-to-30 s gap does not force an unnecessarily conservative choice.

The implemented first stage takes immutable probes, finds the spectral trace in
the configured spatial aperture and rejects trace-local saturation, clipped
wavelength-column fraction and long clipped runs. Whenever a different tier is
selected, that tier must be recaptured and pass on its own; a clipped frame may
not authorize a lower science exposure by extrapolation alone.

The next production increment uses multiple probes and rolling science-frame
adaptation:

1. acquire at least two probes at the candidate tier when time permits;
2. select from the upper envelope of trace peak/clipping metrics, not one lucky
   low-throughput frame;
3. compute a bounded candidate exposure between configured minimum/maximum and
   snap only when the camera/API requires discrete tiers;
4. after every science frame, update a robust throughput estimate and allow
   only a bounded multiplicative change before a fresh probe;
5. immediately back off and re-probe on trace clipping; do not delete the
   rejected frame;
6. for faint spectra, optimize accumulated-stack SNR and maximum sub-exposure
   constraints rather than demanding high SNR from each frame.

A fixed bad/hot-pixel mask and persistent-column map may exclude commissioned
detector defects from clipping statistics, but the mask is versioned and cannot
be inferred from the current target alone. The 2026-08-25 Algol frames contain
one repeatable saturated pixel outside the trace; it is recorded as a detector
defect candidate, not mistaken for spectral clipping.

## 8. Acceptance milestones

Acceptance status after the M76 run:

1. a sparse stellar field solved by a consistent low-match cluster;
2. a target field recovered by a two-or-more-node overlapping mosaic and
   returned to its origin on a forced failure;
3. **partly passed:** M76 was placed from WCS geometry, held by an off-slit
   guide and verified by emission-line SNR; a 3C 273-class invisible point
   target remains to be replayed;
4. **passed for the field-tool route and implemented in production:** M76 did
   not require the nebula to pass a stellar-shape gate;
5. adaptive exposure responding both upward and downward during a science block
   without clipping or silently discarding frames;
6. all routes preserving N.I.N.A./PHD2/QHY/UVEX ownership, bounded motion,
   immutable evidence and operator-controlled roof safety.

## 9. 15 µm slit interpretation and parallel QHY imaging

The M76 commissioning exposure used slit-wheel slot 2, nominally 15 µm. This
was the correct retained setup for the preceding nova but is throughput-limited
for a planetary nebula whose surface brightness is poorer than an approximately
8th-magnitude point source. A low continuum SNR is therefore not a placement
failure. At 600 s the accepted M76 frame measured continuum SNR/resolution
element about 2.02 but line SNR/resolution element about 19.90, with zero
trace-saturated fraction and no clipped dispersion columns. Target selection
for emission nebulae must prioritize line and accumulated-stack SNR; choosing a
wider slit is an explicit resolution/throughput observing-plan decision.

QHYminiCam8M and its integrated filter wheel may operate concurrently with the
N.I.N.A.-owned ATR585M because they are different physical cameras with
different owners. A photometry request can now carry a repeating, configured
`Filter:ExposureSeconds` sequence such as `H:60,O:60,S:60`. The QHY service
moves and reads back the wheel for every frame, writes the actual filter to
FITS, labels roles `PHOTOMETRY-H/O/S`, and maintains a separate transparency
baseline per filter. The production default records detected-star count and
transparency but does not impose the former hidden 10-star veto or a
cross-filter transparency veto (`0` disables each blocking threshold); the
configured saturation fraction remains a hard pixel-quality gate. This is
optional sidecar imaging; it never borrows ATR or G3 ownership.
