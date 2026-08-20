from __future__ import annotations

from pathlib import Path

import matplotlib

matplotlib.use("Agg")
from matplotlib import pyplot as plt
import numpy as np

from .models import ReductionResult


def write_preextraction_diagnostic(
    image: np.ndarray,
    mask: np.ndarray,
    output_dir: str | Path,
    target_name: str,
) -> Path:
    destination = Path(output_dir).expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=True)
    path = destination / f"{_safe_stem(target_name)}_preprocessed.png"
    finite = image[~mask & np.isfinite(image)]
    low, high = np.percentile(finite, [5.0, 99.7]) if finite.size else (0.0, 1.0)
    scale = max((high - low) / 8.0, 1e-6)
    display = np.arcsinh(np.clip((image - low) / scale, 0.0, None))
    work = np.where(mask | ~np.isfinite(image), np.nan, image)
    column_background = np.nanmedian(work, axis=0, keepdims=True)
    spatial_profile = np.nanmedian(work - column_background, axis=1)

    figure, (image_axis, profile_axis) = plt.subplots(
        1,
        2,
        figsize=(15, 7),
        gridspec_kw={"width_ratios": [5, 1]},
        constrained_layout=True,
    )
    image_axis.imshow(display, origin="lower", aspect="auto", cmap="gray", interpolation="nearest")
    image_axis.set(
        title="Preprocessed 2D spectrum (before trace)",
        xlabel="Dispersion pixel",
        ylabel="Spatial pixel",
    )
    profile_axis.plot(spatial_profile, np.arange(image.shape[0]), linewidth=0.8)
    profile_axis.set(title="Robust spatial profile", xlabel="Signal", ylabel="Spatial pixel")
    figure.savefig(path, dpi=160)
    plt.close(figure)
    return path


def write_diagnostics(result: ReductionResult, output_dir: str | Path, target_name: str) -> dict[str, Path]:
    destination = Path(output_dir).expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=True)
    stem = _safe_stem(target_name)
    paths = {
        "trace": destination / f"{stem}_trace_overlay.png",
        "alignment": destination / f"{stem}_alignment.png",
        "wavelength": destination / f"{stem}_wavelength_residuals.png",
        "spectrum": destination / f"{stem}_spectrum.png",
    }
    _plot_trace(result, paths["trace"])
    _plot_alignment(result, paths["alignment"])
    _plot_wavelength(result, paths["wavelength"])
    _plot_spectrum(result, paths["spectrum"])
    return paths


def _plot_trace(result: ReductionResult, path: Path) -> None:
    image = result.image
    finite = image[np.isfinite(image)]
    low, high = np.percentile(finite, [5.0, 99.7]) if finite.size else (0.0, 1.0)
    scale = max((high - low) / 8.0, 1e-6)
    display = np.arcsinh(np.clip((image - low) / scale, 0.0, None))
    figure, axis = plt.subplots(figsize=(14, 7), constrained_layout=True)
    axis.imshow(display, origin="lower", aspect="auto", cmap="gray", interpolation="nearest")
    x = np.arange(result.trace.centers.size)
    half_width = np.maximum(3.0, 1.1775 * result.trace.sigma_pixels)
    axis.plot(x, result.trace.centers, color="#00e5ff", linewidth=1.1, label="trace")
    axis.plot(x, result.trace.centers - half_width, color="#ffca28", linewidth=0.7, alpha=0.8)
    axis.plot(x, result.trace.centers + half_width, color="#ffca28", linewidth=0.7, alpha=0.8)
    axis.set(
        title=f"2D trace overlay — {result.trace.method}",
        xlabel=f"Dispersion pixel ({_direction_label(result)})",
        ylabel="Spatial pixel",
    )
    axis.legend(loc="upper right")
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _plot_alignment(result: ReductionResult, path: Path) -> None:
    labels = [shift.path.name for shift in result.shifts]
    x = np.arange(len(labels))
    figure, axis = plt.subplots(figsize=(10, 5), constrained_layout=True)
    axis.axhline(0.0, color="0.5", linewidth=0.8)
    axis.plot(x, [shift.spatial_pixels for shift in result.shifts], "o-", label="spatial y")
    axis.plot(x, [shift.dispersion_pixels for shift in result.shifts], "s-", label="dispersion x")
    axis.set_xticks(x, labels, rotation=30, ha="right")
    axis.set(ylabel="Applied shift (pixel)", title="Frame alignment shifts")
    axis.legend()
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _plot_wavelength(result: ReductionResult, path: Path) -> None:
    figure, axis = plt.subplots(figsize=(10, 5), constrained_layout=True)
    if result.wavelength is None:
        axis.text(
            0.5,
            0.5,
            "Wavelength calibration skipped\nNo verified arc/line anchors were supplied",
            ha="center",
            va="center",
            fontsize=14,
            transform=axis.transAxes,
        )
        axis.set_axis_off()
    else:
        solution = result.wavelength
        axis.axhline(0.0, color="0.4", linewidth=0.8)
        axis.scatter(solution.matched_pixels, solution.residuals_angstrom, s=28)
        rms_label = (
            f"RMS {solution.rms_angstrom:.3f} Angstrom"
            if np.isfinite(solution.rms_angstrom)
            else "RMS unavailable (two-line fit)"
        )
        axis.set(
            xlabel="Dispersion pixel",
            ylabel="Residual (Angstrom)",
            title=(
                f"Wavelength fit residuals — {rms_label}"
                + (
                    f", template r={solution.template_correlation:.3f}"
                    if solution.template_correlation is not None
                    else ""
                )
            ),
        )
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _plot_spectrum(result: ReductionResult, path: Path) -> None:
    calibrated = result.wavelength is not None
    x = result.wavelength.wavelength_angstrom if calibrated else np.arange(result.flux.size)
    valid = ~result.mask & np.isfinite(result.flux)
    figure, axis = plt.subplots(figsize=(14, 5), constrained_layout=True)
    display_flux = np.where(valid, result.flux, np.nan)
    axis.plot(x, display_flux, color="#1565c0", linewidth=0.8)
    axis.set(
        xlabel=(
            "Wavelength (Angstrom)"
            if calibrated
            else f"Dispersion pixel ({_direction_label(result)}; uncalibrated)"
        ),
        ylabel="Extracted flux (ADU)",
        title=f"Extracted 1D spectrum — {result.extraction_backend}",
    )
    axis.grid(alpha=0.2)
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _safe_stem(value: str) -> str:
    import re

    cleaned = re.sub(r"[^A-Za-z0-9_.-]+", "_", value.strip()).strip("._")
    return cleaned or "uvex"


def _direction_label(result: ReductionResult) -> str:
    if result.wavelength is not None:
        return "blue → red"
    return "blue → red" if result.horizontal_flip_applied else "detector x; orientation unnormalised"
