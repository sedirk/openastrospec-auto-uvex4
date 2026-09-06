# ADR-0013: Explicit supervised ATR probing with slit-precision warnings

**Status:** Accepted by the observatory owner on 2026-09-06. Supersedes only the unconditional quality veto in baseline section 5.3(6), and the corresponding transient-settle/residual rejection in ADR-0005, for the explicitly authorized supervised path below.

## Context

Coastal wind and image motion can make individual guide frames exceed a 2 px target/midpoint tolerance even when a known target remains close to the measured slit. Stopping and rebuilding the entire acquisition for each short excursion can waste positioning actions and interrupt useful native guiding. On 2026-09-06 the owner explicitly approved warning-after-threshold ATR probing, with usability decided from the actual spectrum, without claiming exact placement.

## Decision

- Strict acceptance remains the default. A separate process-local consent, visible in N.I.N.A., permits supervised probing with slit-precision warnings. It resets on restart, is frozen into the run configuration hash, and cannot be changed on an active run. The backend invokes the same visible command and must supply the separate scientific-quality attestation. A general equipment-motion attestation is insufficient.
- Native guide/settle timeouts and measured tracking excursions are quality warnings when fresh, immutable, same-epoch target/slit/guide evidence remains valid. Preserve the native failed result. Do not tighten the physical slit tolerance merely because of that warning.
- Normal bounded placement still approaches the fresh slit midpoint. Probe entry requires at least three actually measured frames, confirmed identity/topology, an authorized current guide/lock state, all samples within the commissioned acquisition envelope, and a median residual within the original target tolerance plus original residual-growth allowance. This is a **probe eligibility envelope**, not a new definition of exact placement. A denied safety/identity/epoch plan cannot become probe authority.
- Record a warning result rather than `TARGET_AT_SLIT_MIDPOINT` when the full window does not meet precise acceptance. A readback-verified lock endpoint with fresh optical evidence may be recorded as a settled *motion accounting* endpoint for probing, retaining all counters, origins, timestamps and positions. It proves neither exact slit placement nor return to the original lock. No pending or ambiguous command may be cleared by this rule.
- Before each ATR exposure, obtain fresh same-epoch measured target/slit evidence. An authorized precision excursion within the commissioned acquisition envelope remains a warning; missing identity, stale evidence, guide loss, topology changes, unconfirmed locks and explicit unsafe device states still prevent a new exposure.
- ATR probe selection and science-frame acceptance retain actual spectral-trace clipping, target/sky contrast and SNR gates. Preserve every rejected frame. An empty or unusable spectrum is not success. FITS and manifests retain the precision-warning flag, measured pre-exposure residual and native settle outcome; no unattended-quality claim is granted.
- Read-only wind recovery uses the original time budget minus its worst-case return reserve, not an arbitrary four-window cutoff. Movement remains bounded by the unchanged per-stage, total, attempt, elapsed-time and return constraints.

### Post-lock observation instead of repeated native settling (0.4.0.138)

For the already-established, explicitly supervised outbound fine-lock path, the
verified exact-lock readback may be followed by read-only GuideStep observation
and at least three distinct fresh target/slit residual frames. PHD2 continues
guiding; another native `guide`/settle RPC is not required at each microstep.
The original stage deadline, verified current lock and connection/guide epochs
remain binding. Pause, loss, disconnection or a pending competing settle revoke
this continuation rather than becoming quality-only warnings.

This is a separately typed `ReadOnlyPostLockWindow`, not a native settle result.
It always caps authority at `DegradedSupervised`, retains the original native
outcome and requires the usual pre-ATR fresh optical verification. New FITS label
the source with `PHD2EV`; logs record that no new guide command or synthetic
`SettleDone` occurred. The separate precision-warning consent is still necessary
for spectra outside strict slit acceptance. Initial guiding, recalibration,
strict-mode outbound steps and return recovery retain their native settle path.
No global notification suppression, threshold increase or device-ownership change
is introduced.

## Consequences and rollback

This permits useful supervised spectra despite transient slit losses, but throughput may vary and flux calibration can be degraded. Reduction must retain those warnings. Strict midpoint results and warning-authorized spectra are distinct outcomes. Removing session consent restores strict acceptance for subsequent runs; it does not relabel existing data. Device ownership, roof/mount/UVEX safety, raw-data immutability and the single visible production route are unchanged.
