"""Build final normalized and identity-check products for the May 4 Albireo pair."""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path

from astropy.io import fits
import matplotlib.pyplot as plt
import numpy as np
from scipy.ndimage import gaussian_filter1d

from uvex_reduce.calibration import fit_robust_continuum, load_reduced_spectrum
from uvex_reduce.stellar import load_stellar_template


BALMER_LINES = {
    "H-delta": 4101.74,
    "H-gamma": 4340.47,
    "H-beta": 4861.35,
    "H-alpha": 6562.79,
}
TEMPLATE_SMOOTH_SIGMA_PIXELS = 6.0


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--a-fits", type=Path, required=True)
    parser.add_argument("--b-fits", type=Path, required=True)
    parser.add_argument("--a-calibrated-fits", type=Path, required=True)
    parser.add_argument("--a-template", type=Path, required=True)
    parser.add_argument("--b-template", type=Path, required=True)
    parser.add_argument("--baseline-a-fits", type=Path)
    parser.add_argument("--baseline-b-fits", type=Path)
    parser.add_argument("--output-dir", type=Path, required=True)
    return parser


def _normalize_reduced(path: Path) -> dict[str, np.ndarray | fits.Header]:
    spectrum = load_reduced_spectrum(path)
    if not bool(spectrum.header.get("DISPFLIP", False)):
        raise RuntimeError(
            f"Albireo product was not corrected from raw red-left/blue-right orientation: {path}"
        )
    if not np.all(np.diff(spectrum.wavelength_angstrom) > 0):
        raise RuntimeError(f"Albireo wavelength axis is not strictly increasing: {path}")
    continuum = fit_robust_continuum(
        spectrum.wavelength_angstrom,
        spectrum.flux_adu,
        spectrum.mask,
        bin_width_angstrom=120.0,
        percentile=55.0,
    )
    mask = (
        spectrum.mask
        | ~np.isfinite(continuum)
        | (continuum <= 0)
        | ~np.isfinite(spectrum.flux_adu)
    )
    normalized = np.divide(
        spectrum.flux_adu,
        continuum,
        out=np.full_like(spectrum.flux_adu, np.nan),
        where=~mask,
    )
    uncertainty = np.divide(
        spectrum.uncertainty_adu,
        continuum,
        out=np.full_like(spectrum.uncertainty_adu, np.nan),
        where=~mask,
    )
    return {
        "header": spectrum.header,
        "wavelength": spectrum.wavelength_angstrom,
        "flux": spectrum.flux_adu,
        "flux_uncertainty": spectrum.uncertainty_adu,
        "continuum": continuum,
        "normalized": normalized,
        "normalized_uncertainty": uncertainty,
        "mask": mask,
    }


def _load_calibrated_a(path: Path) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    with fits.open(path, memmap=False) as hdul:
        data = hdul["SPECTRUM"].data
        wavelength = np.asarray(data["WAVELENGTH"], dtype=float)
        normalized = np.asarray(data["NORMALIZED_FLUX"], dtype=float)
        mask = np.asarray(data["MASK"], dtype=bool)
    if not np.all(np.diff(wavelength) > 0):
        raise RuntimeError(f"Calibrated Albireo wavelength axis is not increasing: {path}")
    return wavelength, normalized, mask


