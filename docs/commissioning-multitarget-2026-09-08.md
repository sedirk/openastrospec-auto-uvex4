# Supervised multi-target frontend commissioning — 2026-09-08 evening

This is a new observing night, separate from the
[September 7 results](commissioning-multitarget-2026-09-07.md). Times are UTC+8;
production run IDs contain UTC. This is a live commissioning record: a started
run, successful solve, or guide-lock readback is not a completed observation.

## Scope and acceptance

The operator authorized clear-sky, roof-open real frontend testing on different
target types, necessary repairs, N.I.N.A. restarts and verified homing. The
operator reported nobody operating near the equipment. Stop conditions remain
operator intervention, prolonged clouds and dawn. The per-session supervised
slit-precision warning/probe policy is explicit; identity, physical safety,
current guide evidence and durable motion accounting are not waived.

Observing uses `invoke-model-frontend-closed-loop.ps1`, the named-pipe bridge and
the actual visible Dockable commands/shared production runner. No computer-use
automation or substitute script-driven sequence is used. Maintenance happens
only at terminal run boundaries and does not count as observation success.

Acceptance requires a distinct production run's 11/11 completion, durable
`RUN_COMPLETED`, successful controller result, accepted science frames and
independent read-only FITS checks of hashes, target, run, role and capture IDs.
Spectral signal is reviewed separately; precision warnings remain in the data.
None of this alone certifies wavelength/response calibration, photometric
precision, exact slit placement or unattended operation.

## Installed starting configuration

- N.I.N.A. plugin 0.4.0.151, SHA-256
  `F63A9F6EDDC375A21DE6813E34859777762A6903D6CCF989A40E1AADA4DC1F00`.
- Morning UVEX service update: installed Protocol SHA-256
  `6217DB4A57140049278BAB1EA7539FBCF1F24091912E969A43F16D4E14C440C3`.
  This includes the SLON/SLOF response handling missing from the previous
  installed service; a plugin installation alone had not updated that component.
- The September 8 restored 2 s guiding commissioning package remains selected.
  No G3/WCS motion budget, slit quality tolerance, Sync threshold, hardware
  identity, exposure setting or SDK/firmware was changed for this repair.
- This machine still explicitly uses the legacy QHY service owner. These tests
  must not be reported as dual-N.I.N.A. worker real-hardware acceptance.

## Deneb first attempt: real service regression passed, guiding failed

Run `UVEX-20260908T111509Z-bed216efadcf4e6`, started 19:15:09, stopped during
`PlaceTargetOnSlit` with `SLIT_LOCK_RETURN_CUMULATIVE_RESERVE`; zero ATR probe or
science frames. It was subsequently cancelled, not relabelled as completed.

The new service passed both nine-frame LED OFF/ON/OFF sequences. The formal
same-run slit identity matched 15 um position 2: measured width 2.25 +/- 0.50 px,
LED contrast 9.792 sigma, midpoint approximately (817.441722,425.971317). Fresh
target/guide residuals used this immutable LED geometry, not visibility of a
star-illuminated dark slit. This is the first real regression of the newly
installed service component.

The .151 post-WCS full-radius target recognizer handed off at about 9 px. The
independent guide-selection image placed the same solid target core about
6.29 px from the slit, so the later displacement was not the prior post-WCS
clipped-halo substitution bug.

PHD2 initially selected the real compact off-slit star at (926.37,598.68).
Its native debug log at 19:18:42 then records a 50 px `Star::Find` search jumping
to (873.80,606.86), and the following guide frame to (836.55,564.62), on the
nearby bright ghost. Native guide corrections consequently displaced the field.
The target residual grew to tens of pixels. Three 12.5 px outbound stages plus
the checked return used nearly 75 px of cumulative motion. The bounded recovery
subsequently selected the proper star (native SNR about 70-85), but fresh target
residuals 20.77/21.69/21.68 px could not reserve another outbound stage and return
inside the unchanged budget. The reserve guard correctly retained that debt.

Evidence is under the immutable production run directory. Key records:

- `00044-20260908T111829669Z-g3-phd2-guide-selection-OffSlitGuideStar.fit`;
- `00046-20260908T111840316Z-phd2-full-frame-guide-takeover.json`;
- outbound intents 00054, 00063, 00072 and return intents 00081, 00083;
- recovery guide takeover 00096 and final fresh residuals 00098, 00100, 00102.

## Between-run repair: native PHD2 star-search window

