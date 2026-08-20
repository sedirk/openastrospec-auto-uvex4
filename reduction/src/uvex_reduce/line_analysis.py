from __future__ import annotations

import csv
from dataclasses import asdict, dataclass
import json
from math import pi, sqrt
from pathlib import Path
import re

import matplotlib

matplotlib.use("Agg")
from matplotlib import pyplot as plt
import numpy as np
from scipy.optimize import least_squares

from .calibration import CalibratedSpectrum


@dataclass(slots=True)
class EmissionLineMeasurement:
    label: str
    laboratory_air_angstrom: float
    fitted_center_angstrom: float
    center_offset_angstrom: float
    fwhm_angstrom: float
    integrated_relative_flux: float
    relative_to_hbeta_100: float | None
    peak_snr: float
    detected: bool
    fit_method: str
    second_order_risk: bool


@dataclass(slots=True)
class NebularLineAnalysis:
    measurements: list[EmissionLineMeasurement]
    oiii_5007_to_4959: float | None
    halpha_to_hbeta: float | None
    sii_6716_to_6731: float | None
    median_detected_fwhm_angstrom: float | None

    def summary(self) -> dict[str, float | int | None]:
        return {
            "detectedLineCount": sum(line.detected for line in self.measurements),
            "oiii5007To4959": self.oiii_5007_to_4959,
            "halphaToHbeta": self.halpha_to_hbeta,
            "sii6716To6731": self.sii_6716_to_6731,
            "medianDetectedFwhmAngstrom": self.median_detected_fwhm_angstrom,
        }


_SINGLE_LINES = (
    ("H-gamma", 4340.47),
    ("He II 4686", 4685.68),
    ("H-beta", 4861.35),
    ("[O III] 4959", 4958.91),
    ("[O III] 5007", 5006.84),
    ("He II 5412", 5411.52),
    ("[N II] 5755", 5754.64),
    ("He I 5876", 5875.62),
    ("[O I] 6300", 6300.30),
    ("He I 6678", 6678.15),
    ("He I 7065", 7065.19),
    ("[Ar III] 7136", 7135.79),
)


def measure_nebular_lines(calibrated: CalibratedSpectrum) -> NebularLineAnalysis:
    """Fit the common optical lines of a planetary-nebula spectrum.

    Fits use the response-corrected, non-normalized spectrum.  Strong blends are
    handled explicitly: H-alpha+[N II] uses the theoretical 6583/6548 ratio of
    2.96, and the [S II] doublet shares a wavelength shift and instrumental width.
    The returned line fluxes remain relative slit spectra, not absolute fluxes.
    """

    wavelength = np.asarray(calibrated.wavelength_angstrom, dtype=float)
    flux = np.asarray(calibrated.relative_flux, dtype=float)
    uncertainty = np.asarray(calibrated.relative_uncertainty, dtype=float)
    mask = np.asarray(calibrated.mask, dtype=bool)
    measurements = [
        _fit_single_line(
            wavelength,
            flux,
            uncertainty,
            mask,
            label,
            laboratory,
            calibrated.second_order_start_angstrom,
        )
        for label, laboratory in _SINGLE_LINES
        if wavelength[0] + 25.0 <= laboratory <= wavelength[-1] - 25.0
    ]
    measurements.extend(
        _fit_halpha_nii(
            wavelength,
            flux,
            uncertainty,
            mask,
            calibrated.second_order_start_angstrom,
        )
    )
    measurements.extend(
        _fit_sii_doublet(
            wavelength,
            flux,
            uncertainty,
            mask,
            calibrated.second_order_start_angstrom,
        )
    )
    measurements.sort(key=lambda line: line.laboratory_air_angstrom)

    hbeta = _detected_line(measurements, "H-beta")
    if hbeta is not None and hbeta.integrated_relative_flux > 0:
        for line in measurements:
            line.relative_to_hbeta_100 = (
                100.0 * line.integrated_relative_flux / hbeta.integrated_relative_flux
                if line.detected
                else None
            )

    oiii_4959 = _detected_line(measurements, "[O III] 4959")
    oiii_5007 = _detected_line(measurements, "[O III] 5007")
    halpha = _detected_line(measurements, "H-alpha")
    sii_6716 = _detected_line(measurements, "[S II] 6716")
    sii_6731 = _detected_line(measurements, "[S II] 6731")
    reliable_widths = [
        line.fwhm_angstrom
        for line in measurements
        if line.detected
        and not line.second_order_risk
        and line.label not in {"[N II] 6548", "[N II] 6583", "H-alpha"}
    ]
    return NebularLineAnalysis(
        measurements=measurements,
        oiii_5007_to_4959=_safe_ratio(oiii_5007, oiii_4959),
        halpha_to_hbeta=_safe_ratio(halpha, hbeta),
        sii_6716_to_6731=_safe_ratio(sii_6716, sii_6731),
        median_detected_fwhm_angstrom=(
            float(np.median(reliable_widths)) if reliable_widths else None
        ),
    )