def _wavelength_metadata(path: Path) -> dict[str, object]:
    with fits.open(path, memmap=False) as hdul:
        primary = hdul[0].header
        spectrum = hdul["SPECTRUM"].data
        wave_hdu = hdul["WAVECAL"]
        wavelength = np.asarray(spectrum["WAVELENGTH"], dtype=float)
        post_flip_pixels = np.asarray(wave_hdu.data["PIXEL"], dtype=float)
        matched_wavelengths = np.asarray(wave_hdu.data["WAVELENGTH"], dtype=float)
        residuals = np.asarray(wave_hdu.data["RESIDUAL"], dtype=float)
        degree = int(wave_hdu.header.get("WAVEDEG", 1))
        flipped = bool(primary.get("DISPFLIP", False))
    if not flipped or not np.all(np.diff(wavelength) > 0):
        raise RuntimeError(f"Incorrect Albireo orientation remains in {path}")
    expected_b_lines = np.asarray([4340.47, 4861.35, 6562.79], dtype=float)
    if matched_wavelengths.size != expected_b_lines.size or not np.allclose(
        matched_wavelengths,
        expected_b_lines,
        atol=0.1,
    ):
        raise RuntimeError(
            "Albireo B must be independently anchored by H-gamma, H-beta, and H-alpha."
        )
    dispersion = float(np.median(np.diff(wavelength)))
    if not 0.90 <= dispersion <= 1.00:
        raise RuntimeError(
            f"Albireo B dispersion {dispersion:.4f} Angstrom/pixel is outside the "
            "validated three-line range."
        )
    raw_pixels = wavelength.size - 1.0 - post_flip_pixels
    anchors = []
    for pixel, raw_pixel, line_wave, residual in zip(
        post_flip_pixels,
        raw_pixels,
        matched_wavelengths,
        residuals,
    ):
        line_name = min(BALMER_LINES, key=lambda name: abs(BALMER_LINES[name] - line_wave))
        anchors.append(
            {
                "line": line_name,
                "wavelengthAngstrom": float(line_wave),
                "postFlipPixel": float(pixel),
                "rawDetectorPixel": float(raw_pixel),
                "residualAngstrom": float(residual),
            }
        )
    rms = None
    if post_flip_pixels.size > degree + 1:
        rms = float(np.sqrt(np.mean(residuals**2)))
    return {
        "method": str(wave_hdu.header.get("WAVEMETH", "unknown")),
        "medium": str(wave_hdu.header.get("AIRORVAC", "AIR")).lower(),
        "rawDirection": "red-left/blue-right",
        "horizontalFlipApplied": True,
        "outputDirection": "blue-left/red-right; increasing wavelength",
        "anchors": anchors,
        "rmsAngstrom": rms,
        "rangeAngstrom": [float(wavelength[0]), float(wavelength[-1])],
        "dispersionAngstromPerPixel": dispersion,
        "absoluteStatus": "provisional_three_stellar_lines_no_arc_lamp",
    }


def _normalized_template(path: Path, wavelength: np.ndarray) -> np.ndarray:
    template = load_stellar_template(path)
    sampled = np.interp(
        wavelength,
        template.wavelength_angstrom,
        template.flux,
        left=np.nan,
        right=np.nan,
    )
    mask = ~np.isfinite(sampled) | (sampled <= 0)
    continuum = fit_robust_continuum(
        wavelength,
        sampled,
        mask,
        bin_width_angstrom=120.0,
        percentile=70.0,
    )
    normalized = np.divide(
        sampled,
        continuum,
        out=np.full_like(sampled, np.nan),
        where=~mask & (continuum > 0),
    )
    indices = np.arange(normalized.size)
    valid = np.isfinite(normalized)
    if np.count_nonzero(valid) < 2:
        return normalized
    filled = normalized.copy()
    filled[~valid] = np.interp(indices[~valid], indices[valid], normalized[valid])
    smoothed = gaussian_filter1d(
        filled,
        TEMPLATE_SMOOTH_SIGMA_PIXELS,
        mode="nearest",
    )
    smoothed[~valid] = np.nan
    return smoothed


def _median_snr(product: dict[str, np.ndarray | fits.Header]) -> float:
    wavelength = np.asarray(product["wavelength"], dtype=float)
    flux = np.asarray(product["flux"], dtype=float)
    uncertainty = np.asarray(product["flux_uncertainty"], dtype=float)
    mask = np.asarray(product["mask"], dtype=bool)
    valid = (
        ~mask
        & np.isfinite(flux)
        & np.isfinite(uncertainty)
        & (uncertainty > 0)
        & (wavelength >= 4500.0)
        & (wavelength <= 6500.0)
    )
    return float(np.median(flux[valid] / uncertainty[valid]))


def _line_contrasts(
    wavelength: np.ndarray,
    normalized: np.ndarray,
    mask: np.ndarray,
) -> dict[str, dict[str, float] | None]:
    smoothed = gaussian_filter1d(
        np.where(mask | ~np.isfinite(normalized), 1.0, normalized),
        2.0,
        mode="nearest",
    )
    result: dict[str, dict[str, float] | None] = {}
    for name, center in BALMER_LINES.items():
        inside = (~mask) & (np.abs(wavelength - center) <= 25.0)
        if np.count_nonzero(inside) < 8:
            result[name] = None
            continue
        values = smoothed[inside]
        result[name] = {
            "absorptionDepth": float(1.0 - np.min(values)),
            "emissionHeight": float(np.max(values) - 1.0),
        }
    return result


