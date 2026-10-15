# ADR-0039: Rapid maintenance restart for an explicitly requested next round

Status: Accepted by explicit owner instruction on 2026-10-15.

Supersedes only the repeated on-site-supervision confirmation in the maintenance
practice of ADR-0017 / ADR-0033 when the current request explicitly includes both
the repair/restart and immediate initiation of the next observing round.

The owner accepts the brief software-monitor interruption in this particular
workflow and requests no repeated supervision question. This is not a claim that
a short interruption is risk-free, nor a new autonomous runtime restart loop.

- Prepare and test the exact artifact and startup procedure before shutdown.
  Confirm the terminal acquisition/motion boundary, retain recovery records,
  back up the Profile/plugin, exit normally and minimize the interruption.
- After restart, verify the artifact, owner identities, selected safety monitor
  and current weather/roof state. Start only through the ordinary validated native
  sequence with its original cutoff/dawn protection. An expired cutoff must not
  roll into another day merely because maintenance took longer than expected.
- A busy owner, unknown stop, unavailable/unsafe monitor or unknown roof state
  still prevents starting. No force-kill, artificial Safe state, disabled limits,
  synthetic closed-roof receipt or startup with skipped validation is permitted.
- Installation-only and “do not observe” requests still end without observing.
  Ordinary blocker maintenance does not acquire standing permission for exposure,
  opening the roof, resuming a sequence or starting a new round.
- Record actual protection gaps and the outcome. A failed restart/start is not
  evidence that the former independent standby remains active. No new hardware
  owner, service restart, firmware change or independent weather watcher is added.

This is a maintainer authorization change, not a change to the runtime safety
chain. Existing weather, cancellation, dawn/cutoff, ownership and native stop/
skip regressions remain required, along with post-install readbacks. Rollback is
an explicit revocation of this narrow preference, not rewriting past evidence.
