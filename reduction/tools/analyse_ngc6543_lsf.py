from __future__ import annotations

import csv
import json
from pathlib import Path

from astropy.io import fits
import matplotlib.pyplot as plt
import numpy as np
from scipy.optimize import least_squares

from uvex_reduce.lsf import ReplicaKernelAnchor, remove_wavelength_dependent_left_replica


ROOT = Path(__file__).resolve().parents[1]
INPUT = (
    ROOT
    / "output"
    / "_internal"
    / "runs"
    / "20260512-sharpcap"
    / "ngc6543-final"
    / "final"
    / "NGC6543_calibrated_1d.fits"
)
OUTPUT = INPUT.parent / "lsf-diagnostic"
LINE_ANALYSIS = INPUT.parent / "NGC6543_line_analysis.json"

LINE_DEFINITIONS = [
    ("H-gamma", 4340.47),
    ("H-beta", 4861.35),
    ("[O III] 4959", 4958.91),
    ("[O III] 5007", 5006.84),
    ("He I 5876", 5875.62),
    ("He I 6678", 6678.15),
    ("He I 7065", 7065.19),
    ("[Ar III] 7136", 7135.79),
]

# Only the repeatable blue/green shoulder is used for inversion.  The red-line
# decompositions are weaker or degenerate and remain measurements, not kernel
# anchors.
ANCHOR_LABELS = {
    "H-gamma",
    "H-beta",
    "[O III] 4959",
    "[O III] 5007",
}


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    primary_header, columns = _read_input(INPUT)
    pixel = columns["PIXEL"]
    wavelength = columns["WAVELENGTH"]
    raw_flux = columns["RAW_FLUX"]
    raw_uncertainty = columns["RAW_UNCERTAINTY"]
    relative_flux = columns["RELATIVE_FLUX"]
    relative_uncertainty = columns["RELATIVE_UNCERTAINTY"]
    continuum = columns["CONTINUUM"]
    line_analysis = json.loads(LINE_ANALYSIS.read_text(encoding="utf-8"))

    fits_by_line = [
        _fit_asymmetric_line(pixel, wavelength, raw_flux, raw_uncertainty, label, laboratory)
        for label, laboratory in LINE_DEFINITIONS
    ]
    anchors = _anchors_from_fits(fits_by_line, pixel, wavelength)

    raw_result = remove_wavelength_dependent_left_replica(
        raw_flux,
        anchors,
        raw_uncertainty,
    )
    relative_result = remove_wavelength_dependent_left_replica(
        relative_flux,
        anchors,
        relative_uncertainty,
    )
    corrected_normalized = np.divide(
        relative_result.corrected,
        continuum,
        out=np.full_like(relative_result.corrected, np.nan),
        where=np.isfinite(continuum) & (continuum != 0),
    )
    inverse_validation = _validate_inverse(
        pixel,
        raw_flux,
        raw_result.corrected,
        fits_by_line,
    )
    flux_reallocation_validation = _validate_flux_reallocation(
        wavelength,
        columns["NORMALIZED_FLUX"],
        corrected_normalized,
    )

    csv_path = OUTPUT / "NGC6543_asymmetric_lsf_diagnostic.csv"
    _write_csv(
        csv_path,
        pixel,
        wavelength,
        columns["RAW_FLUX"],
        raw_result.corrected,
        raw_result.uncertainty,
        columns["NORMALIZED_FLUX"],
        corrected_normalized,
        raw_result.offset_pixels,
        raw_result.secondary_to_primary,
    )
    fits_path = OUTPUT / "NGC6543_asymmetric_lsf_diagnostic.fits"
    _write_fits(
        fits_path,
        primary_header,
        pixel,
        wavelength,
        columns,
        raw_result.corrected,
        raw_result.uncertainty,
        relative_result.corrected,
        relative_result.uncertainty,
        corrected_normalized,
        raw_result.offset_pixels,
        raw_result.secondary_to_primary,
    )
    plot_path = OUTPUT / "NGC6543_asymmetric_lsf_diagnostic.png"
    _write_plot(
        plot_path,
        wavelength,
        columns["NORMALIZED_FLUX"],
        corrected_normalized,
    )
    overview_plot_path = (
        OUTPUT / "NGC6543_line_diagnostics_corrected_experimental.png"
    )
    _write_full_spectrum_plot(
        overview_plot_path,
        wavelength,
        columns["NORMALIZED_FLUX"],
        corrected_normalized,
        np.asarray(columns["MASK"], dtype=bool),
        line_analysis.get("measurements", []),
        _optional_header_float(primary_header, "ORD2STRT"),
    )
    corrected_only_plot_path = (
        OUTPUT / "NGC6543_line_diagnostics_corrected_only_experimental.png"
    )
    _write_full_spectrum_plot(
        corrected_only_plot_path,
        wavelength,
        columns["NORMALIZED_FLUX"],
        corrected_normalized,
        np.asarray(columns["MASK"], dtype=bool),
        line_analysis.get("measurements", []),
        _optional_header_float(primary_header, "ORD2STRT"),
        show_original=False,
    )
    readme_path = OUTPUT / "README.md"
    _write_readme(
        readme_path,
        plot_path,
        overview_plot_path,
        corrected_only_plot_path,
        fits_path,
        csv_path,
    )
    json_path = OUTPUT / "NGC6543_lsf_assessment.json"
    _write_json(
        json_path,
        fits_by_line,
        anchors,
        raw_result,
        raw_uncertainty,
        inverse_validation,
        flux_reallocation_validation,
        plot_path,
        overview_plot_path,
        corrected_only_plot_path,
        fits_path,
        csv_path,
    )
    print(
        json.dumps(
            {
                "fits": str(fits_path),
                "csv": str(csv_path),
                "profileComparisonPng": str(plot_path),
                "correctedLineOverviewPng": str(overview_plot_path),
                "correctedOnlyLineDiagnosticsPng": str(corrected_only_plot_path),
                "readme": str(readme_path),
                "json": str(json_path),
            },
            indent=2,
        )
    )