def _resolution_matched_template_correlation(
    product: dict[str, np.ndarray | fits.Header],
    template_path: Path,
    *,
    exclude_halpha: bool = False,
) -> float:
    wavelength = np.asarray(product["wavelength"], dtype=float)
    observed = np.asarray(product["normalized"], dtype=float)
    mask = np.asarray(product["mask"], dtype=bool)
    template = _normalized_template(template_path, wavelength)
    valid = (
        ~mask
        & np.isfinite(observed)
        & np.isfinite(template)
        & (wavelength >= 4250.0)
        & (wavelength <= 6800.0)
    )
    if exclude_halpha:
        valid &= np.abs(wavelength - BALMER_LINES["H-alpha"]) > 45.0
    if np.count_nonzero(valid) < 200:
        return float("nan")
    observed_signal = 1.0 - gaussian_filter1d(observed, 2.0, mode="nearest")
    template_signal = 1.0 - template
    observed_values = observed_signal[valid]
    template_values = template_signal[valid]
    observed_scale = 1.4826 * np.median(
        np.abs(observed_values - np.median(observed_values))
    )
    template_scale = 1.4826 * np.median(
        np.abs(template_values - np.median(template_values))
    )
    observed_values = (observed_values - np.median(observed_values)) / max(
        observed_scale,
        1e-12,
    )
    template_values = (template_values - np.median(template_values)) / max(
        template_scale,
        1e-12,
    )
    return float(
        np.corrcoef(
            np.clip(observed_values, -4.0, 4.0),
            np.clip(template_values, -4.0, 4.0),
        )[0, 1]
    )


def _ratio_correlations(
    a: dict[str, np.ndarray | fits.Header],
    b: dict[str, np.ndarray | fits.Header],
    a_template_path: Path,
    b_template_path: Path,
) -> dict[str, float]:
    wavelength = np.asarray(a["wavelength"], dtype=float)
    a_flux = np.asarray(a["flux"], dtype=float)
    a_mask = np.asarray(a["mask"], dtype=bool)
    b_wave = np.asarray(b["wavelength"], dtype=float)
    b_flux = np.interp(wavelength, b_wave, np.asarray(b["flux"], dtype=float))
    b_mask = np.interp(
        wavelength,
        b_wave,
        np.asarray(b["mask"], dtype=float),
        left=1.0,
        right=1.0,
    ) > 0.1
    valid = ~a_mask & ~b_mask & (a_flux > 0) & (b_flux > 0)
    observed = np.full(wavelength.size, np.nan)
    observed[valid] = np.log(a_flux[valid]) - np.log(b_flux[valid])
    indices = np.arange(wavelength.size)
    observed[~valid] = np.interp(indices[~valid], indices[valid], observed[valid])
    observed_smooth = gaussian_filter1d(observed, 80.0)
    observed_lines = observed_smooth - gaussian_filter1d(observed, 3.0)

    a_template = load_stellar_template(a_template_path)
    b_template = load_stellar_template(b_template_path)
    template_ratio = np.log(
        np.interp(wavelength, a_template.wavelength_angstrom, a_template.flux)
    ) - np.log(np.interp(wavelength, b_template.wavelength_angstrom, b_template.flux))
    template_ratio = gaussian_filter1d(
        template_ratio,
        TEMPLATE_SMOOTH_SIGMA_PIXELS,
        mode="nearest",
    )
    template_smooth = gaussian_filter1d(template_ratio, 80.0)
    template_lines = template_smooth - gaussian_filter1d(template_ratio, 3.0)
    central = valid & (wavelength >= 3900.0) & (wavelength <= 6800.0)
    return {
        "continuumRatioCorrelation": float(
            np.corrcoef(observed_smooth[central], template_smooth[central])[0, 1]
        ),
        "lineRatioCorrelation": float(
            np.corrcoef(observed_lines[central], template_lines[central])[0, 1]
        ),
    }