def write_nebular_line_products(
    calibrated: CalibratedSpectrum,
    analysis: NebularLineAnalysis,
    output_dir: str | Path,
    target_name: str,
) -> dict[str, Path]:
    destination = Path(output_dir).expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=True)
    stem = re.sub(r"[^A-Za-z0-9_.-]+", "_", target_name).strip("._") or "target"
    csv_path = destination / f"{stem}_emission_lines.csv"
    json_path = destination / f"{stem}_line_analysis.json"
    png_path = destination / f"{stem}_line_diagnostics.png"

    fieldnames = list(asdict(analysis.measurements[0])) if analysis.measurements else []
    with csv_path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(asdict(line) for line in analysis.measurements)

    payload = {
        "schemaVersion": 1,
        "analysisType": "relative-nebular-emission-line-fit",
        "target": target_name,
        "absoluteFluxCalibrated": False,
        "summary": analysis.summary(),
        "measurements": [asdict(line) for line in analysis.measurements],
        "notes": [
            "Integrated fluxes use the relative-response corrected spectrum and are not absolute physical fluxes.",
            "H-alpha+[N II] was deblended with the theoretical [N II] 6583/6548 ratio fixed at 2.96.",
            "Lines at or beyond the configured second-order threshold are retained but flagged as qualitative.",
        ],
    }
    json_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    _plot_line_diagnostics(calibrated, analysis, png_path, target_name)
    return {
        "line_csv": csv_path,
        "line_json": json_path,
        "line_png": png_path,
    }


def _fit_single_line(
    wavelength: np.ndarray,
    flux: np.ndarray,
    uncertainty: np.ndarray,
    mask: np.ndarray,
    label: str,
    laboratory: float,
    second_order_start: float | None,
) -> EmissionLineMeasurement:
    half_width = 25.0
    selected = (
        (np.abs(wavelength - laboratory) <= half_width)
        & ~mask
        & np.isfinite(flux)
        & np.isfinite(uncertainty)
    )
    x = wavelength[selected]
    y = flux[selected]
    if x.size < 20:
        return _failed_line(label, laboratory, "single-gaussian", second_order_start)
    edge = np.abs(x - laboratory) >= 0.58 * half_width
    baseline = float(np.median(y[edge]))
    noise = _robust_noise(y[edge])
    near = np.abs(x - laboratory) <= 6.0
    peak_index = int(np.argmax(y[near]))
    center0 = float(x[near][peak_index])
    amplitude0 = max(float(y[near][peak_index] - baseline), noise)

    def model(parameters: np.ndarray) -> np.ndarray:
        continuum, slope, amplitude, center, sigma = parameters
        return continuum + slope * (x - laboratory) + amplitude * np.exp(
            -0.5 * ((x - center) / sigma) ** 2
        )

    result = least_squares(
        lambda parameters: (model(parameters) - y) / noise,
        [baseline, 0.0, amplitude0, center0, 5.0],
        bounds=(
            [-np.inf, -np.inf, 0.0, laboratory - 6.0, 1.0],
            [np.inf, np.inf, np.inf, laboratory + 6.0, 15.0],
        ),
        loss="soft_l1",
        f_scale=2.0,
        max_nfev=5000,
    )
    _, _, amplitude, center, sigma = result.x
    peak_snr = float(amplitude / noise)
    at_bound = abs(center - laboratory) >= 5.8 or sigma >= 14.8
    detected = bool(result.success and peak_snr >= 5.0 and not at_bound)
    return EmissionLineMeasurement(
        label=label,
        laboratory_air_angstrom=laboratory,
        fitted_center_angstrom=float(center),
        center_offset_angstrom=float(center - laboratory),
        fwhm_angstrom=float(2.354820045 * sigma),
        integrated_relative_flux=float(amplitude * sigma * sqrt(2.0 * pi)),
        relative_to_hbeta_100=None,
        peak_snr=peak_snr,
        detected=detected,
        fit_method="robust-single-gaussian",
        second_order_risk=_second_order_risk(laboratory, second_order_start),
    )


