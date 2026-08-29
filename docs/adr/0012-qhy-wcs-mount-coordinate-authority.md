# ADR-0012: Fresh QHY WCS as absolute coordinate authority for a no-home mount

**Status:** Accepted; supersedes the no-QHY-mount-command default in ADR-0008 and baseline sections 5.1, 5.2 and 7 only for coordinate Sync recovery
**Date:** 2026-08-29
**Decision owners:** Observatory owner and OpenAstroSpec Auto project

## Context

The ClearSky ST25 (non-Pro) installation has neither a mechanical home reference
nor absolute encoders. After a complete power interruption the operator can place
the mount approximately and declare a zero, but the resulting ASCOM-reported
right ascension and declination are not an independent measurement of the sky.

During the 2026-08-29 run, the formal QHY/GS350 and G3 PlateSolve3 solutions
agreed to about 4.8 arcmin while both differed from the target/mount hint by about
19 degrees. Treating that 19-degree residual as a two-optical-axis offset would
corrupt configuration and still leave subsequent GoTo semantics wrong. QHY at
350 mm has a large, well-populated field and its formal PL3 result is the strongest
available absolute-coordinate observation for this installation.

ADR-0008 correctly retained QHY as a no-motion witness in the ordinary route and
made fresh G3 WCS the authority for optical arrival. It did not address recovering
the coordinate system of a mount which has no repeatable home after power loss.

## Decision

For this commissioned no-mechanical-home mount, a current-run QHY accepted frame
and formal PL3 solution are absolute sky-coordinate authority when all of the
following remain true:

- the QHY camera, frame, solve, action configuration, commissioning preset and
  current run identities are exact and their SHA-256 evidence is unchanged;
- dual-ended capture readbacks show that the mount did not move;
- the mount remains connected, unparked, not slewing and not pulse guiding;
- coordinate epoch and a known pier side remain unchanged.

The fresh QHY WCS always becomes the preferred hint for the immediately following
G3 PL3 solve at that unchanged pointing. If QHY WCS and the mount-reported
coordinate differ by no more than the configured G3 sky-hint trust radius, no
coordinate mutation is made. If they differ by more, the coordinator may issue
exactly one `ITelescopeMediator.Sync(QhyWcs)` in the observation run. It writes a
durable intent first, sends no Slew, and must verify a connected, stationary,
same-pier readback within 5 arcsec of QHY WCS. Rejection, exception, failed
readback, or a second gross mismatch pauses recovery rather than repeating Sync.
After that readback succeeds, the coordinator reissues the planned catalogue
slew exactly once through the N.I.N.A. mediator, applies the ordinary horizon and
immediate action gates, waits for the commissioned stable readback, and requires
the next fresh G3 WCS to prove optical arrival. This is a retry of the original
catalogue slew whose absolute coordinate basis was invalid, not QHY-derived
fine motion or an unbounded search loop.

The trust radius is never enlarged from the observed mount/target residual. Only
the difference between nearly simultaneous, same-pointing QHY and G3 WCS centres
is a two-optical-axis measurement. Those pairs are archived automatically under
an installation/hardware fingerprint and remain `MotionAuthority=false` until a
separate multi-sample activation process exists.

After a successful coordinate Sync, N.I.N.A. remains the sole owner of telescope
commands and fresh G3 WCS remains the authority for coarse optical arrival and
slit acquisition. QHY does not issue slews and does not repeatedly overwrite
mount coordinates during ordinary operation.

## Consequences

### Positive

- A power-cycled ST25 can recover meaningful absolute coordinates without a
  manual 19-degree configuration change; the catalogue slew is then repeated
  from a measured coordinate basis and verified by fresh G3 WCS.
- QHY/G3 optical-axis separation and mount/target coordinate error are no longer
  conflated.
- A threshold and one-shot latch prevent normal few-arcminute optical-axis offset
  from causing QHY/G3 coordinate oscillation.
- Every coordinate mutation has immutable source evidence, a pre-command intent
  and post-command readback.

### Costs and risks

- The OnStep ASCOM driver must implement coordinate Sync correctly.
- A false formal QHY solve would corrupt the mount coordinate, so no local
  morphology guess or unbound/stale solve may enter this route.
- The first implementation uses the existing G3 sky-hint trust radius as the
  gross-mismatch trigger; changing it affects both plausibility and Sync trigger
  semantics and is therefore action-configuration provenance.

## Unchanged decisions

- Camera/device process ownership from ADR-0001 is unchanged.
- QHY does not become a telescope command owner; the N.I.N.A. mediator executes
  the single coordinate mutation.
- ADR-0008 fresh-G3 post-move verification remains required.
- QHY-to-G3 Candidate records still cannot authorize pre-positioning motion.