def _relative_high_frequency_noise(product: dict[str, np.ndarray | fits.Header]) -> float:
    wavelength = np.asarray(product["wavelength"], dtype=float)
    normalized = np.asarray(product["normalized"], dtype=float)
    mask = np.asarray(product["mask"], dtype=bool)
    valid = (
        ~mask
        & np.isfinite(normalized)
        & (wavelength >= 4000.0)
        & (wavelength <= 6800.0)
    )
    for center in BALMER_LINES.values():
        valid &= np.abs(wavelength - center) > 30.0
    indices = np.arange(normalized.size)
    filled = normalized.copy()
    filled[~valid] = np.interp(indices[~valid], indices[valid], filled[valid])
    residual = filled - gaussian_filter1d(filled, 3.0)
    sample = residual[valid]
    return float(1.4826 * np.median(np.abs(sample - np.median(sample))))


def _plot_denoise_comparison(
    baseline_a: dict[str, np.ndarray | fits.Header],
    baseline_b: dict[str, np.ndarray | fits.Header],
    cleaned_a: dict[str, np.ndarray | fits.Header],
    cleaned_b: dict[str, np.ndarray | fits.Header],
    output: Path,
) -> dict[str, object]:
    figure, axes = plt.subplots(2, 1, figsize=(16, 9), sharex=True, constrained_layout=True)
    metrics: dict[str, object] = {
        "method": "per-frame L.A.Cosmic then aligned sigma-clipped mean",
        "randomNoiseRobustSigma": {},
        "scientificFluxSmoothed": False,
    }
    for axis, name, baseline, cleaned in (
        (axes[0], "A", baseline_a, cleaned_a),
        (axes[1], "B", baseline_b, cleaned_b),
    ):
        base_wave = np.asarray(baseline["wavelength"], dtype=float)
        base_flux = np.asarray(baseline["normalized"], dtype=float)
        base_mask = np.asarray(baseline["mask"], dtype=bool)
        clean_wave = np.asarray(cleaned["wavelength"], dtype=float)
        clean_flux = np.asarray(cleaned["normalized"], dtype=float)
        clean_mask = np.asarray(cleaned["mask"], dtype=bool)
        base_noise = _relative_high_frequency_noise(baseline)
        clean_noise = _relative_high_frequency_noise(cleaned)
        reduction = 1.0 - clean_noise / base_noise
        metrics["randomNoiseRobustSigma"][name] = {
            "original": base_noise,
            "cleaned": clean_noise,
            "reductionFraction": reduction,
        }
        base_valid = ~base_mask & np.isfinite(base_flux)
        clean_valid = ~clean_mask & np.isfinite(clean_flux)
        axis.plot(
            base_wave[base_valid],
            base_flux[base_valid],
            color="#777777",
            linewidth=0.55,
            alpha=0.65,
            label=f"original median (noise {base_noise:.4f})",
        )
        axis.plot(
            clean_wave[clean_valid],
            clean_flux[clean_valid],
            color="#05899b",
            linewidth=0.75,
            label=f"L.A.Cosmic + mean (noise {clean_noise:.4f})",
        )
        axis.axvspan(6800.0, 7800.0, color="#f59e0b", alpha=0.10)
        axis.set(title=f"Albireo {name}", ylabel="Continuum-normalized flux", ylim=(0.4, 1.55))
        axis.grid(alpha=0.18)
        axis.legend(loc="upper right")
    axes[-1].set(
        xlabel="Air wavelength (Angstrom; corrected blue to red)",
        xlim=(4200.0, 7800.0),
    )
    figure.savefig(output, dpi=170)
    plt.close(figure)
    return metrics


def _write_normalized(
    name: str,
    product: dict[str, np.ndarray | fits.Header],
    destination: Path,
) -> tuple[Path, Path]:
    wavelength = np.asarray(product["wavelength"], dtype=float)
    continuum = np.asarray(product["continuum"], dtype=float)
    normalized = np.asarray(product["normalized"], dtype=float)
    uncertainty = np.asarray(product["normalized_uncertainty"], dtype=float)
    mask = np.asarray(product["mask"], dtype=bool)
    header = product["header"].copy()
    header["CONTNORM"] = (True, "Continuum-normalized spectrum included")
    header.add_history("UVEX-ADV Albireo analysis: independent robust continuum normalization")
    columns = [
        fits.Column(name="WAVELENGTH", format="D", unit="Angstrom", array=wavelength),
        fits.Column(name="CONTINUUM", format="D", unit="adu", array=continuum),
        fits.Column(name="NORMALIZED_FLUX", format="D", array=normalized),
        fits.Column(name="NORMALIZED_UNCERTAINTY", format="D", array=uncertainty),
        fits.Column(name="MASK", format="L", array=mask),
    ]
    fits_path = destination / f"{name}_normalized.fits"
    csv_path = destination / f"{name}_normalized.csv"
    fits.HDUList(
        [fits.PrimaryHDU(header=header), fits.BinTableHDU.from_columns(columns, name="SPECTRUM")]
    ).writeto(fits_path, overwrite=True, checksum=True)
    with csv_path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "wavelength_angstrom_air",
                "continuum_adu",
                "normalized_flux",
                "normalized_uncertainty",
                "mask",
            ]
        )
        writer.writerows(zip(wavelength, continuum, normalized, uncertainty, mask))
    return fits_path, csv_path


