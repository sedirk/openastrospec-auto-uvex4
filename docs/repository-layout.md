# Repository layout and implementation status

OpenAstroSpec — UVEX4 is a single GPL-3.0-only repository containing two deliberately separated
products: **OpenAstroSpec Auto — UVEX4** for equipment-side acquisition/control in C#/.NET 8,
and **OpenAstroSpec Spectral Studio — UVEX4** for offline spectral reduction in Python 3.11. The
machine-readable product boundaries live under `products/`.

## Equipment-side solution

| Path | Responsibility | Current status |
|---|---|---|
| `src/UvexAdv.Protocol` | Public UVEX serial commands, frames, parser and transport contract | Implemented with unit tests |
| `src/UvexAdv.Core` | Device state, leases, limits, operations and calibration records | Implemented with unit tests |
| `src/UvexAdv.Service` | Single-owner COM5 Windows service, loopback API, simulator, serial transport, telemetry and persistence | Implemented; real motion remains subject to commissioning |
| `src/UvexAdv.Admin` | Standalone WPF manager using only the service API | Implemented |
| `src/UvexAdv.Reduction.Launcher` | Native desktop launcher for the isolated Python reduction studio | Implemented |
| `src/UvexAdv.Spectroscopy` | Managed extraction and focus/wavelength closed-loop algorithms | Prototype implemented with synthetic tests |
| `src/UvexAdv.Nina.Plugin` | N.I.N.A. dock panels, explicit simulator/real workflow, live QHY/G3/ATR diagnostics, ATR capture and advanced-sequence integration | Implemented; requires installed N.I.N.A. 3.2 assemblies to compile |
| `src/UvexAdv.Qhy.Core` / `src/UvexAdv.Qhy.Service` | Isolated QHY acquisition/photometry owner, FITS/evidence, metrics, filter-wheel attestation, jobs and leases | Implemented with simulator/native adapters |
| `src/UvexAdv.Phd2` / `src/UvexAdv.Phd2.Watchdog` | PHD2 event client, binding/calibration gates, evidence capture, guiding/settle and independent safety lease | Implemented with fake-server tests |
| `src/UvexAdv.Observatory` | Observation plan/state machine, horizon/motion gates, image analysis, manifests and commissioning models | Implemented; real use remains commissioning-gated |
| `tests/` | Hardware-independent protocol, service, analysis, orchestration and UI-contract tests | Offline/simulator tests |

The canonical accepted acquisition requirements remain in
`docs/design/observatory-automation-baseline.md`. Implemented code does not imply that a
particular installation has completed commissioning.

## Spectral Studio product

`reduction/` is an independently packaged Python application. It reads FITS files and writes new products; it does not open COM5, cameras, the mount, PHD2 or N.I.N.A. Its pinned environment is intentionally isolated because ASPIRED 0.5.1/RASCAL 0.3.10 require an older NumPy stack.

- `reduction/src/uvex_reduce/`: reusable pipeline and desktop studio;
- `reduction/tests/`: synthetic and product-behavior tests;
- `reduction/configs/`: reproducible processing configurations;
- `reduction/tools/`: dataset-specific audits and report helpers;
- `reduction/docs/`: reduction SOPs and scientific reports;
- `reduction/output/`: generated local results, ignored by Git.

## Versioned versus local content

Versioned:

- source, tests and safe configuration examples;
- installation/build scripts;
- architecture decisions, commissioning records and SOP source documents;
- small documentation images needed to explain the UI.

Local and ignored:

- raw or repaired FITS/other camera data;
- calibration libraries, `.astroproj` projects and reduction products;
- diagnostic plots and generated PDFs;
- SDK/runtime bundles, virtual environments, `bin`, `obj` and publish artifacts;
- logs, SQLite databases, credentials and effective machine configuration.

The ignored files are not disposable. In particular, observation data remain on their existing storage paths and must be backed up separately; Git is only the software/design history.

## Build expectations

The observatory Windows machine is the authoritative full-build environment because it has N.I.N.A. 3.2 installed. The hosted CI workflow runs hardware-independent .NET test projects and the Python suite. It intentionally does not pretend to validate the N.I.N.A. plugin against absent local application assemblies.