After visible cancellation, N.I.N.A. homed the mount and provided two stationary,
tracking-off confirmations. The exact PHD2 profile 2 was backed up, native
equipment disconnect confirmed, and native shutdown completed normally. Only
then was the existing profile's `guider/onestar/SearchRegion` DWORD changed from
50 to 20. PHD2 was restarted; its native `get_search_region` returned 20, profile
identity matched, and state was Stopped. Calibration was not cleared or rewritten;
the next production connection is responsible for actual calibration validation.

This is a versioned local commissioning adjustment for the observed neighbouring
ghost contamination, not a machine-independent 20 px default or a relaxation of
guiding/placement quality gates. The guide owner still detects and selects stars;
the coordinator does not rank a replacement. The original full registry export,
intent, disconnect, shutdown and readback receipts remain local under
`maintenance/phd2-search-window-20260908T113050Z`. Homing receipt:
`maintenance/nina-home-20260908T112846Z-ea71f9ae`.

At 19:31:52 a new Deneb frontend run started:
`UVEX-20260908T113152Z-59f6b4fea46447d`. Its unsuccessful terminal result is
recorded below; it does not count toward tonight's completed targets.

### Second attempt and .152 candidate-level repair

The second run selected the upper clipped feature at (747.45,235.23) during
initial native selection. The 20 px window fixes the earlier tracking-window
contamination mechanism but does not by itself prevent an initial selection on
a ring rim. The native guide SNR was about 6.5-9.2 with large measured offsets.
The run was explicitly cancelled before any ATR trial/science and the mount
was again verified home (`nina-home-20260908T113649Z-ac51d4bb`). It is not a
completed observation.

Read-only replay of this exact selection FITS (SHA-256
`7FCD544AAFCAE9D16162D8C5029255DBF59FDD8FC41F757C4CD7EF1FFE31E7A7`)
found three large saturated structures: the genuine filled target core,
a lower annular ghost, and the upper extended feature with *indeterminate*
topology. The upper structure's measured radius was 68.4 px, so the native
point lay on its extent. It would be incorrect to label that upper component
as a conclusively classified annular ghost; its clipped extended geometry is
itself inappropriate for ordinary compact-star selection.

.152 adds whole-detector candidate-level exclusion of measured annular and
extended saturated regions. It does not enlarge the catalogue target-recognition
window or use a new identity estimate. Each rejected native point triggers the
existing bounded fresh-frame/geometric-ROI `find_star` path; the next frame's
exclusion geometry is recomputed and hash-linked to the candidate evidence.
The coordinator never ranks a replacement. Ordinary unsaturated broad stars
remain under the existing advisory morphology policy; explicit supervised
direct-target fallback remains a separately labelled mode. Neither a local SNR
threshold nor guide precision is promoted to a new whole-run hard veto.

Four image/region regressions and the updated source-path check passed; the
complete build passed 1287 tests with zero compiler warnings/errors. All 26
offline UI harness scenes rendered and were visually inspected. .152 was
installed at 19:44:59, SHA-256
`AFF2B12AE62832E96B815EA1C2B935EA3B8F9E7DB6561FA8A95883B3D6B4F4DA`,
N.I.N.A. PID 420; the actual bridge reported .152, Idle, Deneb and no UI error.
The matching new process log records successful loading without an observed
XAML/unhandled exception. This is not a claim that all three real manual panels
have been opened; the actual observing path still requires the following run.
The .151 plugin/profile backup and installation receipt are retained under
`maintenance/plugin-0152-20260908T114456Z`.

## Deneb third attempt: completed frontend and independent science checks

Run `UVEX-20260908T114555Z-3f7d1cbc8aa34ee` started at 19:45:55 and completed
11/11 stages with a durable final manifest and successful frontend controller
`frontend-model-loop-20260908T114555Z-40b239f7`. It retained two probe frames
(one accepted), three accepted 3 s science frames and nine accepted synchronous
photometry frames. The camera had reached approximately -10 C for the science.

Native candidate evidence `00045-20260908T114903606Z-phd2-native-guide-candidate.json`
contains the new whole-frame exclusions for both annular ghosts and the large
filled target. PHD2's first native choice (920.37,599.45) was outside all three
regions and passed the unchanged edge/target/slit geometry checks. Thus the
new geometric guard ran on a real frame; this particular successful run did
**not** exercise candidate rejection/reselection, since the first point was
valid. The native 20 px tracking window also remained in effect.