def _plot_comparison(
    a_wave: np.ndarray,
    a_normalized: np.ndarray,
    a_mask: np.ndarray,
    b: dict[str, np.ndarray | fits.Header],
    a_template_path: Path,
    b_template_path: Path,
    output: Path,
) -> None:
    b_wave = np.asarray(b["wavelength"], dtype=float)
    b_normalized = np.asarray(b["normalized"], dtype=float)
    b_mask = np.asarray(b["mask"], dtype=bool)
    a_template = _normalized_template(a_template_path, a_wave)
    b_template = _normalized_template(b_template_path, b_wave)
    figure, axes = plt.subplots(2, 1, figsize=(16, 9), sharex=True, constrained_layout=True)
    panels = [
        (
            axes[0],
            a_wave,
            a_normalized,
            a_mask,
            a_template,
            "Albireo A",
            "K3 II proxy (A is composite)",
        ),
        (
            axes[1],
            b_wave,
            b_normalized,
            b_mask,
            b_template,
            "Albireo B",
            "B8 V photospheric proxy (B is Be)",
        ),
    ]
    for axis, wave, observed, mask, template, name, template_name in panels:
        valid = ~mask & np.isfinite(observed) & (wave >= 4200.0) & (wave <= 7800.0)
        axis.plot(wave[valid], observed[valid], color="#1368aa", linewidth=0.75, label="Observed")
        axis.plot(wave, template, color="#e36b13", linewidth=1.15, alpha=0.9, label=template_name)
        for line_name, center in BALMER_LINES.items():
            if not 4200.0 <= center <= 7800.0:
                continue
            axis.axvline(center, color="#6b7280", linewidth=0.7, alpha=0.55)
            axis.text(center + 8, 0.48, line_name, rotation=90, fontsize=8, color="#4b5563")
        axis.axvspan(6800.0, 7800.0, color="#f59e0b", alpha=0.10)
        axis.set(title=name, ylabel="Continuum-normalized flux", ylim=(0.4, 1.55))
        axis.legend(loc="upper right")
        axis.grid(alpha=0.18)
    axes[-1].set(xlabel="Air wavelength (Angstrom)", xlim=(4200.0, 7800.0))
    figure.suptitle(
        "Albireo A/B — B anchored by H-gamma, H-beta and H-alpha; provisional stellar scale"
    )
    figure.savefig(output, dpi=170)
    plt.close(figure)


