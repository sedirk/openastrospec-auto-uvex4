# ADR-0052: Exposure-timed delayed dual-optics calibration

- Status: Accepted
- Date: 2026-10-31
- Supersedes: ADR-0043 / ADR-0044 only for admitting an already captured,
  synchronized formal pair as a historical local-model sample after solver delay.
- Authorization: owner requested the continuous-wide/narrow and cold-start
  implementation, explicit exposure/solve-time treatment, offline validation and
  preservation of the original safety chain and budgets.

## Context

In the latest later1 run the continuous wide camera produced hundreds of accepted
relative measurements and dozens of formal solutions, but the local dual-optics
archive remained empty. The one formal G3 solution arrived after the old 30-second
pair wall clock. That rule conflated simultaneous image acquisition with delayed
astrometric computation. A rolling buffer of relative poses could also evict the
simultaneous formal wide frame before G3 solving completed.

Late computation does not make an old observation current. It can, however, finish
calibrating two images which were captured close together under a proven unchanged
physical setup. Discarding that calibration needlessly prevents cold start.

## Decision

1. Preserve the schema-1 pair contract and hash interpretation. Introduce an
   explicit schema-2 historical-calibration contract; never silently reinterpret
   an old receipt or infer permission from a missing field.
2. Schema 2 records the native single-G3-frame capture bracket and actual exposure
   duration, not `save_image completed - requested exposure`. Exposure midpoint
   uncertainty includes the excess capture-bracket duration. The joint capture
   support remains at most 30 seconds and worst-case midpoint separation remains
   at most 20 seconds. Unknown rolling-preview timing is not exact shutter timing.
   The solve ladder reuses the existing PHD2-owned parameterized native single-frame
   route at its confirmed-Stopped boundary, with the same exposure tier, parameters,
   attempt count and return clock. The native receipt records request dispatch and
   completion. Unsupported native capture, changed owner or uncertain completion
   cannot silently fall back to loop/save and manufacture a timestamp. Other live
   preview/guiding routes are unchanged; historical loop/save frames remain untimed.
3. Preserve solver completion and candidate creation timestamps for provenance,
   but do not use solve latency as a false exposure gap. Historical measurement
   age is derived from acquisition, not from the arrival of the solution.
4. Retain a bounded buffer of 16 formal wide measurements independently of the
   latest 16 relative poses. Selection of a historical partner must verify the
   immutable raw hash, formal WCS, same device owners/connection epochs, geometry,
   focus, pier side and stopped/unchanged-pointing brackets. It must not start
   another camera job, switch owners or treat a relative pose as a formal sample.
5. A delayed pair is calibration/training evidence only. It cannot revive an
   expired live pose, authorize movement, establish current target identity or
   satisfy the current G3 target/slit measurement gate. Local-model applicability,
   independent samples/holdout, drift uncertainty, fresh-image confirmation and
   all existing source-chain checks still apply.
6. Processing is bounded by the existing target cancellation/deadline and reserved
   return time. No motion, exposure, return, attempt or time budget is increased or
   reset. An unavailable optional pair is a recovery miss, not a new night stop.

## Related implementation corrections (not new authority)

- Recentring a detector WCS must retain its complete CD/TAN transform and parity.
  Converting it to a scalar position angle can move the prediction hundreds of
  pixels even when the actual field is already near the slit.
- Registered mosaics and relative tracking must use measured sky sources, not
  fixed detector defects. Preserve source-shape evidence and reject demonstrated
  narrow defects without treating zero image motion itself as an error. A stable
  guided field is valid; too few usable sources means the registration is absent,
  not proof of defocus or permission to invent a transform.

## Verification and limits

Regression must include delayed valid synchronized captures, excessive acquisition
gaps, unknown/overlong native brackets, changed owner/focus/pier/pointing, hash
changes, schema-1 receipts and live-pose expiration. Replay the October 31 records
and retain source hashes. Cover rejected optional recovery followed by an actually
executed next native target after checked cleanup, and failure to skip when stop
or safety is unconfirmed.

This is not a full telescope EKF or star-SLAM implementation. Catalogue WCS anchors,
relative image measurements, mount readback priors and local extrinsics retain
different authority and uncertainty. Delayed-measurement fusion is implemented
where it is supported by recorded evidence; a mount encoder is not independent
proof of sky pointing and a wide-camera pose alone cannot determine an uncalibrated
narrow-camera boresight. New algorithmic fallbacks still require raw-data and
negative-control validation before production promotion.
