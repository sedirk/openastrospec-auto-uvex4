# OpenAstroSpec — UVEX4 products

This repository is one source tree with two separately installable products. The
split is by user workflow and hardware authority, not by programming language.

| Product | User-facing purpose | Product definition |
|---|---|---|
| **OpenAstroSpec Auto — UVEX4** | Acquisition, equipment orchestration, commissioning, N.I.N.A. integration, live diagnostics, and immutable observation evidence | [`observatory/README.md`](observatory/README.md) |
| **OpenAstroSpec Spectral Studio — UVEX4** | Offline FITS inspection, reduction, wavelength/response calibration, visualization, and delivery | [`spectral-studio/README.md`](spectral-studio/README.md) |

The Observatory product may command hardware only through the ownership and safety
rules in `AGENTS.md`. Spectral Studio is offline-only and must never open cameras,
COM5, PHD2, a mount, or a roof controller.

Both products are licensed as `GPL-3.0-only` for original project source code.
External applications, drivers, SDKs, scientific dependencies, and observation data
retain their own terms; see [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md).
