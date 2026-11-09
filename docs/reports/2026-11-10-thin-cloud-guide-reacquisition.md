# 薄云后同场导星恢复与已完成帧保留

**Date:** 2026-11-10  
**Installed:** OpenAstroSpec Auto — UVEX4 `0.4.0.347`  
**Scope:** acquisition/Science continuation only; no new observation was started.

## Evidence reviewed

- `HD 25627`, run `UVEX-20261109T164602Z-ae4c4cd63a0a4da`
- `HD 25868`, run `UVEX-20261109T171456Z-e3943299e73d4e8`
- `HD 26380`, run `UVEX-20261109T175913Z-8f18f671b78a48f`
- latest post-restart runs for `HD 25627`, `HD 25868`, `HD 26380`,
  `HD 26546`
- PHD2 guide log `PHD2_GuideLog_2026-11-09_214018.txt`

The AIWeather log repeatedly reported 5–35% cloud between 01:39 and 02:24. This is
consistent with intermittent thin cloud, but it does not explain every software
decision by itself.

For `HD 26380`, the 600 s ATR probe completed at 02:17:01 and passed the trace
clipping/contrast/SNR gate. PHD2 ended guiding after progressive low-SNR losses at
02:18:08. The first fresh G3 triage frame at 02:18:15 already had 16 coherent
stellar sources with median SNR 95.76; the second at 02:18:33 had 11 with median
SNR 122.70. Mount readback changed by about one arcsecond. The old path nevertheless
started a whole-frame G3/WCS/neighbour reacquisition and exhausted its original
handoff reserve at 02:19:41.

The earlier `HD 25627` and `HD 25868` losses show the same pattern: completed ATR
data was retained, but a short guide-star loss forced full G3 reacquisition and
then failed on the original return/action budget.

## Changes

1. A completed ATR frame whose spectrum passes clipping/contrast/SNR checks is
   retained even if guide continuity changed during exposure/readout. It carries
   `ATR_FRAME_GUIDE_CONTINUITY_WARNING`. A new exposure still requires fresh,
   settled PHD2 and target/slit verification.
2. Passive same-lock recovery is now independent of the single in-place check
   allowance. It waits on the owner’s current state without selection, calibration
   or motion.
3. If two fresh G3 frames prove a usable field, the runner captures and verifies a
   fresh current target/slit field locally, asks PHD2 to reselect/settle, and
   measures a new target/slit residual.
4. The durable PHD2 lineage is translated into the new guide epoch: only the
   origin-current endpoint vector is shifted. Attempts, cumulative pixels, exposure
   credit, time clock and return obligation are preserved.
5. Ordinary local no-candidate, repeated guide-loss and PHD2 timeout outcomes only
   end that bounded local attempt. They no longer escape as an unclassified
   whole-run exception.

Hard stops remain for changed connection/ownership, outstanding motion, genuine
missing mount pulse output, ambiguous target/companion identity, weather, operator
cancellation, and unconfirmed physical stop.

## Verification and deployment

- Release .NET: 5,058 passed, 0 failed
- Python: 354 passed, 68 environment-dependent skips
- Ruff: passed
- frozen design hashes: passed
- backup: `output/deployment-backups/0347-20261110T025945Z`
- installed artifact: 65/65 files match by relative path and SHA-256
- installed plugin DLL SHA-256:
  `EE9CE7E5DF13685473678C7A45D497DD04DFE1915FF3EC0A51F7C5FC518E119B`
- N.I.N.A. PID `19276`; bridge Idle; no run; real mode not selected;
  real control unarmed; `uiError` empty
- startup log contains no ERROR/FATAL/XAML/Binding/unhandled-exception entry

No hardware connection, exposure, motion, PHD2 restart, or observation was started.
Real-sky confirmation of the next intermittent-cloud window remains pending.
