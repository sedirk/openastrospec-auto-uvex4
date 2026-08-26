# ADR-0008: Fresh-G3 authority for large acquisition corrections

**Status:** Accepted; supersedes the default QHY-motion route described by ADR-0004 and baseline sections 5.1, 5.2 and 7
**Date:** 2026-08-27
**Decision owners:** Observatory owner and OpenAstroSpec Auto project

## Context

Two unattended on-sky runs (Mirfak and Algol) completed the useful route from a
single start: catalogue slew, QHY wide-field witness, fresh G3 solve, direct
N.I.N.A. WCS moves with fresh G3 response checks, PHD2 native star selection and
slit placement, guiding, adaptive ATR acquisition and simultaneous QHY imaging.
The later M76 sparse-field run also proved the deterministic neighbouring-field
fallback: a direct long-exposure G3 solve failed, a 5 arcmin overlapping field
solved, a direct N.I.N.A. correction was made, and a fresh solve confirmed the
target field before a 600 s spectrum.

The production dock still selected an older route in which a QHY solve drove
multiple 600 arcsec mount segments. That route reserved only 2400 arcsec of
cumulative travel, so it could not complete a measured 3782 arcsec residual. It
also reused a 2 arcsec static frame/mount binding tolerance as the endpoint test
for a large slew. A command that stopped 2.12 arcsec from the requested coordinate
was therefore returned to origin even though a fresh G3 solve could safely and
decisively measure the remaining optical error.

The QHY and C11/G3 optical axes are not rigidly identical. A QHY-derived movement
therefore adds another transformation assumption exactly where fresh G3 WCS is
available and has already worked on sky.

## Decision

The production route is:

1. slew to the catalogue coordinate through N.I.N.A.;
2. capture and formally solve a QHY/GS350 wide-field frame, retaining it as an
   immutable no-motion witness;
3. capture a fresh G3 frame through PHD2 and attempt the commissioned exposure
   ladder;
4. when the formal G3 solution places the catalogue target outside the usable
   field, send a bounded large WCS correction through the N.I.N.A. telescope
   mediator;
5. after stable reported coordinates, capture and solve another fresh G3 frame;
   this post-move WCS—not the commanded coordinate—is the optical arrival
   authority;
6. if no direct G3 frame solves, execute the bounded overlapping neighbouring-
   field search, retaining every frame, move and return obligation;
7. after the target is in the usable G3 field, use PHD2 native star selection and
   the separately governed exact slit-placement loop to reach the freshly measured
   black-aperture midpoint;
8. guide continuously while ATR adaptive spectral exposure and QHY time-series
   acquisition run concurrently.

QHY solve failure advances the configured QHY exposure ladder. A physically
plausible formal PlateSolve3 solution is not rejected by a project-defined minimum
source or match count.

### Separate position questions

The implementation keeps three questions separate:

- **Frame binding:** did the mount remain effectively unchanged between a frame
  and the decision that consumes it? This retains the strict 2 arcsec tolerance.
- **Large-command verification eligibility:** did the reported mount endpoint
  remain inside the declared single-move, origin-radius, cumulative-motion,
  horizon, pier-side and stability envelopes? A bounded endpoint residual may
  authorize one fresh verification frame; any unreserved actual movement is
  charged to the durable ledger before that exposure.
- **Optical arrival:** did the fresh post-move G3 WCS show the expected response
  and place the target inside the usable field? Only this can finish coarse
  acquisition.

The wider verification eligibility is not a centering tolerance, slit-placement
tolerance or permission to guide from stale coordinates.

### Compatibility

The old QHY-motion implementation and its versioned limits remain readable for
historical evidence and interrupted-run recovery, but the production UI does not
select it and those legacy values do not block a new observation. ADR-0004 remains
authoritative for an explicitly activated versioned wide-to-slit transfer; its
former statement that QHY motion is the default is superseded here.

Device ownership is unchanged: N.I.N.A. owns ATR585M and telescope commands, PHD2
owns G3M2210M, the QHY service owns QHYminiCam8M, and `UvexAdv.Service` owns COM5.

## Consequences

### Positive

- The production dock matches the route that actually completed unattended sky
  runs instead of a stricter, unproven QHY-motion route.
- Large movement uses the optical train whose next task is slit acquisition and
  proves success with fresh data rather than a command readback.
- Sparse and faint fields retain a finite, evidence-rich neighbouring-field
  fallback.
- Static evidence binding stays strict; widening fresh-frame eligibility does not
  weaken slit-placement or guiding evidence.

### Costs and risks

- G3 must sometimes take longer exposures before the first large correction.
- Very sparse regions can consume the bounded overlapping search budget.
- The legacy QHY-motion code remains maintenance surface until old interrupted
  runs no longer require compatible recovery.

## Evidence

- `docs/commissioning-night-2026-08-24.md` — Mirfak and Algol unattended closed
  loops.
- `docs/commissioning-night-2026-08-25.md` — M76 neighbouring-field solve,
  direct correction, fresh confirmation and 600 s spectrum.
- `docs/design/faint-target-and-sparse-field-acquisition.md` — bounded sparse-field
  strategy.

