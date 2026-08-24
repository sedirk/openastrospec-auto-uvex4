# ADR-0006: Runtime-measured slit midpoint as the science destination

**Status:** Accepted; supersedes only the nearest-point science-destination rule in ADR-0005  
**Date:** 2026-08-24  
**Decision owners:** Observatory owner and OpenAstroSpec Auto project

## Context

ADR-0005 treated the physical slit as a finite segment and moved the target only
to the nearest point on its centreline. That rule correctly rejected a bright
reflective edge and avoided using one historical detector pixel as the slit.
However, it also allowed a target at either end of the usable slit to complete
placement even when a fresh frame measured a reliable geometric midpoint.

On 2026-08-24, a live Deneb acquisition demonstrated the scientific cost. The
target was admitted at the left end of the dark aperture, but the observatory
owner identified that position as undesirable because the middle of the
spectrograph entrance slit generally has lower optical asymmetry and aberration.
A subsequent bounded PHD2 exact-lock correction placed the saturated-wing
centroid `1.03 px` from the freshly measured midpoint before acquisition and
`1.56 px` after the ATR/QHY exposure block.

The distinction is important: "runtime midpoint" is not a fixed calibration
point. It is the `AcquisitionPoint` produced by the current detector-fixed dark
aperture measurement after the OFF/ON/OFF illumination sequence. The finite
length remains essential for rejecting reflections, edge artifacts, guide-star
overlap and impossible geometry, but it need not define the desired science
position.

## Decision

The default science destination is the **freshly measured geometric midpoint of
the physical dark slit aperture**.

- Both PHD2-calibration-guided exact-lock placement and the independently
  commissioned `G3PixelToMount` fallback use the same runtime
  `SlitGeometry.AcquisitionPoint`.
- Completion is measured as the Euclidean detector residual from the fresh
  target centroid to that midpoint. Merely intersecting the finite centreline,
  including either endpoint, is not completion.
- The finite slit length and width remain authoritative geometry for slit
  identity, dark-aperture detection, exclusion/guard regions and diagnostics.
- A historical seed, reflective ridge, bright edge, fixed detector coordinate
  or remembered position cannot replace the fresh midpoint.
- Every bounded stage still requires operation-bound settle and a fresh G3
  target/midpoint residual. Lock readback alone is not optical proof.
- If the midpoint cannot be measured reliably, automation pauses or uses an
  explicitly commissioned degraded policy; it does not silently fall back to
  the nearest endpoint.

The target-to-lock equation remains:

```text
desiredGuideLock = guideCentroid
                 + (runtimeSlitMidpoint - targetCentroid)
```

## Consequences

### Positive

- Science acquisition is reproducible at the optically preferred part of the
  entrance slit instead of at an arbitrary along-slit location.
- PHD2 and independent-transform paths now share one destination and one
  residual definition.
- Fresh slit recognition still absorbs detector rotation, small installation
  shifts and illumination geometry changes without compiling a site pixel into
  source.

### Costs and risks

- A target already passing light through an endpoint may require additional
  along-slit motion and fresh validation.
- The total correction can be longer than a cross-slit-only correction, so the
  existing single-stage, cumulative, attempt, elapsed-time and return-reserve
  limits remain mandatory.
- A biased midpoint detector can introduce along-slit error. The existing dark
  aperture contrast, width, angle, identity and commissioning-envelope gates
  must pass before it becomes motion authority.

## Relationship to earlier decisions

ADR-0005 remains authoritative for PHD2 ownership, calibration grading,
direct-target degradation, exact-lock staging and fresh residual evidence. This
ADR supersedes only its paragraph selecting the nearest point on the finite slit
centreline as the science destination. ADR-0003 remains authoritative for
runtime physical slit measurement and independent focus domains.
