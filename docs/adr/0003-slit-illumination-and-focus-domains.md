# ADR-0003: Measured slit illumination and three independent focus domains

**Status:** Accepted  
**Date:** 2026-08-17  
**Decision owners:** Observatory owner and UVEX-ADV project

## Context

The motorized UVEX4 contains a white slit-positioning LED controlled by the
documented serial commands `SLON` and `SLOF`. The LED and photodiode are the
UVEX `IST0` bit-5 slit-detector capability; they are not a Calibrex relay and
must not be addressed through `CACT`.

The observatory also has three physically different focus mechanisms whose
effects must not be conflated:

| Focus domain | Mechanism | Primary evidence |
|---|---|---|
| C11 focal plane and G3 slit-field image | Gemini focuser | G3M2210M stellar shape, slit throughput and target-placement stability |
| GS350 wide-field acquisition/photometry path | ToupTek focuser | QHYminiCam8M star FWHM/HFR and plate-solve quality |
| UVEX internal spectral image | UVEX M2 motor over COM5 | ATR585M spectral-line FWHM and extraction quality |

A single generic N.I.N.A. focuser selection cannot safely stand for all three.
Moving the wrong mechanism can make one camera look better while degrading a
different optical path.

The current observatory binding evidence is:

| Focus role | Local software identity | Physical binding note |
|---|---|---|
| C11 / G3 main-telescope focus | `ASCOM.StarFocuserPro.Focuser` | The operator identifies the physical unit as Gemini; it is currently configured on COM8. The driver name itself does not prove the Gemini brand. |
| GS350 / QHY wide-field focus | `ASCOM.ToupTek.AAF` | Windows reports an `AUTOFOCUSER` with VID/PID `0547:14AD`. It has no trustworthy serial number, so the commissioned USB topology must also match. |
| UVEX / ATR spectral focus | `UvexAdv.Service` M2 | COM5 is owned exclusively by the UVEX service; no ASCOM focuser identity is involved. |

Historical focus positions are seeds, not current measurements. In particular,
an old successful C11 autofocus result near 4758 steps does not authorize a
movement in a later Night Setup.

## Decision

### Slit geometry

- `UvexAdv.Service` remains the sole owner of COM5 and is the only process that
  may issue `SLON` or `SLOF`.
- A slit-geometry commissioning measurement uses immutable, paired G3 frames:
  LED off and LED on, with the same camera settings and no intervening mount,
  slit-wheel or focus motion.
- Slit illumination is fixed in G3 detector coordinates. Whole frames are not
  shifted to align drifting stars, because that would move and broaden the
  physical slit signal. Estimated star drift may only drive a transient-star
  mask or a quality flag; detector-fixed robust composites remain the geometry
  input.
- Geometry is measured from the robust LED-on minus LED-off signal. The saved
  PHD2 overlay may seed the search but can never by itself become commissioned
  geometry.
- The product records slit center/locus, angle, illuminated width, uncertainty,
  contrast, camera identity, binning, UVEX slit position and source-frame
  hashes. Low contrast, saturation, multiple plausible loci or changed device
  state yields `PausedNeedsAttention`.
- The LED is switched off in `finally` cleanup on success, pause, cancel,
  takeover, service shutdown and communication failure. A run cannot proceed
  to ordinary acquisition until an explicit off-state command has been
  acknowledged or the uncertainty has been surfaced.

### Focus ownership

- Night Setup and every run manifest bind the three focus domains separately,
  including stable device identity, starting position, allowed range, maximum
  single move, maximum cumulative move, approach direction/backlash policy and
  evidence metric.
- G3 star defocus is a C11/Gemini-focus symptom. It must not trigger a UVEX M2
  movement.
- QHY/GS350 plate-solve or photometry defocus is a ToupTek-focus symptom. It
  must not trigger Gemini or UVEX M2 movement.
- ATR spectral-line width is the metric for UVEX M2. It must not trigger either
  external telescope focuser.
- Each automatic focus loop is independently commissioned, bounded and
  reversible. An unavailable or ambiguous focuser identity pauses that loop;
  the coordinator never substitutes another focuser.
- A passing focus or slit-geometry gate advances automatically in accordance
  with ADR-0002. No routine confirmation dialog is added.

## Consequences

- Slit placement is based on an observed physical locus rather than a remembered
  overlay, which removes a major source of target-placement uncertainty.
- The system can diagnose whether poor G3, QHY or ATR sharpness belongs to the
  telescope, guide-scope or spectrograph optical path.
- The N.I.N.A. plugin needs explicit adapters or stable bindings for Gemini and
  ToupTek focusers instead of assuming the active `IFocuserMediator` represents
  both.
- Commissioning requires three independent focus response measurements and a
  paired LED slit measurement before the corresponding automatic motions are
  enabled.

## Relationship to earlier decisions

ADR-0001 remains authoritative for sole device ownership, and ADR-0002 remains
authoritative for automatic progression with operator pause. This ADR adds the
physical slit evidence source and focus-domain ownership that those decisions
did not previously specify.
