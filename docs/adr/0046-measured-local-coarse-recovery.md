# ADR-0046 — One local coarse correction from an independently measured target

- Accepted: owner approval, 2026-10-26.
- Narrowly supersedes ADR-0030's restriction that post-neighbour short-frame
  confirmation is attempted only inside the remaining PHD2 fine envelope.
- Ownership, physical interlocks, budgets and final scientific acceptance remain unchanged.

## Evidence

The latest later0 HD 18552 arrival frame contains a filled bright core 89.40 px
from the measured slit, outside the remaining 44 px PHD2 envelope. Its formal
solve failed. The core alone is not identity; nevertheless, refusing even an
independent short pair makes the previously useful neighbour geometry unreachable.
HD 21455 independently reached its fine envelope but an evidence JSON naming
collision stopped the night. That exception is an implementation defect, not a
reason to weaken the unknown-error or physical-stop boundary.

## Decision

Within the same run, original coarse ledger, owner, detector, focus/configuration,
coordinate epoch and pier side, a formal neighbouring G3 WCS may supply only its
local scale/orientation for **one** measured coarse correction. The anchor must
be at most 120 seconds old and within one degree of the settled field. It cannot
be inferred, recycled after this correction or trained from its own prediction.

The existing recognition circle, capped at 100 px, may admit the long-frame
candidate to the existing maximum-six-frame short confirmation. Both independent
short frames must pass the existing SEP identity/morphology, uniqueness, repeat
and long/short association rules. Resolved companions, multiple lobes, reused
frames and ambiguous identities are not waived. No remaining fine range is
enlarged. A measured pair outside that range can yield a clearly labelled local
pixel/WCS model; it is not a formal solve of this image or proof of slit arrival.

The local model uses the catalogue identity and **measured** current pixel as its
translation anchor, preserving the formal neighbour's scale/orientation. It is
limited to at most 100 px and 40 arcsec (including measured repeat uncertainty),
and inherits the original single/radius/cumulative/action/time and full return
limits. The existing N.I.N.A. coarse planner alone may execute it. Precharge the
one-use counter with the outbound intent, preserve it through return, reload and
family changes, and recheck current binding, source hashes, safety and expiry
immediately before movement. Never issue movement from the short-frame helper.

The source chain includes the original formal image/receipt, long candidate,
both short frames, confirmation receipt and inferred model receipt. The model
expires no later than 30 seconds after the latest short frame or the original
anchor's 120-second limit. It cannot seed another local-model recovery, a formal
dual-camera calibration sample, or an unconfirmed PHD2 handoff. A new destination
image must again establish the target and slit relationship before fine placement.

Failure continues available optical alternatives within the same ledger, then
verified return and saved skip policy. A non-exhausted original solve ladder is
not cut short merely because a candidate exceeds fine range. Weather, cancelled
ownership, invalid source, changed side and unconfirmed stop retain their own
boundaries; no extra time, movement, exposure loop or automatic new run is granted.

## Verification

Replay immutable successful and failed HD 18552/HD 21455 inputs. The absent future
short images in the failed HD 18552 run must not be invented as historical success.
Use explicit finite fixtures for the proposed correction and new-image response,
including repeated-use, stale anchor, ambiguity, changed context, budget reserve,
late safety/cancel and failed-stop negatives. Execute the next native target after
produced quality failure. Run normal release checks and install only at a verified
idle boundary. Installation and offline replay are not on-sky acceptance.