def _read_input(path: Path) -> tuple[fits.Header, dict[str, np.ndarray]]:
    with fits.open(path, memmap=False) as hdul:
        header = hdul[0].header.copy()
        table = hdul["SPECTRUM"].data
        columns = {name: np.asarray(table[name]).copy() for name in table.names}
    return header, columns


def _fit_asymmetric_line(
    pixel: np.ndarray,
    wavelength: np.ndarray,
    flux: np.ndarray,
    uncertainty: np.ndarray,
    label: str,
    laboratory: float,
) -> dict[str, float | str | bool]:
    center_index = int(np.nanargmin(np.abs(wavelength - laboratory)))
    selected = (
        (pixel >= pixel[center_index] - 28)
        & (pixel <= pixel[center_index] + 28)
        & np.isfinite(flux)
        & np.isfinite(uncertainty)
    )
    x = pixel[selected] - pixel[center_index]
    y = flux[selected]
    sigma_y = np.maximum(uncertainty[selected], np.nanmedian(uncertainty[selected]))
    edge = np.abs(x) > 20
    slope, intercept = np.polyfit(x[edge], y[edge], 1)
    residual = y - intercept - slope * x
    peak = int(np.nanargmax(residual))
    amplitude = max(float(residual[peak]), 1.0)
    center = float(np.clip(x[peak], -8.0, 8.0))

    single = least_squares(
        lambda value: (_single_gaussian(value, x) - y) / sigma_y,
        [intercept, slope, amplitude, center, 6.0],
        bounds=([-np.inf, -np.inf, 0, -8, 1], [np.inf, np.inf, np.inf, 8, 15]),
        loss="soft_l1",
    ).x
    double = least_squares(
        lambda value: (_left_replica_gaussian(value, x) - y) / sigma_y,
        [
            single[0],
            single[1],
            max(single[2], 1.0),
            min(single[3] + 1.5, 7.5),
            max(2.0, single[4] * 0.7),
            0.3,
            9.0,
            max(2.0, single[4] * 0.75),
        ],
        bounds=(
            [-np.inf, -np.inf, 0, -8, 1, 0.005, 2, 1],
            [np.inf, np.inf, np.inf, 8, 15, 1.5, 20, 20],
        ),
        loss="soft_l1",
        max_nfev=5000,
    ).x
    single_chi2 = float(np.sum(np.square((_single_gaussian(single, x) - y) / sigma_y)))
    double_chi2 = float(np.sum(np.square((_left_replica_gaussian(double, x) - y) / sigma_y)))
    sample_count = int(x.size)
    single_bic = sample_count * np.log(max(single_chi2 / sample_count, 1e-30)) + 5 * np.log(sample_count)
    double_bic = sample_count * np.log(max(double_chi2 / sample_count, 1e-30)) + 8 * np.log(sample_count)
    dispersion = float(np.nanmedian(np.diff(wavelength[selected])))
    area_ratio = float(double[5] * double[7] / double[4])
    return {
        "label": label,
        "laboratoryAngstrom": laboratory,
        "coordinatePixel": float(pixel[center_index]),
        "offsetPixels": float(double[6]),
        "offsetAngstrom": float(double[6] * dispersion),
        "secondaryPeakToPrimary": float(double[5]),
        "secondaryAreaToPrimary": area_ratio,
        "mainSigmaPixels": float(double[4]),
        "secondarySigmaPixels": float(double[7]),
        "mainCenterOffsetPixels": float(double[3]),
        "deltaBicSingleMinusReplica": float(single_bic - double_bic),
        "usedAsKernelAnchor": label in ANCHOR_LABELS,
    }


