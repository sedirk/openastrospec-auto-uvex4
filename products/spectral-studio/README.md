# OpenAstroSpec Spectral Studio — UVEX4

OpenAstroSpec Spectral Studio — UVEX4 is the **offline post-processing product**. It scans immutable
FITS inputs, shows neutral-grayscale 2D previews, extracts 1D spectra, performs
quality-gated wavelength/response calibration, and writes new products with provenance.

It has no equipment authority: it must not open COM5, cameras, PHD2, a mount, N.I.N.A.,
or roof/weather devices. This separation is deliberate even though both products share
one Git repository.

## Human entry point

Install or launch **OpenAstroSpec 光谱处理 — UVEX4**. The seven-stage workbench includes 2D and 1D
visualizations. Image viewers support mouse-wheel zoom around the cursor, left-button
pan, scrollbars, fit-to-window, and 1:1 display; these display operations never rewrite
the source FITS.

The companion **OpenAstroSpec 观测资料库 (Library)** is a separate offline window
in the same Python runtime: in-place catalogue, metadata/log/product preview,
independent pick/reject/rating/notes, storage summaries and exact-frame handoff
to Studio. It does not duplicate raw files or start reduction automatically.
Projects are linked from acquisition/night journals and existing reduction manifests,
with explicit source paths, scientific roles and product dependencies. Cleanup has
separate mark-only, Windows Recycle Bin and permanent-delete modes: preview the exact
eligible files, then confirm once with a clearly labelled button. Raw FITS, scientific
products and dependencies remain protected; only proven redundant previews from
terminated runs are currently eligible for physical cleanup. Recycling is not counted
as immediately freed disk space. Archive export remains planning-only; no archive
drive is selected or moved to automatically. See the
[library usage guide](../../reduction/README.md) and
[data-lifecycle design](../../docs/design/observation-library-and-data-lifecycle.md).

## Source boundary

- Python package and GUI: `reduction/src/uvex_reduce/`;
- tests: `reduction/tests/`;
- safe example configurations: `reduction/configs/`;
- native Windows launcher: `src/UvexAdv.Reduction.Launcher/`.

The pinned Python 3.11 environment and build/test commands are documented in
[`reduction/README.md`](../../reduction/README.md).

## Release contents

A future GitHub release for this product should contain the launcher, Python source,
`pyproject.toml`, the pinned requirements file, safe example configurations, and the
reduction documentation. Raw observations, `.astroproj` files, calibration libraries,
virtual environments, and generated results are not release assets.
