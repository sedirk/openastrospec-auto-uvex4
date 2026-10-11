# ADR-0028 — Measured guide/lock residual is advisory

- Status: Accepted
- Date: 2026-10-10
- Authorization: the owner explicitly requested removal of the 2 → 2.5 → 3 pixel staircase, replaced by one 3 pixel warning that does not block.
- Scope: the optical guide-star-to-lock residual during slit placement, not native command completion or target/slit acceptance.

## Decision

Read one complete fresh optical window under the existing deadline. With current guiding output, unchanged connection/guide epoch and lock, confirmed target identity, and measured same-frame guide/target/slit geometry, a guide-to-lock residual greater than 3 pixels produces a warning only. It does not request more precision windows, force calibration, stop the target, or restrict otherwise qualified unattended operation. Exactly 3 pixels does not warn. Keep every original measurement; do not average away failures or describe the warning as precise guiding.

Apply the same distinction in post-settle grading and lock-shift planning. The desired lock is calculated from measured guide + measured slit − measured target, not from the requested lock. The advisory flag applies only to measured guide/lock error, never to target/slit error or catalogue-only projection. It grants no extra motion distance, attempts or elapsed time.

PHD2 remains the sole guide owner and manages calibration. Lost lock, missing output, invalid/stale geometry, changed identity/epoch, pending operations, native move/settle completion, final target/slit acceptance and physical motion envelopes remain independent requirements. A native settle timeout is not rewritten as success. Existing supervised fresh-window alternatives retain their separate conditions.

Legacy commissioning values remain readable for native movement timing and compatibility, but no longer select an optical staircase. Standalone and native-sequence targets call the same implementation. No machine-local preset change is required.

## Verification

Recorded 6 Cep residuals 3.5242, 3.5755 and 4.9679 pixels must pass the measured-position window with a warning; 2, 2.5 and 3 must not warn. Tests retain rejection of absent geometry, stale/changed identity, invalid numbers and unavailable output. Grading must not convert this warning into a supervision-only veto. Planning must use the measured star and preserve acquisition/return budgets. Real-sky completion remains a separate frontend acceptance, not inferred from tests.