def _fit_halpha_nii(
    wavelength: np.ndarray,
    flux: np.ndarray,
    uncertainty: np.ndarray,
    mask: np.ndarray,
    second_order_start: float | None,
) -> list[EmissionLineMeasurement]:
    references = np.asarray([6548.05, 6562.79, 6583.45])
    selected = (wavelength >= 6515.0) & (wavelength <= 6615.0) & ~mask
    x = wavelength[selected]
    y = flux[selected]
    edge = (x <= 6530.0) | (x >= 6600.0)
    baseline = float(np.median(y[edge]))
    noise = _robust_noise(y[edge])

    def gaussian(center: float, sigma: float) -> np.ndarray:
        return np.exp(-0.5 * ((x - center) / sigma) ** 2)

    def model(parameters: np.ndarray) -> np.ndarray:
        continuum, slope, nii6548, halpha, shift, nii_sigma, halpha_sigma = parameters
        return (
            continuum
            + slope * (x - references[1])
            + nii6548 * gaussian(references[0] + shift, nii_sigma)
            + halpha * gaussian(references[1] + shift, halpha_sigma)
            + 2.96 * nii6548 * gaussian(references[2] + shift, nii_sigma)
        )

    result = least_squares(
        lambda parameters: (model(parameters) - y) / noise,
        [baseline, 0.0, 100.0, max(float(np.max(y) - baseline), noise), 0.0, 5.0, 5.0],
        bounds=(
            [-np.inf, -np.inf, 0.0, 0.0, -5.0, 1.0, 1.0],
            [np.inf, np.inf, np.inf, np.inf, 5.0, 15.0, 15.0],
        ),
        loss="soft_l1",
        f_scale=2.0,
        max_nfev=10_000,
    )
    _, _, nii6548, halpha, shift, nii_sigma, halpha_sigma = result.x
    entries = (
        ("[N II] 6548", references[0], nii6548, nii_sigma),
        ("H-alpha", references[1], halpha, halpha_sigma),
        ("[N II] 6583", references[2], 2.96 * nii6548, nii_sigma),
    )
    return [
        EmissionLineMeasurement(
            label=label,
            laboratory_air_angstrom=float(reference),
            fitted_center_angstrom=float(reference + shift),
            center_offset_angstrom=float(shift),
            fwhm_angstrom=float(2.354820045 * sigma),
            integrated_relative_flux=float(amplitude * sigma * sqrt(2.0 * pi)),
            relative_to_hbeta_100=None,
            peak_snr=float(amplitude / noise),
            detected=bool(result.success and amplitude / noise >= 5.0),
            fit_method="constrained-Halpha-[NII]-blend;[NII]6583/6548=2.96",
            second_order_risk=_second_order_risk(float(reference), second_order_start),
        )
        for label, reference, amplitude, sigma in entries
    ]


def _fit_sii_doublet(
    wavelength: np.ndarray,
    flux: np.ndarray,
    uncertainty: np.ndarray,
    mask: np.ndarray,
    second_order_start: float | None,
) -> list[EmissionLineMeasurement]:
    references = np.asarray([6716.44, 6730.82])
    selected = (wavelength >= 6688.0) & (wavelength <= 6758.0) & ~mask
    x = wavelength[selected]
    y = flux[selected]
    edge = (x <= 6700.0) | (x >= 6747.0)
    baseline = float(np.median(y[edge]))
    noise = _robust_noise(y[edge])

    def model(parameters: np.ndarray) -> np.ndarray:
        continuum, slope, amplitude1, amplitude2, shift, sigma = parameters
        return (
            continuum
            + slope * (x - float(np.mean(references)))
            + amplitude1 * np.exp(-0.5 * ((x - references[0] - shift) / sigma) ** 2)
            + amplitude2 * np.exp(-0.5 * ((x - references[1] - shift) / sigma) ** 2)
        )

    result = least_squares(
        lambda parameters: (model(parameters) - y) / noise,
        [baseline, 0.0, max(noise, 10.0), max(noise, 10.0), 0.0, 5.0],
        bounds=(
            [-np.inf, -np.inf, 0.0, 0.0, -5.0, 1.0],
            [np.inf, np.inf, np.inf, np.inf, 5.0, 15.0],
        ),
        loss="soft_l1",
        f_scale=2.0,
        max_nfev=10_000,
    )
    _, _, amplitude1, amplitude2, shift, sigma = result.x
    return [
        EmissionLineMeasurement(
            label=label,
            laboratory_air_angstrom=float(reference),
            fitted_center_angstrom=float(reference + shift),
            center_offset_angstrom=float(shift),
            fwhm_angstrom=float(2.354820045 * sigma),
            integrated_relative_flux=float(amplitude * sigma * sqrt(2.0 * pi)),
            relative_to_hbeta_100=None,
            peak_snr=float(amplitude / noise),
            detected=bool(result.success and amplitude / noise >= 5.0),
            fit_method="joint-[SII]-doublet-shared-shift-width",
            second_order_risk=_second_order_risk(float(reference), second_order_start),
        )
        for label, reference, amplitude in (
            ("[S II] 6716", references[0], amplitude1),
            ("[S II] 6731", references[1], amplitude2),
        )
    ]


