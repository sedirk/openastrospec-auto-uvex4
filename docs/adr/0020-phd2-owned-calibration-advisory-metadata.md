# ADR-0020: PHD2 owns guiding calibration; coordinator metadata is advisory

- Status: Accepted
- Date: 2026-10-08
- Authorization: the owner explicitly requested that PHD2 completely manage guiding calibration and that the plugin's duplicate calibration thresholds become warnings.
- Supersedes: calibration grading, selection and forced-renewal requirements in ADR-0005 and baseline §§5.2, 5.3, 12–13. The remaining ADR-0005 operational and motion requirements remain in force.

## Context

The plugin read active PHD2 calibration and native original-time/guide-rate metadata, then independently rejected age, orthogonality, rates, parity or historical provenance. The 03:35 recovery readback had a 31.21-day age and a native/current guide-rate mismatch (about 7.52 versus 15.04 arcsec/s). These diagnostics do not by themselves establish failure of current manual guiding. They also are not evidence that a newly performed manual calibration conflicts with a second plugin calibration file.

## Decision

1. Production calibration ownership is PHD2. The coordinator neither imports its own calibration nor writes PHD2 calibration/guide-rate settings. All production `guide` requests use `recalibrate=false`. Plugin-triggered geometry/age renewal is removed.
2. Calibration age, original timestamp, axis angle/orthogonality, rate ranges/ratios, native-versus-current guide-rate mismatch, parity, and historical calibration-process/topology/pier-side provenance are diagnostic warnings. They cannot reject otherwise current operational evidence or reduce motion scale. Historical schema/policy fields remain readable for provenance and compatibility; they do not become a new calibration authority.
3. Readback still must belong to the expected current PHD2 Profile and connection/calibration epoch. Equipment ownership and sensor topology checks are unchanged. A missing calibration is not a valid lock-shift authority.
4. On a new motion-free acquisition, one ordinary PHD2 guide operation may let PHD2 perform its own calibration if needed. The client accepts only operation-bound native start/terminal events and preserves native failure. Observed calibration invalidates pre-calibration G3 geometry: checked stop and bounded fresh acquisition are required before placement. The reacquisition cannot open another calibration cycle.
5. An outstanding or inherited lock-motion lineage cannot authorize new calibration motion. Native calibration is not permitted during return/recovery; absence or unexpected calibration stops the operation without changing the old ledger. Ordinary guiding with an available PHD2 calibration may continue despite advisory metadata.
6. Calibration-quality labels on owner-managed assessments describe **operational evidence**, not a claim that the calibration is excellent. Calibration diagnostics are retained separately as warnings. Actual operation-bound settle/fresh guiding windows, guide output, star loss, target identity, measured target/slit residual, current pier/sensor identity, movement limits and return reservations remain authoritative.
7. No warning grants an exposure or movement outside the normal authorized observing workflow. Software replay/install is not a real-sky acceptance run. No production device settings or raw observations are modified by this change.

## Verification

Test owner-managed aged/poor-geometry/incomplete metadata without forcing recalibration; unsupported native export; PHD2 reuse, native calibration success/failure, absent calibration with and without a motion-free boundary; and unchanged identity, epoch, stale/far residual, topology and movement-ledger rejections. Verify both observation frontends share the same production runner. Install only at the existing idle/cancellation boundary.

## Consequences

PHD2's own settings and calibration warnings are the operator's calibration management surface. A bad PHD2 calibration can still produce poor guiding; the coordinator must detect that through actual guide output and fresh optical evidence rather than reimplement calibration quality. Old observations and legacy serialized assessments keep their original semantics; only new production readbacks use the owner-managed policy.
