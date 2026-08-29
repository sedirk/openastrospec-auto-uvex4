# Architecture decision records

ADRs record durable architectural choices and their consequences.

- Accepted ADRs are historical records and are not rewritten to make later decisions look inevitable.
- A changed decision is documented by a new numbered ADR whose status says which earlier ADR it supersedes.
- The canonical design baseline may then be updated to reflect the new accepted state.
- Frozen ADRs listed in `docs/design-baseline.sha256` require the explicit design-change process in `CONTRIBUTING.md`.

File names use `NNNN-short-title.md` and statuses are `Proposed`, `Accepted`, `Superseded`, or `Rejected`.

Current accepted decisions:

- [ADR-0001: Single-owner, multi-camera observatory orchestration](0001-single-owner-device-orchestration.md)
- [ADR-0002: Automatic progression with operator pause and intervention](0002-automatic-progression-with-operator-pause.md)
- [ADR-0003: Measured slit illumination and three independent focus domains](0003-slit-illumination-and-focus-domains.md)
- [ADR-0004: Optional, versioned wide-field-to-slit-field transfer](0004-optional-versioned-wide-to-slit-field-transfer.md)
- [ADR-0005: PHD2-calibration-guided slit placement with versioned fallbacks](0005-phd2-calibration-guided-slit-placement.md)
- [ADR-0006: Runtime-measured slit midpoint as the science destination](0006-runtime-slit-midpoint-as-science-destination.md)
- [ADR-0007: N.I.N.A.-native target identity and image provenance](0007-nina-native-target-and-image-provenance.md)
- [ADR-0008: Fresh-G3 authority for large acquisition corrections](0008-g3-first-large-acquisition-corrections.md)
- [ADR-0009: Single production observation route and field-test promotion](0009-single-production-observation-route.md)
- [ADR-0010: N.I.N.A. environment supervision and roll-off-roof lifecycle](0010-nina-environment-supervision-and-rolloff-roof.md)
- [ADR-0011: One official QHY AllInOne installation and no private service SDK copy](0011-qhy-allinone-shared-sdk-installation.md)
- [ADR-0012: Fresh QHY WCS as absolute coordinate authority for a no-home mount](0012-qhy-wcs-mount-coordinate-authority.md)
