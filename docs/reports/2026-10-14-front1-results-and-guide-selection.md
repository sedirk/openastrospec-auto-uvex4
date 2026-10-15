# front1, October 14 evening: results and bounded guide-selection recovery

All times below are UTC+8. This report concerns the native night beginning at
19:23:29 and ending at 21:16:06, not the previous morning's front0/later0 runs.
Source: night journal `night-20261014T112329753Z-884a3058c9004a168e749988add13b60`,
the per-target manifests and immutable evidence, and the corresponding PHD2 and
N.I.N.A. logs. No original observing product is modified by this investigation.

## Actual result

| Number | Target | Recorded outcome | Accepted spectroscopy |
| --- | --- | --- | --- |
| 01 | HD 219937 | Completed | 2 × 300 s |
| 02 | HD 220526 | Completed | 2 × 600 s |
| 03 | HD 220539 | Quality failure, checked stop, skipped | 0 |
| 04 | HD 221662 | Completed | 2 × 300 s |
| 05 | HD 222940 | Quality failure, checked stop, skipped | 0 |
| 06 | HD 223211 | Completed | 2 × 120 s |
| 07 | HD 223554 | Search exhausted, confirmed return, skipped | 0 |
| 08 | HD 224060 | Safety-monitor Unsafe cancellation | 0; retained probes are not accepted science |
| 09 | HD 224474 | Not executed | 0 |
| 10 | HD 417 | Not executed | 0 |

Eight accepted frames total 2,640 seconds (44 minutes). Each completed target
includes one valid final probe reused as science; do not count it twice. Targets
04, 06 and 08 actually executed after the preceding quality failures. There is
no evidence of a quality-failure-to-whole-night-blocker regression in this run.

## Failure evidence

### 03: weak off-slit candidates, then an ambiguous direct-target fallback

Run `UVEX-20261014T121309Z-ed973acef1d6462` first obtained neighbouring-field WCS
and two independent 30 ms target measurements, with a 1.05 px spread. Native
off-slit selection at 2 s then lost its preview candidate during SNR sampling
(frame 5, native error 2, SNR 0). The old code retried at 2 s instead of trying
an available longer tier, exhausted local selection and fell back to 10 ms
direct-target guiding.

The final direct frame contains several nearby detected peaks, not one unique
measured core. Offline replay reproduces `TARGET_AMBIGUOUS`. The old, successful
short-frame position must not be submitted as a new measured star. The terminal
`PHD2_DIRECT_TARGET_POSITION_UNMEASURED` therefore remains a target-quality
outcome, not fabricated placement or permission to guide a catalogue cursor.

### 05: real star loss after warning-probe admission, expensive reacquisition

Run `UVEX-20261014T123506Z-90280ef4b30f4b1` admitted warning probing at 20:46:01
with target/slit residuals 9.07, 4.94 and 6.38 px, preserving the measured slit
aperture geometry. StartGuiding reused that session at 20:46:05. The subsequent
`GUIDING_UNSTABLE` was **not just a stale timer or a contradictory precision
threshold**: PHD2 recorded SNR 2.9 and native low-SNR star loss at 20:46:11 and
20:46:15. The guide epoch changed. Old optical proof cannot authorize science
through those losses. The original pre-guide SNR median was 38.05, so it did not
predict the later deterioration; cloud is plausible, not proven for this target.

Checked stop and fresh star detection worked. Full reacquisition then consumed
the remaining original five-minute fine-motion lineage; at 330.8 seconds no
additional complete window plus return reserve was available. It ended as
`PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED`, then target 06 executed. The code also
skipped SNR preparation on this reselection simply because guiding had occurred
earlier in the run; that run-wide shortcut is corrected below. The original
motion ledger, return reserve and deadline are not extended.

### 07: no successful G3 solution; not an unused successful-neighbour solution