Independent read-only review checked every retained ATR FITS hash, immutable
run/target/capture/role provenance, accepted science count and same-run LED
identity linkage on 22 distinct fresh residual frames. The three science
signal-to-noise proxies were approximately 389, 533 and 445, with zero clipped
dispersion columns. The extracted uncalibrated profiles show consistent
absorption structure but variable throughput/profile shape. This is not
wavelength/response calibration or a precision-stability certification.

The retained per-science residuals were 2.69, 0.63 and 3.17 px. The explicit
`DegradedSupervised` / slit warning remains in FITS and evidence; completion
does not relabel those measurements as strict 2 px placement. The independent
review products are local under
`tmp/night-20260907/UVEX-20260908T114555Z-3f7d1cbc8aa34ee/` (the existing
review helper's output root is named for the prior night; the verified run ID
and FITS provenance are September 8). Raw observations were not changed.

## 10 Lac first attempt: inefficient acquisition, explicitly cancelled

Run `UVEX-20260908T115253Z-147f346e939249a` started 19:52:53. Fresh LED geometry
independently confirmed the 15 um slit (2.50 +/- 0.50 px). QHY/PL3 succeeded,
but the 5/10/15 s G3 tiers failed despite a structured field; the ordinary
bounded neighbour search began. This was not an empty-image veto, and no star
was assumed to be 10 Lac merely because it was bright in a failed-solve frame.

The QHY WCS differed from the catalogue/mount pointing by approximately 2.89
degrees. The existing 5 degree trigger did not activate the ADR-0012 one-shot
coordinate recovery. Same-pointing paired WCS in the immediately preceding
Deneb run differed only by a few arcminutes, supporting an absolute mount
coordinate error rather than a degree-scale optical-axis separation.

At about 20:00 the controller's visible cancel was dispatched to end this
inefficient search; the run is **Cancelled**, not an exhausted-search result
and not a successful observation. It acquired no ATR science. N.I.N.A. then
provided two stationary, tracking-off home confirmations, receipt
`maintenance/nina-home-20260908T120055Z-a4e2df66`.

### Local one-shot coordinate-recovery commissioning adjustment

After normal N.I.N.A. exit and a complete profile backup, only this machine's
`G3MaximumPlateSolveHintOffsetDegrees` is changed from 5 to 2. The generated
profile's serialization is preserved, with exactly one numeric character
changed. Audit root: `maintenance/qhy-sync-trigger-20260908T120241Z`.
This is not a new global default and does not enlarge a search/motion budget.
The setting has both meanings documented by ADR-0012: G3 hint trust radius and
the threshold for one-shot gross coordinate-mismatch recovery. The resulting
new action configuration must be recorded by the next production run.

The production runner still requires fresh immutable same-pointing QHY WCS,
known pier/epoch, all immediate physical gates, durable intent, exactly one
N.I.N.A.-mediated Sync and a verified readback within 5 arcseconds before
reissuing the catalogue slew. Fresh G3 evidence must still prove target arrival.
No pending motion ledger is discarded and no failed run becomes successful.

Runtime verification found that this **profile-only edit did not become the
effective action setting**: startup reapplied the machine-local operational
template, restoring 5. Run `UVEX-20260908T120344Z-5422db8ea633461`, started
20:03:44 after N.I.N.A. restarted as PID 32484, explicitly recorded
`g3MaximumPlateSolveHintOffsetDegrees=5`. No Sync occurred. The agent did not
claim the requested 2 degree adjustment was active or rewrite that manifest.
The run nevertheless solved and entered ordinary G3 WCS centering by 20:06;
it was allowed to continue. Any operational-template correction belongs at
the next terminal boundary, with a new captured action configuration.

## 10 Lac second attempt: two science frames, recovery handoff failure

Run `UVEX-20260908T120344Z-5422db8ea633461` reached real native guiding,
fresh LED-linked slit placement, accepted probes and science. Same-pointing
QHY/G3 WCS differed by 256.22 arcseconds (4.27 arcminutes), not the roughly
2.9 degree absolute mount coordinate discrepancy. Fine placement progressed
from 14.05 to 4.99, 2.25 and 2.15 px with retained supervised quality warnings.

Two 120 s ATR science frames were saved and accepted, with signal-to-noise
proxies 435.72 and 162.55 and no clipped dispersion columns. The run retained
59 accepted synchronous photometry frames. These are partial observing
products, **not a completed three-frame target**. The native log contains
extended low-SNR star-drop intervals, brief recovery, then another loss;
weather was queried, not inferred as confirmed from the log alone.