def _single_gaussian(parameters: np.ndarray, x: np.ndarray) -> np.ndarray:
    intercept, slope, amplitude, center, sigma = parameters
    return intercept + slope * x + amplitude * np.exp(-0.5 * np.square((x - center) / sigma))


def _left_replica_gaussian(parameters: np.ndarray, x: np.ndarray) -> np.ndarray:
    intercept, slope, amplitude, center, sigma, ratio, offset, secondary_sigma = parameters
    primary = amplitude * np.exp(-0.5 * np.square((x - center) / sigma))
    secondary = amplitude * ratio * np.exp(
        -0.5 * np.square((x - (center - offset)) / secondary_sigma)
    )
    return intercept + slope * x + primary + secondary


def _anchors_from_fits(
    fits_by_line: list[dict[str, float | str | bool]],
    pixel: np.ndarray,
    wavelength: np.ndarray,
) -> list[ReplicaKernelAnchor]:
    anchors: list[ReplicaKernelAnchor] = []
    for item in fits_by_line:
        if not item["usedAsKernelAnchor"]:
            continue
        area_ratio = float(item["secondaryAreaToPrimary"])
        # The correction is intentionally bounded.  An empirical component
        # larger than this is more likely a model degeneracy than a safe kernel.
        bounded_ratio = float(np.clip(area_ratio, 0.03, 0.65))
        anchors.append(
            ReplicaKernelAnchor(
                coordinate_pixel=float(item["coordinatePixel"]),
                offset_pixels=float(item["offsetPixels"]),
                secondary_to_primary=bounded_ratio,
                secondary_blur_sigma_pixels=float(
                    np.sqrt(
                        max(
                            float(item["secondarySigmaPixels"]) ** 2
                            - float(item["mainSigmaPixels"]) ** 2,
                            0.0,
                        )
                    )
                ),
            )
        )
    # The isolated red lines are already close to symmetric and do not support
    # the same decomposition.  Taper the blue/green correction to exactly zero
    # before He I 5876 instead of extrapolating an unvalidated kernel.
    taper_pixel = float(pixel[int(np.nanargmin(np.abs(wavelength - 5600.0)))])
    last = anchors[-1]
    anchors.append(
        ReplicaKernelAnchor(
            coordinate_pixel=taper_pixel,
            offset_pixels=last.offset_pixels,
            secondary_to_primary=0.0,
            secondary_blur_sigma_pixels=last.secondary_blur_sigma_pixels,
        )
    )
    return anchors


def _write_csv(
    path: Path,
    pixel: np.ndarray,
    wavelength: np.ndarray,
    original: np.ndarray,
    corrected: np.ndarray,
    uncertainty: np.ndarray | None,
    original_normalized: np.ndarray,
    corrected_normalized: np.ndarray,
    offset: np.ndarray,
    ratio: np.ndarray,
) -> None:
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "pixel",
                "wavelength_angstrom",
                "original_raw_flux",
                "diagnostic_corrected_raw_flux",
                "diagnostic_corrected_uncertainty",
                "original_normalized_flux",
                "diagnostic_corrected_normalized_flux",
                "kernel_offset_pixels",
                "kernel_secondary_to_primary",
            ]
        )
        errors = np.full_like(corrected, np.nan) if uncertainty is None else uncertainty
        writer.writerows(zip(pixel, wavelength, original, corrected, errors, original_normalized, corrected_normalized, offset, ratio))


