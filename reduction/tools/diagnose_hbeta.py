"""Audit the candidate 3C 273 H-beta region with identical local normalization.

This is a diagnostic, not a physical broad-line decomposition.  Every input is
smoothed and divided by a straight local continuum fitted to the same two
sidebands so that pipeline normalization choices do not drive the comparison.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from astropy.io import fits
import matplotlib.pyplot as plt
import numpy as np
from scipy.ndimage import gaussian_filter1d


BLUE_SIDEBAND = (5350.0, 5480.0)
LINE_WINDOW = (5540.0, 5690.0)
RED_SIDEBAND = (5900.0, 6050.0)
DISPLAY_SMOOTHING_FWHM_ANGSTROM = 7.0
EXPECTED_H_BETA = 4861.35 * (1.0 + 0.158339)
EXPECTED_FE_II_4924 = 4923.92 * (1.0 + 0.158339)
EXPECTED_O_III_4959 = 4958.91 * (1.0 + 0.158339)
EXPECTED_O_III_5007 = 5006.84 * (1.0 + 0.158339)
LINE_MARKERS = (
    ("H-beta", EXPECTED_H_BETA, "#991b1b"),
    ("Fe II 4924 candidate", EXPECTED_FE_II_4924, "#7c3aed"),
    ("[O III] 4959", EXPECTED_O_III_4959, "#0369a1"),
    ("[O III] 5007", EXPECTED_O_III_5007, "#0369a1"),
)


def _sigma_samples_for_fwhm(spacing: float, fwhm_angstrom: float) -> float:
    return max(0.5, fwhm_angstrom / 2.354820045 / spacing)


def _load_fits(path: Path) -> tuple[np.ndarray, np.ndarray, np.ndarray | None]:
    with fits.open(path, memmap=False) as hdul:
        table = hdul["SPECTRUM"].data
        names = set(table.names)
        flux_name = "FLUX" if "FLUX" in names else "NORMALIZED_FLUX"
        wavelength = np.asarray(table["WAVELENGTH"], dtype=float)
        flux = np.asarray(table[flux_name], dtype=float)
        uncertainty_name = (
            "UNCERTAINTY"
            if "UNCERTAINTY" in names
            else "NORMALIZED_UNCERTAINTY"
            if "NORMALIZED_UNCERTAINTY" in names
            else None
        )
        uncertainty = (
            np.asarray(table[uncertainty_name], dtype=float)
            if uncertainty_name is not None
            else None
        )
        mask = np.asarray(table["MASK"], dtype=bool) if "MASK" in names else False
    valid = np.isfinite(wavelength) & np.isfinite(flux) & ~mask
    if uncertainty is not None:
        valid &= np.isfinite(uncertainty) & (uncertainty > 0)
    order = np.argsort(wavelength[valid])
    return (
        wavelength[valid][order],
        flux[valid][order],
        uncertainty[valid][order] if uncertainty is not None else None,
    )


def _load_reference(path: Path) -> tuple[np.ndarray, np.ndarray, None]:
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        fields = line.split()
        if not fields or fields[0].startswith("#") or len(fields) < 5 or fields[4] != "STIS":
            continue
        rows.append((float(fields[0]) * 10_000.0, float(fields[2])))
    values = np.asarray(rows, dtype=float)
    valid = np.isfinite(values).all(axis=1) & (values[:, 1] > 0)
    order = np.argsort(values[valid, 0])
    return values[valid, 0][order], values[valid, 1][order], None


def _measure(
    wavelength: np.ndarray,
    flux: np.ndarray,
    uncertainty: np.ndarray | None,
) -> tuple[np.ndarray, dict[str, float | None]]:
    spacing = float(np.nanmedian(np.diff(wavelength)))
    smoothed = gaussian_filter1d(
        flux,
        _sigma_samples_for_fwhm(spacing, DISPLAY_SMOOTHING_FWHM_ANGSTROM),
        mode="nearest",
    )
    sidebands = ((wavelength >= BLUE_SIDEBAND[0]) & (wavelength <= BLUE_SIDEBAND[1])) | (
        (wavelength >= RED_SIDEBAND[0]) & (wavelength <= RED_SIDEBAND[1])
    )
    coefficients = np.polyfit(wavelength[sidebands], smoothed[sidebands], 1)
    continuum = np.polyval(coefficients, wavelength)
    normalized = smoothed / continuum
    inside = (wavelength >= LINE_WINDOW[0]) & (wavelength <= LINE_WINDOW[1])
    line_indices = np.flatnonzero(inside)
    peak_index = int(line_indices[np.nanargmax(normalized[inside])])
    positive_area = float(
        np.trapz(np.clip(normalized[inside] - 1.0, 0.0, None), wavelength[inside])
    )
    integrated_snr = None
    if uncertainty is not None:
        signal = float(
            np.trapz(
                np.clip(flux[inside] - continuum[inside], 0.0, None),
                wavelength[inside],
            )
        )
        noise = float(np.sqrt(np.sum((uncertainty[inside] * spacing) ** 2)))
        integrated_snr = signal / noise if noise > 0 else None
    return normalized, {
        "peakWavelengthAngstrom": float(wavelength[peak_index]),
        "peakOverLocalContinuum": float(normalized[peak_index]),
        "fluxAtExpectedHbetaOverLocalContinuum": float(
            np.interp(EXPECTED_H_BETA, wavelength, normalized)
        ),
        "positiveExcessAreaAngstrom": positive_area,
        "integratedPositiveExcessSnr": integrated_snr,
        "feIi4924RegionOverLocalContinuum": float(
            np.interp(EXPECTED_FE_II_4924, wavelength, normalized)
        ),
        "oIii4959RegionOverLocalContinuum": float(
            np.interp(EXPECTED_O_III_4959, wavelength, normalized)
        ),
        "oIii5007RegionOverLocalContinuum": float(
            np.interp(EXPECTED_O_III_5007, wavelength, normalized)
        ),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--spectrum", action="append", nargs=2, metavar=("LABEL", "FITS"))
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()

    series: list[tuple[str, np.ndarray, np.ndarray]] = []
    measurements: dict[str, dict[str, float | None]] = {}
    for label, value in arguments.spectrum or []:
        wavelength, flux, uncertainty = _load_fits(Path(value))
        normalized, measurement = _measure(wavelength, flux, uncertainty)
        series.append((label, wavelength, normalized))
        measurements[label] = measurement
    wavelength, flux, uncertainty = _load_reference(arguments.reference)
    normalized, measurement = _measure(wavelength, flux, uncertainty)
    series.append(("HST/STIS reference", wavelength, normalized))
    measurements["HST/STIS reference"] = measurement

    observed_measurements = [
        values for label, values in measurements.items() if label != "HST/STIS reference"
    ]
    detections = [
        values
        for values in observed_measurements
        if values["fluxAtExpectedHbetaOverLocalContinuum"] is not None
        and values["fluxAtExpectedHbetaOverLocalContinuum"] >= 1.05
        and abs(values["peakWavelengthAngstrom"] - EXPECTED_H_BETA) <= 25.0
        and (
            values["integratedPositiveExcessSnr"] is None
            or values["integratedPositiveExcessSnr"] >= 3.0
        )
    ]
    h_beta_detected = bool(detections)

    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    figure, axis = plt.subplots(figsize=(14, 7), constrained_layout=True)
    for label, wavelength, values in series:
        inside = (wavelength >= 5325.0) & (wavelength <= 6075.0)
        axis.plot(wavelength[inside], values[inside], linewidth=1.35, label=label)
    axis.axhline(1.0, color="0.4", linewidth=0.8)
    axis.axvspan(*LINE_WINDOW, color="#fde68a", alpha=0.18)
    axis.axvspan(*BLUE_SIDEBAND, color="#94a3b8", alpha=0.10)
    axis.axvspan(*RED_SIDEBAND, color="#94a3b8", alpha=0.10)
    for label, wavelength, colour in LINE_MARKERS:
        axis.axvline(wavelength, color=colour, linestyle="--", linewidth=1.0)
        axis.text(
            wavelength,
            0.98,
            f"{label}\n{wavelength:.1f} A",
            color=colour,
            rotation=90,
            va="top",
            ha="right",
            fontsize=8,
            transform=axis.get_xaxis_transform(),
        )
    axis.set(
        xlabel="Observed wavelength (Angstrom)",
        ylabel="Flux / identical local linear continuum",
        title=(
            "3C 273 H-beta / Fe II / [O III] diagnostic "
            f"({DISPLAY_SMOOTHING_FWHM_ANGSTROM:g} A FWHM display smoothing)"
        ),
    )
    axis.grid(alpha=0.2)
    axis.legend()
    figure.savefig(arguments.output, dpi=180)
    plt.close(figure)

    json_path = arguments.output.with_suffix(".json")
    json_path.write_text(
        json.dumps(
            {
                "expectedHbetaAngstrom": EXPECTED_H_BETA,
                "continuumSidebandsAngstrom": [BLUE_SIDEBAND, RED_SIDEBAND],
                "lineWindowAngstrom": LINE_WINDOW,
                "displaySmoothing": {
                    "kind": "Gaussian",
                    "fwhmAngstrom": DISPLAY_SMOOTHING_FWHM_ANGSTROM,
                    "legacyBug": (
                        "The previous plot used 7 A as Gaussian sigma (16.48 A FWHM); "
                        "this product uses 7 A as FWHM."
                    ),
                },
                "expectedObservedFeaturesAngstrom": {
                    "Hbeta": EXPECTED_H_BETA,
                    "FeII4924Candidate": EXPECTED_FE_II_4924,
                    "OIII4959": EXPECTED_O_III_4959,
                    "OIII5007": EXPECTED_O_III_5007,
                },
                "assessment": {
                    "hBetaDetected": h_beta_detected,
                    "classification": (
                        "candidate-detected-requires-deblending"
                        if h_beta_detected
                        else "not-recovered-under-current-wavelength-solution"
                    ),
                    "gates": {
                        "minimumFluxAtExpectedOverContinuum": 1.05,
                        "maximumPeakOffsetAngstrom": 25.0,
                        "minimumIntegratedPositiveExcessSnrWhenAvailable": 3.0,
                    },
                    "reason": (
                        "No observed product simultaneously has an emission excess at "
                        "5631 A, a nearby peak, and adequate integrated significance. "
                        "Positive residual elsewhere in the wide window is not H-beta."
                        if not h_beta_detected
                        else "Independent acquisition segments passed the H-beta gates under "
                        "the same-session NGC 6543 wavelength solution. Detailed physical "
                        "line flux still requires response calibration and Fe II/continuum "
                        "deblending."
                    ),
                },
                "warning": (
                    "Positive excess area includes broad H-beta, Fe II and any residual "
                    "continuum curvature; it is not a deblended physical equivalent width. "
                    "The old 5690-5725 A red sideband overlapped the redshifted Fe II 4924 "
                    "feature and was replaced by 5900-6050 A."
                ),
                "measurements": measurements,
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )
    print(json.dumps(measurements, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
