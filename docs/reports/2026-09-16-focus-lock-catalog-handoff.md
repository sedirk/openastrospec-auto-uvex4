# 2026-09-16: Main-focus run boundary and catalogue handoff

## v0.4.0.206 — main telescope focus

The owner requested that a between-run main-telescope focus change not invalidate
the spectrograph's internal calibration. ADR-0018 records this narrow change to
the frozen baseline. Each new runner captures the stopped, identity-checked C11
position within travel limits; Resume and downstream stages retain that same
position. A historical 5000-step Night Setup no longer rejects a new run at 4950.
The original setup, hashes, calibration and observations remain unchanged. UVEX
M2, GS350, owner/physical-identity and same-run focus gates remain enforced.
An accepted run position is not a new optical focus-quality measurement.

Full build: 2113 .NET tests passed; reduction Ruff and 66 Python tests passed.
All 91 UI scenes rendered and contact sheets inspected. Version .206 was
installed with Profile/plugin backups and normal N.I.N.A. shutdown, then the
loaded version and artifact hash were read back. The owner subsequently started
a new observation which passed Night Setup; no observing run was started by the
maintenance agent. The post-install idle-only verification script therefore
correctly refused to label that now-active state as an idle installation check.

## v0.4.0.207 — missing original WCS reference

The reported `STAGE_EXCEPTION / G3_CATALOG_REFERENCE_MISSING` followed the
`FaintPointSource` catalogue branch. A formally solved field was followed by a
small WCS correction and an unsolved arrival frame. The arrival replaced the
field state, but the original solved image/reference stars were not transferred
to the PHD2 catalogue registration stage. This is a software handoff defect,
not proof that the selected target's magnitude is unsuitable.

Capture the independent registration reference while the actual solved field
is available, carry it through the motion prediction and unsolved arrival, and
retain its original catalogue point. Fine placement still measures a fresh
three-star field registration and verifies run, PHD2 connection, pier side,
detector geometry and immutable reference pixels. Missing references stop at
handoff with a specific diagnosis; they do not become an unhandled stage error.
The visible-target short-frame shortcut is no longer used by a catalogue-only
plan: a nearby detected peak must not substitute for the requested coordinates.
No precision, return, motion or identity limit is relaxed, and no budget is reset.

An initial image-only replay found a 16-feature near-zero translation consensus.
Further inspection of the next bright-star run disproved its usefulness as sky
motion evidence: many SEP regions remained at detector-fixed positions while
the actual bright source moved. Support/area filters alone do not exclude all
fixed-pattern features. After a clearly resolved expected shift (>4 matching
tolerances), registration now excludes stationary correspondences before the
SNR cap and measures the remaining independent moving stars. It never treats a
command as measured motion. An unresolved small/zero shift remains a limitation
of this image-only test, not proof that the reference contains no detector defects.
Behavioral tests cover preserved reference coordinates, fresh measured shifts
different from predictions, rejection of peak substitution and missing-reference
handling; source-routing regressions cover both production handoff paths.

### Bright-star small-correction continuity

The subsequent run used **AutoFromPlanetarium / stellar priority**, V460 Cyg,
not the faint-source branch. At 20:52:38 local time a 24.47-arcsecond second
correction brought the target close to the slit. The 5-second frame contained
the target at roughly (825,426), but the following 10-ms gain-zero frame went
through the legacy identifier, unlike the earlier successful SEP pair. That
single rejection triggered three redundant PL3 tiers. No third slew was issued;
guiding started only around 20:55:01, by which time the target had drifted.
The logged 3.1-pixel residual was a prediction, not a fresh measured residual.

The post-correction short path now uses the existing SEP parent-region policy
and two independent consistent frames (unchanged 4-pixel repeat limit), bounded
to six captures and two exposure adjustments. Weak/ambiguous short images can
step 10 -> 30 -> 90 ms; measured saturation reduces exposure and prevents an
up/down oscillation. Settings changes discard the previous confirmation anchor.
Native owner parameter readback, raw hashes, run/mount bindings before and after
measurement, uniqueness, saturation and the original 20-pixel acquisition
window remain required. This does not relax exact slit placement or guide RMS.
Failure stops this local confirmation instead of running another 5/10/15-second
solve ladder and subsequently presenting the old forecast as fresh positioning.
Success retains the measured position evidence and hands off immediately to
PHD2's separate fresh residual/settle checks. UI text no longer infers
"faint/non-stellar" from a catalogue-identity enum and labels forecast residuals.

Saved-FITS replay through the production SEP/measurement policies finds the
previously rejected 10-ms frame at (821.09385,430.99126), SNR 9.73066. As there is
only one recorded post-move short frame, it correctly requests a second frame;
no new successful pair or on-sky placement is claimed. The earlier actual
two-frame SEP pair still passes with 1.70751-pixel separation. After stationary
feature exclusion, the first two long-frame pairs measure (-50.497,9.770) and
(-37.854,6.202) pixels using 3/4 stars. Later frames fail safely instead of
accepting a zero-shift fixed-pattern consensus. Generated replay outputs remain
ignored under `output/catalog-207-replay`; all raw FITS hashes are unchanged.

The earlier SEP autofocus failure is a separate pending issue. Its immutable
30-frame record shows a broad star being excluded by the shared 25-pixel aperture
at 4940 steps. Increasing every aperture also loses weak stars, so a blanket
threshold relaxation is not an adequate fix. No real-sky focus success is claimed.

### Release validation and deployment

The final .207 build passed all 2126 .NET tests; reduction Ruff and all 66 Python
tests passed. The frozen-design verifier and strict public-release audit passed,
with zero text findings or binary/data candidates. All 91 UI scenes were rendered
and their contact sheets inspected. Generated build, replay and UI evidence stays
under ignored `output/`; no observing products were added to source control.

The blocked run was cancelled through the existing frontend/backend boundary.
Read-only status checks confirmed no active acquisition or device motion, and
PHD2 independently reported Stopped. Profile and plugin backups preceded normal
N.I.N.A. shutdown. An intervening user launch caused the installer to refuse the
copy while N.I.N.A. was running; installation resumed only after the owner closed
that process and explicitly requested continuation.

At 21:36 local time, N.I.N.A. 3.2.0.9001 restarted as PID 18916 and reported plugin
version **0.4.0.207**. Build, installed and loaded artifact SHA-256 all matched:

`0EBF5CF9EDE8D29017873164D32EE235355796AFEDBC31F8C38831946B9B2AC8`

The target-strategy, spectrum/individual-frame inspection and calibration-library
panels were opened through their actual UI commands and visually checked. The
new startup log contained no ERROR, XAML parse, binding-path or unhandled-dispatcher
matches in the deployment check. The final state was Idle, focus not busy, and
real control disarmed. No observation, exposure, mechanical action or automatic
resume was initiated by this deployment; these checks are not an on-sky success.

Local backup and verification evidence:
`output/deployment-backups/catalog-207-20260916/`.
Native panel evidence:
`output/target-strategy-193-native-20260916T133617Z/` (the legacy script prefix
does not denote the installed version; its snapshots identify .207).