def _write_fits(
    path: Path,
    source_header: fits.Header,
    pixel: np.ndarray,
    wavelength: np.ndarray,
    original: dict[str, np.ndarray],
    corrected_raw: np.ndarray,
    corrected_raw_uncertainty: np.ndarray | None,
    corrected_relative: np.ndarray,
    corrected_relative_uncertainty: np.ndarray | None,
    corrected_normalized: np.ndarray,
    offset: np.ndarray,
    ratio: np.ndarray,
) -> None:
    header = source_header.copy()
    header["DIAGONLY"] = (True, "Not a canonical science product")
    header["LSFVALID"] = (False, "Physical LSF kernel not lamp-validated")
    header["LSFSTAT"] = ("EXPERIM", "Blue/green experimental correction")
    header["LSFMODE"] = ("SHIFTREP", "Empirical shifted-replica inverse")
    header.add_history("UVEX-ADV diagnostic LSF correction; original product remains authoritative")
    raw_errors = np.full_like(corrected_raw, np.nan) if corrected_raw_uncertainty is None else corrected_raw_uncertainty
    relative_errors = np.full_like(corrected_relative, np.nan) if corrected_relative_uncertainty is None else corrected_relative_uncertainty
    table = fits.BinTableHDU.from_columns(
        [
            fits.Column(name="PIXEL", format="D", array=pixel),
            fits.Column(name="WAVELENGTH", format="D", unit="Angstrom", array=wavelength),
            fits.Column(name="ORIGINAL_RAW_FLUX", format="D", array=original["RAW_FLUX"]),
            fits.Column(name="CORRECTED_RAW_FLUX", format="D", array=corrected_raw),
            fits.Column(name="CORRECTED_RAW_UNCERTAINTY", format="D", array=raw_errors),
            fits.Column(name="ORIGINAL_RELATIVE_FLUX", format="D", array=original["RELATIVE_FLUX"]),
            fits.Column(name="CORRECTED_RELATIVE_FLUX", format="D", array=corrected_relative),
            fits.Column(name="CORRECTED_RELATIVE_UNCERTAINTY", format="D", array=relative_errors),
            fits.Column(name="ORIGINAL_NORMALIZED_FLUX", format="D", array=original["NORMALIZED_FLUX"]),
            fits.Column(name="CORRECTED_NORMALIZED_FLUX", format="D", array=corrected_normalized),
            fits.Column(name="KERNEL_OFFSET", format="D", unit="pixel", array=offset),
            fits.Column(name="KERNEL_RATIO", format="D", array=ratio),
            fits.Column(name="MASK", format="L", array=original["MASK"]),
        ],
        name="LSF_DIAGNOSTIC",
    )
    hdul = fits.HDUList([fits.PrimaryHDU(header=header), table])
    hdul.writeto(path, overwrite=True, checksum=True)
    with fits.open(path) as verify:
        verify.verify("exception")


def _write_plot(
    path: Path,
    wavelength: np.ndarray,
    original_normalized: np.ndarray,
    corrected_normalized: np.ndarray,
) -> None:
    figure, axes = plt.subplots(4, 2, figsize=(13, 13), constrained_layout=True)
    for axis, (label, laboratory) in zip(axes.flat, LINE_DEFINITIONS):
        selected = np.abs(wavelength - laboratory) <= 25
        original = original_normalized[selected]
        corrected = corrected_normalized[selected]
        local_scale = max(np.nanmax(original) - np.nanmedian(original), 1e-12)
        axis.plot(wavelength[selected], (original - np.nanmedian(original)) / local_scale, color="#667085", lw=1.4, label="Original")
        axis.plot(
            wavelength[selected],
            (corrected - np.nanmedian(corrected)) / local_scale,
            color="#137c8b",
            lw=1.4,
            label="Recovered primary (replica flux reallocated)",
        )
        axis.axvline(laboratory, color="#d97706", ls="--", lw=0.8)
        axis.set_title(label)
        axis.set_xlabel("Air wavelength (Å)")
        axis.set_ylabel("Local scaled profile")
        axis.grid(alpha=0.2)
    axes.flat[0].legend(loc="best")
    figure.suptitle(
        "NGC 6543 — blue/green asymmetric-LSF correction (EXPERIMENTAL)"
    )
    figure.savefig(path, dpi=170)
    plt.close(figure)