At 20:17:22, `GUIDING_LOST` started the bounded `RebuildStageDependencies`
recovery. At 20:17:23 it failed because the full-frame capture primitive
correctly required confirmed `Stopped` while PHD2 still reported `LostLock`.
The automatic recovery entry had marked dependencies stale but omitted the
checked stop that cooperative manual pause performs. No third science frame
started. The failed run was explicitly cancelled and home was confirmed at
20:20:16 (`maintenance/nina-home-20260908T121946Z-6637bbfd`).

### .153: confirm the owned guide session stopped before rebuilding

The automatic dependency-rebuild entry now checks fresh PHD2 identity,
connection epoch, this run's guide ownership, unchanged commanded lock and
absence of operator pause/pending settling. Only an owned `Guiding` or
`LostLock` session may be stopped; calibration or changed ownership is not
silently interrupted. Durable intent and confirmed-stop evidence precede
dependency invalidation and any new full-frame acquisition. The 0.25 px
comparison checks commanded-lock identity, not guiding precision. LostLock's
old guide epoch cannot authorize science, but does not prevent stopping the
same owned session. Generic full-frame capture guards are unchanged.

Ten focused regressions were added. The complete build passed 1297 tests
with zero compiler warnings/errors; all four frozen records verified, and
26 UI harness scenes rendered and were visually reviewed. This is not a
claim that all real manual equipment panels were opened.

After a verified idle home boundary, native camera disconnect and normal
N.I.N.A. exit, .153 was installed at 20:36:09, plugin SHA-256
`6B4737F91767403ED75153F107CE15E507976ED5C6837BFAC63BE6DBC20CDC87`.
The restarted actual N.I.N.A. process PID 5368 reported .153, Idle, 10 Lac,
and no UI error. Service and PHD2 installations were not changed.

At this same terminal boundary the backed-up machine-local operational
template's `G3MaximumPlateSolveHintOffsetDegrees` was changed from 5 to 2.
JSON semantic comparison verified that no other template field changed.
Template SHA-256 changed from
`44380D19DE49F5E9A54063A55D69D207032570BB2BCAC0B2B7EAE439FD71B71B`
to `2FE1DF03EB53CCE294983E542FAA3F45B24FA04DDBB9AE5230BC73A34790D88B`.
Backups and install receipt are under
`maintenance/station-sync-trigger-20260908T123516Z`. Actual run metadata and
on-sky recovery acceptance still require the following production attempt.

## 10 Lac third attempt: effective template captured by the real frontend

The operator confirmed short-lived cloud had crossed the previous run and
the sky was temporarily clear again. Production run
`UVEX-20260908T142239Z-b8887f79633142e` started at 22:22:39 local time using
.153. Its immutable action labels explicitly record the effective value `2`,
with action-configuration SHA-256
`C77208F5E0E0A7B9919E0BE5B9107D2A3BD5FBA3833BD29E1B3F42A585CA022E`.
This verifies that the template correction reached the production runner;
it is not by itself proof that Sync or guide-loss recovery has succeeded.

At 22:24:11 the production one-shot Sync completed: fresh QHY WCS showed a
11599.26 arcsecond discrepancy; readback at the correctly transformed JNOW
epoch was 2.11 arcseconds from the requested Sync position. N.I.N.A. then
completed the one catalogue re-slew at 22:24:33. The next 5 s G3 frame solved
on its first tier with fourteen coherent sources. It projected the target
outside the image, so ordinary bounded WCS centering was still required;
successful coordinate repair was not mislabeled as optical arrival.

### Cloud interruption exercised .153; the complete target did not finish

The native candidate (765.36,601.77) passed the measured exclusion checks.
Fresh slit residuals reached approximately 2.19 px; a later supervised
pre-exposure window retained a 4.05 px warning. The 60 s probe had a 9840 ADU
high percentile and continuum-SNR proxy 244.84. Read-only detector inspection
showed a coherent trace and absorption structure, with unchanged input hash.
The exposure selector requested a 300 s validation frame for its 65-percent
range target. This was real integration time, not a solver loop.

That 300 s probe retained continuum-SNR proxy 387.16 and no clipped dispersion
columns, but it was **not accepted**: the guide continuity epoch changed from
95 in the pre-exposure proof to 101. The observer later confirmed more passing
cloud. This was not merely a residual above 2 px or a timed-out old read-only
window; guide continuity was actually invalidated. The raw probe remains
available, without being relabeled as accepted science.