Run `UVEX-20261014T125942Z-58f1b1c94c6142b` attempted 5/10/15 s solves at the
initial field and three neighbouring fields: twelve failed G3 solves. The last
two fields had only roughly 1–6 usable sources per exposure. No successful G3
neighbour solution existed to project the target or establish a same-run
QHY/G3 pair. A wide-field QHY solution alone does not establish the unmeasured
dual-camera offset. Sparse fields and diminishing star visibility can explain
the failure; these data do not prove a solver implementation defect or cloud
alone. The original bounded search returned and skipped correctly, allowing 08.
Do not promote a historical pair, reset the search budget or invent an identity
to manufacture success here.

### 08: genuine configured safety action, not an ordinary target failure

Run `UVEX-20261014T130922Z-b11bf9fc1521469` passed slit/guiding and reached
probing. At 21:14:16 the configured AIWeather authority reported MostlyCloudy,
75% cloud coverage and Safe=False. SafetyMonitor became Unsafe at 21:14:17.
The independent closeout confirmed cover closed, mount parked and roof closed
at 21:16:06, with no closeout errors. The local student/shadow estimate differed;
it is not the configured safety authority and cannot silently override it.
The later Safe=True readback neither invalidates that event nor resumes a night.

## Implementation, 0.4.0.296

- Explicit native low-SNR loss during **pre-guide preview** can propose the next
  supported longer tier (at most 2×, ceiling 6 s) before direct fallback. Confirm
  stopped, take a fresh exposure, exclude the lost candidate and ask native PHD2
  to select again. Missing data, clipping, context changes and active guide loss
  do not acquire this optional route. The local candidate allowance is retained.
- Existing adaptive reselection/forced-calibration admissions may run SNR
  preparation again, but only from their current checked-stop receipt, on the
  same connection, with no outstanding motion. Single-use preparation admission
  is distinct from permission to guide or expose science. No extra recovery or
  calibration attempt is added.
- The two permitted exposure increases are shared across initial preparation
  and recovery; they are not reset on reselection. Direct-target minimum exposure,
  target ambiguity checks, native initial settle, guide output, device ownership,
  all motion/time limits and the full independent weather chain remain intact.

This improves the available recovery before abandoning a weak guide; it cannot
promise success under actual cloud, an unsolved sparse field or ambiguous target
pixels. No design-baseline relaxation is introduced.

## Verification

Focused tests exercise the recorded error-2 exposure proposal, shared increase
limits, single-use checked-stop admissions, reconnect/active-guide/paused/motion
rejection, and actual execution of the next native target for all three recorded
quality failures (plus failed-stop negative cases). Raw-FITS replay preserves
HD 220539's two identified short frames and genuinely ambiguous later frame.
Productive-run replay includes the four successes above as well as nine earlier
successful runs. Replay checks immutable hashes and drives extracted placement,
probe and science controllers; its device/safety ports are fixtures, not a new
real-sky validation.

Final release checks passed: 3,731 .NET tests across twelve test assemblies,
including 148 offline UI tests; 75 pinned Python tests; Ruff; all four frozen
design hashes. Thirteen productive runs replayed 34 accepted historical frames,
with unchanged raw hashes. No observation was started for verification.

At 21:53, confirmed native sequence inactive, PHD2 stopped, ATR idle, focuser
idle, cover/roof closed and mount parked/not tracking. Backed up Profile and
installed plugin, exited N.I.N.A. normally and installed all 41 exact artifact
files. The restarted frontend reports 0.4.0.296 and build SHA-256
`33C848AF59C578F90198B6DB9F339D80DDB5BB50DB71FBACE963D359C4B1B594`.
Startup log has no ERROR/XAML/binding errors. Workflow/ATR navigation updates
frontend tab state, but panel docking reports `PanelNotAvailable`; no pixel-level
panel acceptance is claimed. PHD2 and installed service processes were not
restarted. N.I.N.A. remains Idle, unarmed, with no active observation or restored
standby; its equipment adapters are disconnected after normal restart. The roof
was confirmed physically closed before exit, not inferred closed from an offline
post-restart adapter.