def _write_full_spectrum_plot(
    path: Path,
    wavelength: np.ndarray,
    original_normalized: np.ndarray,
    corrected_normalized: np.ndarray,
    mask: np.ndarray,
    measurements: list[dict[str, object]],
    second_order_start: float | None,
    *,
    show_original: bool = True,
) -> None:
    """Write the missing whole-spectrum counterpart of the line diagnostic.

    The original trace is retained as a quiet reference so the plot shows what
    the inverse actually changed.  This remains a diagnostic product: the
    canonical spectrum is intentionally not overwritten.
    """

    figure, axes = plt.subplots(2, 1, figsize=(18, 10), constrained_layout=True)
    ranges = ((4200.0, 5150.0), (5700.0, min(7200.0, float(wavelength[-1]))))
    for axis, (low, high) in zip(axes, ranges):
        selected = (
            (wavelength >= low)
            & (wavelength <= high)
            & ~mask
            & np.isfinite(original_normalized)
            & np.isfinite(corrected_normalized)
        )
        local_wavelength = wavelength[selected]
        original = original_normalized[selected]
        corrected = corrected_normalized[selected]
        if show_original:
            axis.plot(
                local_wavelength,
                original,
                lw=0.85,
                color="#667085",
                alpha=0.72,
                label="Original canonical spectrum",
            )
        axis.plot(
            local_wavelength,
            corrected,
            lw=1.05,
            color="#137c8b",
            label="Recovered primary (experimental)",
        )
        axis.axhline(1.0, color="0.45", lw=0.8)
        combined = np.concatenate((original, corrected)) if show_original else corrected
        upper = max(3.0, float(np.nanpercentile(combined, 99.7)) * 1.15)
        low_percentile = float(np.nanpercentile(combined, 0.1))
        lower = max(-0.15 * upper, min(0.0, 1.1 * low_percentile))
        axis.set_ylim(lower, upper)
        axis.set_xlim(low, high)
        for line in measurements:
            if not bool(line.get("detected", False)):
                continue
            laboratory = float(line["laboratory_air_angstrom"])
            if not low <= laboratory <= high:
                continue
            colour = "#d97706" if bool(line.get("second_order_risk", False)) else "#b91c1c"
            axis.axvline(laboratory, color=colour, lw=0.7, alpha=0.65)
            axis.text(
                laboratory,
                lower + 0.96 * (upper - lower),
                str(line["label"]),
                rotation=90,
                va="top",
                ha="right",
                fontsize=8,
                color=colour,
            )
        if second_order_start is not None and high >= second_order_start:
            axis.axvspan(
                second_order_start,
                high,
                color="#f59e0b",
                alpha=0.10,
                label="second-order risk (retained)",
            )
        axis.grid(alpha=0.2)
        axis.set_ylabel("Continuum-normalized relative flux")
    axes[0].legend(loc="upper left")
    axes[1].legend(loc="upper left")
    axes[-1].set_xlabel("Air wavelength (Angstrom)")
    title = (
        "NGC 6543 — line overview after replica-flux reallocation (EXPERIMENTAL)"
        if show_original
        else "NGC 6543 — corrected emission-line diagnostics (EXPERIMENTAL)"
    )
    figure.suptitle(
        title
        + "\nInverse active in the blue/green and tapered to zero by 5600 Å; "
        "red spectrum unchanged"
    )
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _optional_header_float(header: fits.Header, key: str) -> float | None:
    value = header.get(key)
    if value is None:
        return None
    parsed = float(value)
    return parsed if np.isfinite(parsed) else None