At 22:34:22 the new .153 automatic rebuild path produced both
`phd2-dependency-rebuild-stop-intent` and
`phd2-dependency-rebuild-stop-confirmed`. It verified the same connection and
exact unchanged commanded lock (757.89,579.14), confirmed native `Stopped`,
and then acquired new full frames. The previous capture-while-Guiding/LostLock
dead end was therefore crossed in the real frontend path, not just in tests.

Recovery subsequently exhausted the three G3 solve tiers. Before any bounded
search move, it stopped at `G3_MOTION_FRESH_BINDING_HANDOFF_LIMIT`: a fresh
frame was 0.81 arcsecond from the current readback, but the displacement from
the earlier durable motion position was 94.80 arcseconds, beyond the separate
9 arcsecond continuity envelope. The fresh frame does not retrospectively
account for that displacement. This remaining recovery limitation is **not
fixed or waived** in .153; no origin, clock or motion ceiling was rebased.

The user reported repeated passing cloud at approximately 22:38. The actual
frontend Cancel command terminated this run, with no ATR science frames and
all four probes preserved. N.I.N.A. subsequently completed verified homing;
receipt `maintenance/nina-home-20260908T143908Z-f3a25a05/verified-home.json`.
A later readback confirmed home, tracking off, no slewing or pulse guiding,
and no ATR exposure. Roof commands were not sent. Further real tests are
paused pending the operator's weather/continuation choice, not claimed as
complete. Tonight's completed target remains Deneb; 10 Lac's earlier two
science frames are partial products, and Gamma Cas/Scheat are untested.

### .154 cloud-pause maintenance: budgeted return before new G3 acquisition

The operator requested this remaining recovery defect be fixed and the plugin
installed with N.I.N.A. restarted while cloud prevents observing. The new
shared production recovery entry consumes the checked owned-guide stop proof
and validates the original canonical motion ledger before taking another G3
frame. A position outside the independent continuity envelope now has an
explicit return-only path: charge the displacement, reserve the full return,
use the existing durable N.I.N.A. return, and require actual arrival before
rebuilding target/slit evidence. The 9 arcsecond handoff limit, original
origin/clock and all motion budgets remain unchanged. Both frontend entry
points still use the same runner; there is no maintenance-only acquisition
route or successful reinterpretation of the cancelled run.

A sanitized numerical replay reproduces the measured 94.80 arcsecond gap and
plans a one-segment return within the original budget. Negative cases cover
changed run/epoch/pier/owner, pending motion, missing stop proof, reconnect,
new PHD2 acquisition, nonfinite coordinates and exhausted action/distance/time
reserves. This is source/offline evidence only. The new automatic return and
the complete 10 Lac target remain **pending real frontend on-sky acceptance**;
maintenance does not start a new exposure, slew or roof operation.

The final .154 build passed all 1310 tests with zero compiler warnings/errors,
and all four frozen records verified. The 26 UI harness scenes were rendered
from the final build and visually reviewed. At 23:18:34 local time maintenance
again verified home, tracking off and no camera exposure. The native camera
was disconnected, N.I.N.A. exited normally, and the exact .154 artifact was
installed and restarted at 23:18:37. The new process PID 9232 reported
`0.4.0.154`, `Idle`, no current run, no UI error and no real-session arm.
The installed and bridge-reported plugin SHA-256 both equal
`C0930F9975844C4440AB2D7E4E236EB31193835B51B201B6A04185E91F4CF3C7`.

Backups and install receipts are under
`maintenance/plugin-0154-20260908T151834Z`. The operational template hash
remains `2FE1DF03EB53CCE294983E542FAA3F45B24FA04DDBB9AE5230BC73A34790D88B`;
PHD2 PID 7612 and UVEX service PID 32636 were not restarted. The new log is
`NINA/Logs/20260908-231837-3.2.0.9001.9232-202609.log`; its inspected startup
contains successful .154 loading and no XAML/binding/dispatcher exception.
The existing backend API can select N.I.N.A.'s Imaging workspace but cannot
individually open the calibration Dockable or expand the embedded manual
spectrum tool. Those actual three-panel checks remain **pending** under the
operator's no-computer-use constraint; offline rendering is not substituted
for them. No complete UI or on-sky acceptance claim is made.
