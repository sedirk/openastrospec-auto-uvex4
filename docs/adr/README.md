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
