# ADR-0031 — Independent science-quality consent and target-local outcomes

- Status: Accepted; real-sky acceptance remains separate.
- Date: 2026-10-11
- Authorization: the owner requested that warning-after-threshold probing work
  with the full safety chain, together with rolling-window and skip-target fixes,
  recorded successful-run regressions, and installation without starting observing.
- Supersedes: only the supervision-mode coupling in ADR-0013/0015 and the
  corresponding baseline 5.3(6) restriction. Ownership, weather and motion policy
  are unchanged. ADR-0027 still governs terminal cleanup and archival.

## Evidence

All nine productive real runs from October 2 through October 9 dawn used the
saved precision-warning probe route. EW Lac's October 9 completion window was
4.5040, 3.3280, 5.1404 pixels against a strict 2-pixel preference, followed by
three accepted 600-second spectra. Full-safety runs retained the saved warning
choice but disabled its effective route because WeakSupervisionEnabled was false.
The header did not expose that difference. Existing source also requested one
rolling sample from a helper that forced three, then called Single().

## Decision

1. The saved, hash-locked AllowSupervisedSlitQualityWarning field is retained for
   Profile compatibility, but its meaning is **explicit scientific-quality
   consent**, independent of environment supervision. It enables the existing
   measured-continuation/probe route in both standalone and native night runs.
   New profiles remain strict. Legacy degraded-science/weak-supervision consent
   remains readable, but does not itself enable precision-warning probes.
2. No safety capability is changed, synthesized or inferred from that consent.
   Full safety still requires the selected live AIWeather/environment chain,
   roof/cover state, horizon, dawn/deadline, ownership and checked physical stop.
   The switch does not enable an unselected guiding mode, acquire devices, grant
   a movement, or start/resume a run. Existing commissioned mode prerequisites,
   actual guide output, immutable identity/geometry and motion limits remain.
3. Preserve the existing three-frame measured finite-slit/radial probe envelope,
   same-session pre-exposure verification and actual ATR spectrum-quality checks.
   Precision or native settle warnings are not exact arrival or successful native
   SettleDone. Real star loss, missing output, changed ownership, stale geometry,
   pending commands and invalid identity cannot become warning-only authority.
   Data retain actual residuals and degraded-quality flags; full *equipment*
   safety is not a claim of unattended *scientific* precision.
4. Full windows acquire at least three frames. Rolling updates acquire exactly
   one new frame, append FIFO to the previous complete same-owner/lock window,
   then validate that complete window. Never validate one frame as a complete
   window, select a favourable subset, or reset deadlines and motion accounting.
   A rejected intervening optical sample breaks consecutiveness: discard the
   retained window and reacquire a complete window within the same deadline.
   Ownership, output and cancellation failures do not use this optical fallback.
5. Target-local optical/quality exhaustion carries an explicit typed scope at
   its producer and retains its original code through wrappers. A saved skip
   policy may advance only after the coordinator/manifest join and independent
   stopped/retired-boundary verification. Legacy known quality codes remain
   compatible; unknown exceptions, identity/ownership/safety failures, cleanup
   failure, cancellation and EndNight are not catch-all skip candidates.
   Notification deferral and the native boundary use the same classification.

## Verification and rollback

Replay sanitized numerical evidence from prior successful runs in strict/warning
and full-safety/weak modes; use the same production policy from both frontends.
Read the original local evidence without modifying it and record its SHA-256 in
ignored replay output. Test the actual one-frame acquisition delegate and complete
window validation. Execute two native target containers to prove continuation,
plus stop/cleanup, cancellation, EndNight, unknown and unsafe negative cases.

Strict rollback is the saved warning switch off for subsequent runs; neither
old observations nor their acceptance labels change. Installation/UI checks are
not an observing run and do not claim new real-sky success.
