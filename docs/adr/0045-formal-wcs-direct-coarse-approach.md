# ADR-0045 — Direct coarse approach from a formal current-image WCS

- Status: Accepted by the owner, 2026-10-26. Offline validation and installation
  do not constitute on-sky acceptance.
- Authority: explicit approval to remove mandatory overlap from reliable formal
  WCS corrections while retaining the original movement/return/time budgets.
- Narrowly supersedes ADR-0043's universal coarse-correction overlap cap; does
  not change ownership, physical limits, uncertainty labelling or final acceptance.

## Evidence and decision

The October 25 UTC / October 26 local mid0 run obtained useful formal solutions,
but initial offsets of approximately 1,400–2,000 arcseconds were divided into
120–330 arcsecond overlap-preserving moves. Acquisition used most of the original
action allowance before reaching the target. A useful recovery mechanism had
become a mandatory detour even when the current raw image had a formal solution.

A successful formal solve of the **current G3 image**, with the existing verified
source, pointing and device binding, may approach the desired target/slit position
directly, capped by the original maximum single correction. It need not detour
through an off-frame neighbour or preserve a common rectangle with the next image.
The existing full-path feasibility, cumulative distance, action count, original
return reserve, horizon, physical-stop, weather and cancellation checks still
precede execution. A planner's direct vector grants no motion by itself.

Registered/mosaic/local-dual inferred WCS remains explicitly inferred, even when
its numeric result has `Success=true`. It and blind neighbouring-field search
still preserve uncertainty-aware overlap. A formal solution of a derived mosaic
does **not** qualify as a formal solution of the current raw frame. Unknown solve
authority cannot remove the overlap cap. Existing fresh measured target evidence
may hand off within the original fine-motion envelope; it cannot authorize an
unmeasured long jump.

After any move, the existing new-image response/arrival validation remains
mandatory. Failure at the destination uses the original bounded acquisition,
return and skip-target path; it does not allocate extra moves or restart a clock.
No commanded position, successful driver acknowledgement or predicted centroid
is re-labelled measured slit arrival or accepted science.

## Related implementation corrections, not new authority

- A continuous QHY acquisition owner supplies available witnesses; an optional
  local-dual fallback must not start a competing job when no fresh pose is ready.
  That optional miss must leave the already captured G3 image available to solve.
- Registered G3 displacement already measures the actual image motion. Include
  independent image-fit uncertainty and a one-pixel floor at each endpoint;
  do not add mount-coordinate arrival tolerance again as optical displacement.
- A newly accepted independent short-frame pair can carry current measured
  position with its own bounded age and drift uncertainty. The old catalogue
  association must have been validated while still applicable; all old parents,
  both new frames and confirmation receipt remain hash-checked. Do not rewrite
  the old WCS expiry, invent a centroid, or waive component/multi-lobe rejection.
- Native target visibility checks use the remaining original target deadline,
  not a rolling target duration plus another search duration. Current horizon
  and original night/dawn cutoffs remain independent vetoes.

## Verification, deployment and rollback

Test formal versus inferred authority, unchanged single and round-trip limits,
fresh-frame handoff, stale/corrupt/changed context and cancellation. Replay the
mid0 raw frames and historical successful controls without modifying inputs.
Execute the next real native target container after quality failure and checked
cleanup, with unsafe and unknown-stop counterexamples. Report historical pixels,
finite-port simulation and live acceptance separately; do not invent images from
a correction that was not taken historically.

Run full release checks, back up the exact plugin and Profile, verify a safe idle
boundary, and install/restart normally. Installation does not start observation,
connect another camera owner, move equipment or extend the night cutoff. Rollback
restores the backed-up plugin at the same idle boundary; raw data and original
budget ledgers remain untouched.
