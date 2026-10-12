# ADR-0032: One meridian handoff across acquisition, placement and spectroscopy

Status: Accepted; source/replay verification and real-sky commissioning are separate.

Date: 2026-10-11

Authorization: the owner requested full meridian-flip integration and continuing
small corrections near/crossing the meridian instead of abandoning the target.

## Decision

Extend ADR-0019's existing native meridian session, not a second observing route.
Native trigger scope and N.I.N.A. timing remain explicit; no profile, mount limit,
roof policy or standalone hardware authority is silently changed.

- Crossing hour angle zero is not a pier-state transition. Same-side corrections
  continue with their current limits, fresh evidence and original budget. Use
  actual reported pointing state and the owner's destination-side prediction;
  do not infer a flip from sky-coordinate sign or set a side through a second
  ASCOM/serial connection. A predicted cross-side slew requests the controlled
  handoff before that slew, rather than spending the old return vector.
- The joined stage checks native timing at safe acquisition/placement checkpoints
  as well as existing ATR frame boundaries. Internal interruption cancels only
  that stage's owner operations; operator cancellation, unsafe and deadline keep
  priority. No normal completed exposure is interrupted just for a flip. Later
  markers bypass old-side work and perform normal owner finalization.
- After all owners join, verify original owner identities, stationary connected
  mount, stopped PHD2 and idle cameras/focuser. Persist the meridian intent before
  any old-side control handle is retired or any mechanical flip is dispatched.
- This narrowly supersedes ADR-0027/baseline section 7's prohibition on retiring
  same-target meridian handles: only the first completed segment of the explicit
  native meridian session may archive authenticated G3/PHD2 control envelopes.
  Archive original bytes/counters/timestamps and an audit; do not assert return
  or erase debt from history. Independent transform/mechanical obligations,
  corrupt manifests and unknown stops are not covered. A pending meridian intent
  survives a crash and prevents replay/automatic adoption by a new run.
- If native timing/destination prediction requires a flip, use the existing
  bounded wait and single native mount command. If an ordinary completed slew
  already reports the other side, verify that side remains stationary/tracking
  over the settling interval and synchronize the dome if applicable. Do not send
  an additional flip or old-side return. Record this as `SideChangeVerified`, not
  a claim that our native flip command ran or optical arrival was proved.
- Both paths start the same target's one remaining fresh production segment:
  new WCS/slit/guide epochs and ATR probe, original accumulated accepted frames,
  attempts, integration and guiding recovery allowance. Each original segment
  ledger remains immutable; only one handoff/two segments are allowed, never an
  unbounded target retry. Night cutoff/dawn and safety reservation continue.
- Without an active supported native trigger, existing failure/checked target
  skip handling remains. Installation does not initiate a flip or observation.

## Verification

Test scheduled handoff from acquisition and placement, same-side continuation,
observed-side handoff with zero additional flip commands, native command failure,
unstable/unknown side, cancellation/unsafe, interrupted journal, authenticated
byte-preserving retirement, no post-request old-stage action and cumulative
frame/attempt accounting. Preserve recorded successful observation regressions.
Actual OnStep limit settings remain authoritative and are not modified by this
change. Native two-side on-sky reacquisition/continuation needs separate evidence.

References: [ASCOM pointing state](https://www.ascom-standards.org/newdocs/ptgstate-faq.html)
and [destination side](https://ascom-standards.org/newdocs/dsop-faq.html);
the local N.I.N.A. 3.2 native trigger/mount mediator source was inspected.