def _write_readme(
    path: Path,
    profile_plot: Path,
    overview_plot: Path,
    corrected_only_plot: Path,
    fits_path: Path,
    csv_path: Path,
) -> None:
    path.write_text(
        "# NGC 6543 experimental asymmetric-LSF correction\n\n"
        "This directory does not replace the canonical reduced spectrum.\n\n"
        "The forward model is `observed = (primary + r * shifted_blurred(primary)) / (1 + r)`. "
        "The inverse therefore reallocates the modelled replica flux back into the recovered "
        "primary profile; it does not merely delete the shoulder, and it does not copy-add the "
        "observed shoulder (which would double-count flux).\n\n"
        f"- `{overview_plot.name}`: whole-spectrum original/corrected comparison.\n"
        f"- `{corrected_only_plot.name}`: corrected-only whole-spectrum line diagnostic.\n"
        f"- `{profile_plot.name}`: local line-profile comparisons.\n"
        f"- `{fits_path.name}`: original and corrected arrays with propagated uncertainty.\n"
        f"- `{csv_path.name}`: the same diagnostic arrays in tabular form.\n\n"
        "The correction is active only in the blue/green and tapers to zero by 5600 Angstrom. "
        "Its numerical gates pass, but the physical kernel has not yet been validated with a "
        "narrow-line lamp/slit-width experiment. Do not use it for intrinsic line-shape or "
        "radial-velocity measurements yet.\n",
        encoding="utf-8",
    )


def _write_json(
    path: Path,
    fits_by_line: list[dict[str, float | str | bool]],
    anchors: list[ReplicaKernelAnchor],
    result,
    original_uncertainty: np.ndarray,
    inverse_validation: list[dict[str, float | str | bool]],
    flux_reallocation_validation: dict[str, float | list[float]],
    plot_path: Path,
    overview_plot_path: Path,
    corrected_only_plot_path: Path,
    fits_path: Path,
    csv_path: Path,
) -> None:
    payload = {
        "schemaVersion": 1,
        "status": "experimental-passed-numerical-gates-not-physical-validation",
        "interpretation": "wavelength-dependent asymmetric instrumental line-spread function",
        "physicalCauseConfirmed": False,
        "model": "normalized primary plus a shifted lower-pixel replica; finite Neumann inverse reallocates modelled replica flux into the recovered primary",
        "lineFits": fits_by_line,
        "kernelAnchors": [
            {
                "coordinatePixel": anchor.coordinate_pixel,
                "offsetPixels": anchor.offset_pixels,
                "secondaryAreaToPrimary": anchor.secondary_to_primary,
                "secondaryBlurSigmaPixels": anchor.secondary_blur_sigma_pixels,
            }
            for anchor in anchors
        ],
        "inverseValidation": inverse_validation,
        "fluxReallocationValidation": flux_reallocation_validation,
        "noiseAmplificationMedianAllPixels": float(
            np.nanmedian(result.uncertainty / original_uncertainty)
        )
        if result.uncertainty is not None
        else None,
        "noiseAmplificationMedianWhereCorrectionActive": float(
            np.nanmedian(
                (result.uncertainty / original_uncertainty)[
                    result.secondary_to_primary > 0
                ]
            )
        )
        if result.uncertainty is not None
        else None,
        "warnings": [
            "The cylindrical-lens/camera-window reflection path is not proven.",
            "The fitted secondary is too strong to be explained by a normal AR-coated two-surface ghost alone.",
            "The measured f/7.71 beam rejects the earlier severe f/6.7 cone-mismatch hypothesis.",
            "CCDT67 remains an unproven upstream A/B variable through chromatic/field aberration or slit illumination, not a confirmed detector-side ghost.",
            "Do not use the corrected spectrum for velocity or intrinsic line-profile science until a narrow-line lamp and slit-width test validate the kernel.",
            "Correction is tapered to zero by 5600 Angstrom; the red spectrum is deliberately unchanged.",
            "The inverse reallocates modelled replica flux; it neither deletes the shoulder nor copy-adds the observed shoulder.",
            "The canonical NGC6543 product was not modified.",
        ],
        "products": {
            "fits": str(fits_path),
            "csv": str(csv_path),
            "profileComparisonPng": str(plot_path),
            "correctedLineOverviewPng": str(overview_plot_path),
            "correctedOnlyLineDiagnosticsPng": str(corrected_only_plot_path),
        },
    }
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def _validate_flux_reallocation(
    wavelength: np.ndarray,
    original_normalized: np.ndarray,
    corrected_normalized: np.ndarray,
) -> dict[str, float | list[float]]:
    # The broad blue/green window contains all four fitted anchors.  Integrating
    # the continuum-subtracted spectrum over the whole region is a better test
    # of flux reallocation than a narrow window that a shifted replica can
    # partially cross.
    low, high = 4200.0, 5150.0
    selected = (
        (wavelength >= low)
        & (wavelength <= high)
        & np.isfinite(original_normalized)
        & np.isfinite(corrected_normalized)
    )
    original_excess = float(
        np.trapz(original_normalized[selected] - 1.0, wavelength[selected])
    )
    corrected_excess = float(
        np.trapz(corrected_normalized[selected] - 1.0, wavelength[selected])
    )
    red = (
        (wavelength >= 5600.0)
        & np.isfinite(original_normalized)
        & np.isfinite(corrected_normalized)
    )
    return {
        "blueGreenWindowAngstrom": [low, high],
        "continuumBaseline": 1.0,
        "originalIntegratedExcess": original_excess,
        "correctedIntegratedExcess": corrected_excess,
        "correctedToOriginalIntegratedExcessRatio": corrected_excess
        / max(abs(original_excess), 1e-12),
        "maximumAbsoluteChangeAtOrAbove5600Angstrom": float(
            np.nanmax(np.abs(corrected_normalized[red] - original_normalized[red]))
        ),
    }


