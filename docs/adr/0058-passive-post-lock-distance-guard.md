# ADR-0058: Passive verification after a native post-lock distance rejection

Accepted 2026-11-21 by the observatory owner. Supersedes only ADR-0029's
immediate rejection of this one kind of StarLost at a charged outbound step.

On HD 26546 PHD2 found its original primary at SNR 40.64, then rejected
25.87 px against its native 7.40 px distance limit. The previous controller
stopped the guide before its subsequent behaviour could be known. Error 8 is
generic; neither a high SNR, the word “Recovering”, nor a later Guiding flag
identifies this case. See the [native enum](https://github.com/OpenPHDGuiding/phd2/blob/master/src/star.h)
and [distance checker](https://github.com/OpenPHDGuiding/phd2/blob/master/src/guider_multistar.cpp).

- Only a locally dispatched, acknowledged, read-back exact outbound shift with
  durable charged motion and no pending settle is eligible. Require the original
  guide selection, current equipment/profile/calibration and lock. Match its
  native StarLost frame, time, SNR and HFD to the same local PHD2 thread's
  exact command, measured primary and explicit native distance rejection. An
  absent, rotated, unbound or ambiguous trace is not authority.
- Observe native state for at most eight seconds **from that event**, not from
  when the handler happens to run. The native guard may reject subsequent
  consecutive frames during its own waiting state: each needs a separate
  matching distance witness, contiguous native frame and event lineage, and
  no intervening return to Guiding. A missing receipt fails closed. These
  frames never restart the timer. The single waiting episode must end with
  a native return to Guiding and healthy output. No set-lock, guide, settle,
  selection, calibration, motion, exposure start, retry or new budget.
- Once native guiding recovers, use the existing running stream to verify at
  least three new immutable matched primary/target/slit frames. This still has
  to fit the **original** stage, remaining motion time minus reserved return,
  target and night deadlines. Check live binding and safety before acceptance.
  Do not claim native settle, slit arrival or a qualified spectrum from this.
- A new loss, actual low SNR, a different star, missing evidence, cancellation,
  an unsafe condition or a deadline keeps its existing stop and debt. Restart
  does not repeat this attempt. A target-scoped failure may skip only after
  the existing checked stop; an unconfirmed stop still blocks later targets.
