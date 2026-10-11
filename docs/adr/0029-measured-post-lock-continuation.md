# ADR-0029 — Measured post-lock continuation, not another native settle gate

- Status: Accepted
- Date: 2026-10-11
- Authorization: the owner explicitly approved replacing post-lock native settle
  timeouts with warnings and fresh same-session measurements, retaining real
  star loss, missing correction output, unknown target identity, unconfirmed stop
  and motion limits as blockers.
- Supersedes: ADR-0028's native post-lock completion requirement and ADR-0015's
  supervision-only restriction **for verified runtime exact-lock continuation**.
  Initial guide/calibration acquisition and other ownership/safety rules remain.

## Decision

After a locally verified exact-lock change PHD2 is already guiding. In both
standalone and native-sequence operation, retain this session and spend the
unchanged stage deadline on at least three fresh immutable guide frames rather
than issuing another `guide` RPC and waiting for its smoothed settle statistic.
This extends the existing read-only post-lock implementation to qualified runs.
No successful `SettleDone` is synthesized. Preserve the original native result,
and distinguish measured continuation from native success in evidence/metadata.

Each frame retains same-epoch/lock provenance, a new GuideStep, its immutable
SHA-256 and actual guide/target/slit geometry. Actual target identity and final
target-on-slit tolerance remain independent requirements. The 3 pixel optical
guide precision value remains advisory (ADR-0028). An unaccepted baseline or two
frames confer no authority. Fresh validated continuation may be qualified, but
does not imply excellent calibration or precise slit placement. Before science,
the existing fresh target/slit window is still mandatory.

Return uses the unchanged positional arrival envelope and original elapsed,
distance and attempt budget. Three consecutive immutable frames with frame-bound
PHD2 measured centroids must confirm actual star return; a changed lock coordinate
alone cannot clear debt. Its positional receipt uses the existing return circle
(at least 3 pixels), not a smoothed native distance or five-second settle timer.
If position, output, identity or freshness cannot be proved, retain the return
debt, checked-stop and use the terminal archival policy; do not relabel timeout
as arrival, resend movement or replenish budgets. This is not a new science gate.

Initial guide/calibration native settle remains separate. No permission is added
to override StarLost, failed output, a changed owner, pending work, mount/UVEX
limits, weather safety, final slit position or unconfirmed device stop.

## Related implementation corrections

The commissioned atomic `capture_single_frame` can emit one stop notification
while its owner was already stopped. Recognize that notification only inside
the outstanding local atomic request with unchanged stopped connection/guide
identity, selection, lock and calibration. Still require successful native
completion, correct saved path and final stopped readback. External transitions,
multiple stop notifications and material configuration changes still invalidate.
This preserves an existing stop receipt; it does not adopt an external session.

Overlay labels distinguish `PREDICTED / 未实测` (dashed yellow) from measured
`TARGET` (solid red). Prediction is not a measurement and brightest-star
appearance is not catalogue identity. Raw FITS are never modified.

## Verification

Replay local atomic completion with stop events and adverse intervening events;
test three-frame provenance, old hashes, lost/changed epochs, missing centroids,
failed output and non-arrival. Test qualified post-lock grading while native
failure remains unchanged. Native initial settle and exact target/slit acceptance
remain covered by their existing regressions. Installation and replay cannot
claim a successful real-sky target.