def _validate_inverse(
    pixel: np.ndarray,
    original: np.ndarray,
    corrected: np.ndarray,
    fits_by_line: list[dict[str, float | str | bool]],
) -> list[dict[str, float | str | bool]]:
    checks: list[dict[str, float | str | bool]] = []
    for item in fits_by_line:
        if not item["usedAsKernelAnchor"]:
            continue
        reference = float(item["coordinatePixel"])
        selected = np.abs(pixel - reference) <= 28
        local_pixel = pixel[selected] - reference
        edge = np.abs(local_pixel) > 21
        original_continuum = np.polyval(
            np.polyfit(local_pixel[edge], original[selected][edge], 1),
            local_pixel,
        )
        corrected_continuum = np.polyval(
            np.polyfit(local_pixel[edge], corrected[selected][edge], 1),
            local_pixel,
        )
        original_line = original[selected] - original_continuum
        corrected_line = corrected[selected] - corrected_continuum
        peak = max(float(np.nanmax(original_line)), 1e-12)
        main_center = float(item["mainCenterOffsetPixels"])
        near_main = (local_pixel >= main_center - 3.5) & (local_pixel <= main_center + 0.5)
        minimum_fraction = float(np.nanmin(corrected_line[near_main]) / peak)
        original_metrics = _profile_metrics(local_pixel, original_line)
        corrected_metrics = _profile_metrics(local_pixel, corrected_line)
        checks.append(
            {
                "label": str(item["label"]),
                "minimumCorrectedFluxNearMainAsOriginalPeakFraction": minimum_fraction,
                "passedNoNegativeTrough": minimum_fraction >= -0.03,
                "originalAsymmetry": original_metrics["asymmetry"],
                "correctedAsymmetry": corrected_metrics["asymmetry"],
                "originalFwhmPixels": original_metrics["fwhmPixels"],
                "correctedFwhmPixels": corrected_metrics["fwhmPixels"],
                "integratedFluxRatioCorrectedToOriginal": float(
                    corrected_metrics["integratedFlux"]
                    / max(original_metrics["integratedFlux"], 1e-12)
                ),
            }
        )
    return checks


def _profile_metrics(pixel: np.ndarray, line: np.ndarray) -> dict[str, float]:
    peak_index = int(np.nanargmax(line))
    lower = max(0, peak_index - 15)
    upper = min(line.size, peak_index + 16)
    left = float(np.sum(np.maximum(line[lower:peak_index], 0.0)))
    right = float(np.sum(np.maximum(line[peak_index + 1 : upper], 0.0)))
    asymmetry = (left - right) / max(left + right, 1e-12)
    above_half = np.flatnonzero(line >= 0.5 * line[peak_index])
    fwhm = float(pixel[above_half[-1]] - pixel[above_half[0]]) if above_half.size else np.nan
    integrated = float(np.trapz(line[np.abs(pixel) <= 20], pixel[np.abs(pixel) <= 20]))
    return {
        "asymmetry": asymmetry,
        "fwhmPixels": fwhm,
        "integratedFlux": integrated,
    }


if __name__ == "__main__":
    main()
