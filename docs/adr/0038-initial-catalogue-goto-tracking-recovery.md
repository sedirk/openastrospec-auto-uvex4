# ADR-0038 — One initial catalogue GOTO after tracking timeout

- Status: Accepted; real-sky acceptance remains separate.
- Date: 2026-10-14
- Authorization: the owner explicitly approved this bounded recovery and one new
  observing attempt after the 03:08 tracking failure.
- Supersedes: only the requirement to prove tracking **before the first catalogue
  GOTO**, when one enable request times out and the conditions below all hold.
  Device ownership, driver limits and post-slew verification are unchanged.

## Evidence

Run `UVEX-20261013T190655Z-7244fe8f2af6451` ended before any catalogue GOTO or
exposure. Three requests returned tracking enabled immediately, but N.I.N.A.'s
subsequent polled state remained off. The mount was near the meridian, stationary
and unparked. A controller meridian limit is plausible, not proven. The installed
N.I.N.A. and inspected OnStep driver provide a native catalogue slew without
requiring tracking first; the driver retains its horizon/axis/other limits.

## Decision

1. Keep the one-request, bounded tracking-enable readback. Only its timeout can
   enter this exception; connection, identity, park, unexpected motion, rejection
   and cancellation cannot. Remove the generic timeout retry that only resends
   tracking requests at the same unusable position.
2. Require the initial stage, no optical acquisition or prior correction/return
   state, no inherited clock-recovery budget, no pending meridian transaction,
   full supervision, the original target and unchanged configuration. Confirm
   stopped PHD2 with matching identity, idle cameras/focus, exclusive run ownership
   and a continuous mount pulse-drain window. Never stop an unrelated owner to
   manufacture eligibility. Recheck the full weather, roof/cover, horizon, time,
   selected-device and cancellation chain immediately before dispatch.
3. Persist one initial catalogue intent using exclusive create and disk flush
   **before** movement, for normal initial GOTO as well as this exception. A
   retry, resume, crash or reconstructed runner cannot dispatch it again. Use
   only N.I.N.A.'s normal `SlewToCoordinatesAsync` at the original target, with a
   two-minute bound. N.I.N.A./the driver chooses the pier side and enforces its
   limits. No home, forced flip, sync, reconnect, raw command or limit change.
4. Require native slew completion, stopped same-device readbacks and tracking
   continuously enabled for three seconds within a fifteen-second verification
   window. Do not issue a second tracking request after GOTO. A legitimate
   initial pier-side change is allowed: no previous detector geometry is reused.
   Only then enter the unchanged fresh QHY/G3 solve, slit and guiding pipeline.
   This does not label the target centered or any science frame accepted.
5. Retain intent on rejection, timeout, cancellation, unknown outcome and failed
   post-check. Existing cleanup must independently confirm stopped owners before
   standby/closeout. An untrackable mount is not a mere target-quality miss and
   cannot launch another target as a hidden retry. Night cutoff, dawn, weather,
   all original budgets and saved frames remain unchanged.

## Verification

Exercise the production workflow with a simulated mediator: timeout then native
GOTO and fresh tracking, driver-selected pier change, refusal, cancellation,
command timeout, busy/unsafe/changed owner, rejected tracking request, prior
intent, intent-write failure, unsafe transition after intent, unchanged original
target and failed post-slew tracking. Verify reconstruction cannot repeat motion,
and only verified completion reaches the subsequent acquisition callback. Run
the complete regression and historical successful-observation comparisons before
installation; these do not replace real-sky acceptance.
