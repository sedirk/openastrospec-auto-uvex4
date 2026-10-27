# mid0 acquisition regression and .314 repair

## Scope and observed result

The owner reported that mid0 performed worse than .311 and authorized repair,
optimization and normal idle-boundary installation, without starting observation.
The owner separately approved ADR-0045's formal-WCS direct coarse approach.
The .313 assembly was loaded in the actual October 25 evening N.I.N.A. process;
the continuous QHY stream was active. This is not a stale-plugin explanation.

The latest night began October 26 local time 00:10:46 (October 25 UTC). Records:

| Native order | Target / run ID | Actual terminal evidence |
| --- | --- | --- |
| 01 | HD 218393 / `UVEX-20261025T161046Z-767f2a82eabb488` | 00:18:11 visibility-window exhaustion; confirmed stopped and skipped; no ATR science |
| 02 | HD 224559 / `UVEX-20261025T161820Z-3eb35419525e4fa` | 00:23:23 inferred-position expiry; returned and skipped; no ATR science |
| 03 | HD 698 / `UVEX-20261025T162356Z-f5d2cbf782184c9` | 00:30:06 search exhausted after coarse return and later local-dual position expiry; skipped; no ATR science |
| 04 | 10 Cas / `UVEX-20261025T163039Z-10d2b8284fb845e` | Last actual log 00:31:28 at wide-field acquisition, after GOTO; no terminal proof. Do not report this as another completed failure. |

The first three genuinely failed to reach spectral probing. The night did execute
the next targets; this particular record does not prove an immediate whole-night
misclassification. The real log stops before a final cleanup; later testhost NINA
logs are not real observing sessions. N.I.N.A. was already absent when inspected.

## Causes and changes

1. **Optional fallback competed with the continuous QHY job.** HD 224559's
   `00116` transient receipt records HTTP 409: the continuous job already owned
   the camera. The exception escaped the optional local-dual witness request and
   was labelled a G3 solver failure, skipping the already captured `00110` 5 s
   raw image. A production dispatch guard now withholds the optional second job
   while a stream exists; existing stream/cached witnesses remain usable. Missing
   optional pose falls through to the configured solver on the same G3 pixels.
   It does not stop/restart the owner, mint retries or swallow safety exceptions.

2. **Mandatory overlap consumed coarse action limits.** HD 218393 started roughly
   1,904 arcseconds away and still needed about 447 arcseconds after seven moves;
   HD 698 started around 1,969 arcseconds away and also needed seven outbound
   moves. These 120–330 arcsecond segments arose from a recovery overlap policy
   imposed even on formal current-frame solutions. ADR-0045 permits a direct
   formal approach within unchanged original limits. Inferred WCS and blind
   search keep the overlap cap. Registered/mosaic orientation is retained with a
   wider angular margin, not replaced by an unknown short-axis bound. New-image
   response verification and original return reserve remain mandatory.

3. **Image drift double-counted encoder/readback tolerance.** HD 224559 `00126`
   contains three fitted and two independently held-out stars, with maximum
   holdout residual 0.5035 px. Actual native brackets give a minimum midpoint gap
   of 9.3044871 s. The old 2.8779 px/s upper bound included another approximately
   10.5 px of mount arrival tolerance despite image registration already measuring
   that displacement. Corrected calculation retains the observed corner motion
   and a one-pixel minimum uncertainty at each endpoint (about 1.8644 px/s here).
   Nonzero polar drift, shutter smear and subsequent processing delay still count.

4. **Current measurement inherited an old prediction's expiry.** An accepted
   independent short-frame pair now carries bounded measured-position freshness,
   provided its original catalogue association was validated before expiry.
   Both raw frames, accepted receipt and every original inference parent remain
   hashed; original WCS/expiry are not rewritten. Current position is limited to
   30 s, the original recognition envelope and measured drift, checked again after
   hashing. Component identity, multi-lobe rejection, focus/pier/owner and mount
   bindings are unchanged. A single detection is not promoted to a confirmed pair.

5. **Visibility duration expanded from 35 to 55 rolling minutes.** HD 218393 was
   currently at 48.58 degrees, above the 42-degree wall. A nested recovery added
   20 minutes to the native 35-minute allowance and forecast it again from the
   current instant. Native target execution now carries its original absolute
   deadline through every stage; visibility protects only the remaining interval.
   A shorter actual science block can tighten it. Current horizon, night cutoff,
   safety and exhausted original deadlines still veto commands.

The continuous-owner conflict is specific to the new continuous route. Mandatory
overlap and the rolling visibility calculation were also design/implementation
constraints in older paths. There is no controlled identical-sky comparison that
would justify attributing every failure to .312/.313, darkness or clouds.