def _plot_line_diagnostics(
    calibrated: CalibratedSpectrum,
    analysis: NebularLineAnalysis,
    path: Path,
    target_name: str,
) -> None:
    wavelength = calibrated.wavelength_angstrom
    normalized = calibrated.normalized_flux
    mask = calibrated.mask
    figure, axes = plt.subplots(2, 1, figsize=(18, 10), constrained_layout=True)
    ranges = ((4200.0, 5150.0), (5700.0, min(7200.0, float(wavelength[-1]))))
    for axis, (low, high) in zip(axes, ranges):
        selected = (wavelength >= low) & (wavelength <= high) & ~mask
        axis.plot(wavelength[selected], normalized[selected], lw=1.0, color="#1468b3")
        axis.axhline(1.0, color="0.45", lw=0.8)
        values = normalized[selected]
        upper = max(3.0, float(np.nanpercentile(values, 99.7)) * 1.15)
        axis.set_ylim(0.0, upper)
        axis.set_xlim(low, high)
        for line in analysis.measurements:
            if not line.detected or not low <= line.laboratory_air_angstrom <= high:
                continue
            colour = "#d97706" if line.second_order_risk else "#b91c1c"
            axis.axvline(line.laboratory_air_angstrom, color=colour, lw=0.7, alpha=0.65)
            axis.text(
                line.laboratory_air_angstrom,
                0.96 * upper,
                line.label,
                rotation=90,
                va="top",
                ha="right",
                fontsize=8,
                color=colour,
            )
        if calibrated.second_order_start_angstrom is not None and high >= calibrated.second_order_start_angstrom:
            axis.axvspan(
                calibrated.second_order_start_angstrom,
                high,
                color="#f59e0b",
                alpha=0.10,
                label="second-order risk (retained)",
            )
            axis.legend(loc="upper left")
        axis.grid(alpha=0.2)
        axis.set_ylabel("Continuum-normalized relative flux")
    axes[-1].set_xlabel("Air wavelength (Angstrom)")
    figure.suptitle(f"{target_name} — fitted nebular emission lines")
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _failed_line(
    label: str,
    laboratory: float,
    method: str,
    second_order_start: float | None,
) -> EmissionLineMeasurement:
    return EmissionLineMeasurement(
        label=label,
        laboratory_air_angstrom=laboratory,
        fitted_center_angstrom=float("nan"),
        center_offset_angstrom=float("nan"),
        fwhm_angstrom=float("nan"),
        integrated_relative_flux=float("nan"),
        relative_to_hbeta_100=None,
        peak_snr=0.0,
        detected=False,
        fit_method=method,
        second_order_risk=_second_order_risk(laboratory, second_order_start),
    )


def _detected_line(
    measurements: list[EmissionLineMeasurement],
    label: str,
) -> EmissionLineMeasurement | None:
    return next((line for line in measurements if line.label == label and line.detected), None)


def _safe_ratio(
    numerator: EmissionLineMeasurement | None,
    denominator: EmissionLineMeasurement | None,
) -> float | None:
    if numerator is None or denominator is None or denominator.integrated_relative_flux <= 0:
        return None
    return float(numerator.integrated_relative_flux / denominator.integrated_relative_flux)


def _robust_noise(values: np.ndarray) -> float:
    values = np.asarray(values, dtype=float)
    values = values[np.isfinite(values)]
    if values.size < 5:
        return 1.0
    median = float(np.median(values))
    noise = 1.4826 * float(np.median(np.abs(values - median)))
    # A perfectly noiseless synthetic/local continuum otherwise makes the
    # least-squares residual scale numerically singular.
    floor = max(abs(median) * 1e-6, 1e-9)
    return max(noise, floor)


def _second_order_risk(wavelength: float, start: float | None) -> bool:
    return bool(start is not None and wavelength >= start)