def main() -> int:
    args = _parser().parse_args()
    destination = args.output_dir.expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=True)
    a = _normalize_reduced(args.a_fits)
    b = _normalize_reduced(args.b_fits)
    wavelength_metadata = _wavelength_metadata(args.b_fits)
    a_wave, a_calibrated_normalized, a_calibrated_mask = _load_calibrated_a(
        args.a_calibrated_fits
    )
    a_fits, a_csv = _write_normalized("Albireo-A", a, destination)
    b_fits, b_csv = _write_normalized("Albireo-B", b, destination)
    comparison = destination / "Albireo-AB_template_comparison.png"
    _plot_comparison(
        a_wave,
        a_calibrated_normalized,
        a_calibrated_mask,
        b,
        args.a_template,
        args.b_template,
        comparison,
    )

    broad_b_template_correlation = _resolution_matched_template_correlation(
        b,
        args.b_template,
        exclude_halpha=True,
    )
    ratio_correlations = _ratio_correlations(
        a,
        b,
        args.a_template,
        args.b_template,
    )
    denoise_artifacts: dict[str, str] = {}
    if args.baseline_a_fits is not None or args.baseline_b_fits is not None:
        if args.baseline_a_fits is None or args.baseline_b_fits is None:
            raise ValueError("Both --baseline-a-fits and --baseline-b-fits are required together.")
        baseline_a = _normalize_reduced(args.baseline_a_fits)
        baseline_b = _normalize_reduced(args.baseline_b_fits)
        denoise_plot = destination / "Albireo-AB_denoise_before_after.png"
        denoise_metrics = _plot_denoise_comparison(
            baseline_a,
            baseline_b,
            a,
            b,
            denoise_plot,
        )
        denoise_json = destination / "Albireo-AB_denoise_metrics.json"
        denoise_json.write_text(
            json.dumps(denoise_metrics, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        denoise_artifacts = {
            "denoiseComparisonPng": str(denoise_plot),
            "denoiseMetricsJson": str(denoise_json),
        }
    payload = {
        "schemaVersion": 1,
        "object": "Albireo / Beta Cygni / 6 Cygni",
        "status": "confirmed_field_and_component_labels_with_three_line_b_solution",
        "wavelengthCalibration": {
            **wavelength_metadata,
            "resolutionMatchedB8vPhotosphericCorrelation": broad_b_template_correlation,
        },
        "quality": {
            "medianPerSampleSnr4500To6500": {
                "A": _median_snr(a),
                "B": _median_snr(b),
            },
            **ratio_correlations,
            "balmerContrasts": {
                "A": _line_contrasts(
                    a_wave,
                    a_calibrated_normalized,
                    a_calibrated_mask,
                ),
                "B": _line_contrasts(
                    np.asarray(b["wavelength"], dtype=float),
                    np.asarray(b["normalized"], dtype=float),
                    np.asarray(b["mask"], dtype=bool),
                ),
            },
        },
        "denoise": {
            "A": {
                "perFrameCosmicRayClean": bool(a["header"].get("CRREJECT", False)),
                "combineMethod": str(a["header"].get("COMBMETH", "UNKNOWN")).lower(),
                "scientificFluxSmoothed": False,
            },
            "B": {
                "perFrameCosmicRayClean": bool(b["header"].get("CRREJECT", False)),
                "combineMethod": str(b["header"].get("COMBMETH", "UNKNOWN")).lower(),
                "scientificFluxSmoothed": False,
            },
        },
        "flatAssessment": {
            "sameSessionLedFound": False,
            "sameSessionSearchResult": (
                "No FITS file named or identified as LED/flat/lamp exists in the "
                "2026-05-04 ToupSky session."
            ),
            "adjacentSkyMedianAdu": {"A": 1.0, "B": 1.0},
            "adjacentSkyFractionAtOrBelow2Adu": {
                "A": 0.9992534722222223,
                "B": 0.9989795524691358,
            },
            "adjacentSkyUsableAsMultiplicativeFlat": False,
            "may07Candidate": {
                "classification": "daylight_or_solar_spectrum_not_led",
                "bitpix": 16,
                "gainRange": [10000.0, 15000.0],
                "scienceGain": 100.0,
                "accepted": False,
                "actualSafetyTrialFlatCor": False,
                "reason": (
                    "Recorded three days later with a different gain/readout and "
                    "strong solar absorption structure; detector compatibility and "
                    "unchanged spectrograph state cannot be established."
                ),
            },
            "finalFlatStatus": "not_applied_no_compatible_flat",
        },
        "limitations": [
            "All six source frames are 8-bit FITS, so dynamic range is materially reduced.",
            "No bias, dark, compatible flat or arc frames were applied.",
            "Albireo B plus an ISIS B8V template provides only a relative response, not absolute flux.",
            "Albireo B uses three stellar lines and a linear fit; its residuals do not substitute for an arc-lamp absolute-accuracy test.",
            "Albireo A has only H-beta and H-alpha anchors and therefore no independent residual degree of freedom.",
            "Albireo B is a Be star with persistent H-alpha emission, while the B8V response template is photospheric; Balmer regions are excluded from response fitting.",
            "Data at and above the conservative 6800 Angstrom second-order warning remain present and uncut.",
        ],
        "artifacts": {
            "aNormalizedFits": str(a_fits),
            "aNormalizedCsv": str(a_csv),
            "bNormalizedFits": str(b_fits),
            "bNormalizedCsv": str(b_csv),
            "comparisonPng": str(comparison),
            "aRelativeResponseCalibratedFits": str(args.a_calibrated_fits.resolve()),
            **denoise_artifacts,
        },
    }
    json_path = destination / "Albireo-AB_analysis.json"
    json_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