## Reproducible offline evidence

- `RecordedG3OverlapTests.Mid0Hd698ActualShortPairConfirmsTargetWithoutRequiringAnotherWholeFieldSolve`
  reads the actual `00136` and `00138` 10 ms frames through the pinned SEP worker
  and production confirmation policy. The independent centres agree by 1.8918 px.
  Source SHA-256 values are asserted before/after. The star was still hundreds of
  pixels from the slit: this is target-measurement evidence, **not slit success**.
- `Mid0MosaicDriftIncludesImageHoldoutErrorWithoutAddingEncoderToleranceAgain`
  uses the actual `00126` transform and `00111` / `00131` capture brackets, not a
  fabricated timestamp or a time gap reverse-engineered from the old drift rate.
- Isolated installed PlateSolve3 replay of HD 224559 `00110` succeeds in about
  6 seconds, using only the earlier `00094` formal position/scale as the hint.
  The immutable source SHA is
  `7F6892BDDB67D17BC99D53E0359EB091741F7BD5A0EA42887503B192BD7E0649`.
  Raw FITS and installed solver/config hashes remain unchanged. Generated inputs
  and receipt are under ignored `tmp/replay-0314-hd224559-causal-5s`.
- Historical successful HD 219937 controls still register; non-overlapping
  HD 220526 frames still cannot invent registration. The latter also documents
  the deliberate tradeoff: a direct formal approach can lose overlap, so a fresh
  destination frame remains necessary and a later optical miss is still possible.
- Actual production expiry gates feed two real native target/coordinator stacks:
  checked quality failure executes the next target through finalization; failed
  cleanup or late unsafe state prevents that continuation. Finite QHY owner ports
  prove only one job is started and the retained G3 frame reaches the solver.

No historical image exists at the newly proposed direct destination. These tests
establish corrected decisions and available measured evidence, not future on-sky
completion or a claimed 100% target success rate. Original observations, manifests
and durable motion budgets are preserved.

## Release acceptance

Full release rerun: **4,567 .NET tests passed**, 12 suites, build/publish succeeded.
The first full run found one obsolete universal-overlap wiring assertion; it was
updated to the explicitly approved authority-aware policy before this clean run.
Focused replay/regression: 67 passed; separate native-container run: 275 passed;
broader explicit historical/continuation run: 170 passed. Python Ruff and 304
tests passed, with 68 optional UI tests skipped (no recurring GUI popups).
Frozen-design verification, strict public audit and diff checks passed. Only the
explicitly approved baseline hash changed; ADR-0001/0002 and AGENTS were unchanged.

All 171 offline UI PNGs match .313 byte-for-byte; the failure view was inspected.
The isolated renderer's existing NOVAS/SOFA/SQL dependency diagnostics are not
real N.I.N.A. startup evidence. Live runtime checks are recorded below; actual
on-screen pixel coverage is not claimed.

The .314 artifact is frozen with 56 files, plugin SHA-256
`5C015071B90A248938948DEEAE62A82FCF27C25A1581AD06776D348197052F9C`.
Old plugin and Profiles are backed up; 107 raw/manifest/control files from the
four mid0 runs have preservation hashes. Read-only checks show N.I.N.A. absent,
PHD2 stopped, QHY jobs joined and UVEX operations terminal; no owner was restarted.
QHY remains connected to its existing service, without active acquisition.
The owner subsequently confirmed all acquisition/motion stopped and on-site
supervision of the still-open roof. Immediately rechecked the software idle
boundary and artifact/preservation hashes, backed up Profiles again, installed
the exact 56-file package and started N.I.N.A. normally (already exited; no kill).
Actual startup: October 26 01:09:55 Asia/Shanghai, PID 440.

Bridge and actual startup log confirm .314 and the frozen plugin SHA, Idle,
no observation ID, no focus operation, no real-control authorization, no standby
and empty UI error. ATR workspace/preview indices are 4/2 with manual inspection
expanded. Observation/calibration dock navigation remains `PanelNotAvailable`;
this is limited runtime-state acceptance, not verified live dock pixels. Actual
startup log has no ERROR/XAML/binding/unhandled exception at acceptance time.
All 56 installed files and 107 preserved files match; other owner PIDs unchanged;
QHY jobs remain joined with no new capture.

**Installed and reopened; no observing started.** N.I.N.A.'s camera, mount, dome,
guider, focuser, cover and safety-monitor interfaces remain disconnected. The
existing QHY service connection is unchanged. No automatic safety standby was
restored. The owner was explicitly told that the open roof still requires their
supervision until safety protection is re-established or the roof is closed.
No camera opening, focus, mount/roof operation or other owner restart was issued.
