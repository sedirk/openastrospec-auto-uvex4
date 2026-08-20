# ADR-0001: Single-owner, multi-camera observatory orchestration

**Status:** Accepted / frozen  
**Date:** 2026-08-16  
**Decision owners:** Observatory owner and UVEX-ADV project

## Context

The observing system uses three physically distinct cameras for different simultaneous jobs:

- ATR585M records the spectrum;
- G3M2210M images the UVEX slit field and guides through PHD2;
- QHYminiCam8M on a roughly coaxial GS350 performs coarse acquisition and then simultaneous photometry.

UVEX4 motion is controlled over COM5. An earlier design idea treated PHD2 as a camera broker that would switch between QHYminiCam8M and G3M2210M. That would interrupt guiding, prevent continuous QHY photometry, complicate PHD2 profiles and confuse “one owner per device” with “one process for every auxiliary camera.”

N.I.N.A. must retain ATR585M as the spectral imaging camera, and a camera SDK failure must not take down the whole acquisition stack.

## Decision

Use process-level isolation and a sole owner for each physical device:

| Device | Owner |
|---|---|
| UVEX4 COM5 | `UvexAdv.Service` |
| ATR585M | N.I.N.A. |
| G3M2210M | PHD2 |
| QHYminiCam8M | a dedicated QHY acquisition/photometry service |
| Telescope/mount | N.I.N.A. telescope mediator, orchestrated by the UVEX plugin |

The N.I.N.A. UVEX plugin is the workflow coordinator. It communicates with the UVEX service, PHD2 and the planned QHY service but does not load their camera SDKs or open their devices. Different cameras operate concurrently.

PHD2 is never the normal owner of QHYminiCam8M. It remains connected to G3M2210M during slit acquisition and guiding. The QHY service retains QHYminiCam8M from the first coarse solve through the last simultaneous photometry frame.

## Consequences

### Positive

- ATR spectroscopy, G3 guiding and QHY photometry can run simultaneously.
- A QHY driver or processing failure is isolated from N.I.N.A., PHD2 and COM5 control.
- Camera ownership is simple to audit and test.
- The wide-field acquisition frames become durable target-identity evidence and scientific photometry rather than disposable guide frames.
- The N.I.N.A. plugin remains an orchestration layer compatible with Advanced Sequencer concepts.

### Costs

- A separate QHY service, API, simulator and installer must be built and maintained.
- Cross-process timestamps, health, cancellation and observation identifiers must be coordinated.
- The GS350/QHY-to-G3/slit transformation must be commissioned and periodically verified.
- PHD2 must expose or save enough G3 acquisition information without a second process opening the camera.

### Rejected alternatives

1. **Switch PHD2 between QHY and G3:** rejected because it interrupts both continuous guiding and simultaneous photometry.
2. **Switch N.I.N.A.’s primary camera between ATR and QHY:** rejected because ATR must remain the stable spectral camera throughout the sequence.
3. **Load all vendor SDKs inside the N.I.N.A. plugin:** rejected because it couples driver failures and native-library versions to N.I.N.A.’s process.
4. **Put QHY acquisition inside the COM5 service:** rejected because camera failures and high-volume image processing must not weaken the small, safety-critical UVEX motion service.

## Change policy

This decision is part of the frozen automation baseline. A future change requires the owner’s explicit approval and a new ADR that supersedes ADR-0001. Do not edit this ADR to conceal or retroactively replace the decision.
