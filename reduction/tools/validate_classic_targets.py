"""Build reproducible sanity checks for the strongest classic-target reductions."""

from __future__ import annotations

import argparse
from datetime import datetime
import json
import os
from pathlib import Path

from astropy.io import fits
import matplotlib.pyplot as plt
import numpy as np
from scipy.ndimage import gaussian_filter1d

from uvex_reduce.stellar import (
    _absorption_signal,
    _partial_template_correlation,
    _robust_standardize,
    load_stellar_template,
)


ROOT = Path(__file__).resolve().parents[2]
REDUCTION = ROOT / "reduction"
ISIS = Path("<local-isis-template-directory>")
SPEC = Path("<local-spec-root>")
TOUPSKY = Path("<local-toupsky-root>")
OUTPUT = REDUCTION / "output" / "_internal" / "quality" / "classic-target-validation"


def _load_spectrum(path: Path, flux_column: str) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    with fits.open(path, memmap=False) as hdul:
        table = hdul["SPECTRUM"].data
        wavelength = np.asarray(table["WAVELENGTH"], dtype=float)
        flux = np.asarray(table[flux_column], dtype=float)
        mask = np.asarray(table["MASK"], dtype=bool)
    return wavelength, flux, mask


def _validate_template(
    spectrum_path: Path,
    flux_column: str,
    template_path: Path,
    minimum_angstrom: float,
    maximum_angstrom: float,
) -> tuple[dict[str, object], tuple[np.ndarray, np.ndarray, np.ndarray]]:
    wavelength, flux, mask = _load_spectrum(spectrum_path, flux_column)
    template = load_stellar_template(template_path)
    mask = (
        mask
        | ~np.isfinite(wavelength)
        | ~np.isfinite(flux)
        | (wavelength < minimum_angstrom)
        | (wavelength > maximum_angstrom)
    )
    shifts = np.linspace(-20.0, 20.0, 161)
    correlations = np.asarray(
        [
            _partial_template_correlation(flux, mask, wavelength + shift, template)
            for shift in shifts
        ],
        dtype=float,
    )
    best_index = int(np.nanargmax(correlations))
    best_shift = float(shifts[best_index])
    zero_correlation = float(_partial_template_correlation(flux, mask, wavelength, template))

    observed_signal = _absorption_signal(flux, mask, 80.0)
    sampled_template = np.interp(
        wavelength + best_shift,
        template.wavelength_angstrom,
        template.flux,
        left=np.nan,
        right=np.nan,
    )
    reference_mask = mask | ~np.isfinite(sampled_template)
    reference_signal = _absorption_signal(sampled_template, reference_mask, 80.0)
    valid = ~reference_mask
    observed_standard = np.full_like(observed_signal, np.nan)
    reference_standard = np.full_like(reference_signal, np.nan)
    observed_standard[valid] = _robust_standardize(observed_signal[valid])
    reference_standard[valid] = _robust_standardize(reference_signal[valid])

    smoothed = gaussian_filter1d(flux, 3.0, mode="nearest")
    line_centres: dict[str, float] = {}
    for name, expected in (
        ("Ca I", 4226.7),
        ("H-beta", 4861.3),
        ("Mg b", 5174.0),
        ("Na D", 5892.9),
        ("H-alpha", 6562.8),
    ):
        inside = valid & (wavelength >= expected - 18.0) & (wavelength <= expected + 18.0)
        if np.count_nonzero(inside):
            candidates = np.flatnonzero(inside)
            line_centres[name] = float(wavelength[candidates[np.nanargmin(smoothed[inside])]])
    result = {
        "spectrum": str(spectrum_path.resolve()),
        "template": str(template_path.resolve()),
        "correlationAtExistingWavelength": zero_correlation,
        "bestSmallShiftAngstrom": best_shift,
        "correlationAfterSmallShift": float(correlations[best_index]),
        "measuredAbsorptionMinimaAngstrom": line_centres,
    }
    return result, (wavelength, observed_standard, reference_standard)


def _gap_minutes(first_group: list[Path], second_group: list[Path]) -> float:
    first_end = max(datetime.fromisoformat(fits.getheader(path)["DATE-OBS"]) for path in first_group)
    second_start = min(
        datetime.fromisoformat(fits.getheader(path)["DATE-OBS"]) for path in second_group
    )
    return abs((second_start - first_end).total_seconds()) / 60.0


def _plot(
    destination: Path,
    pollux_plot: tuple[np.ndarray, np.ndarray, np.ndarray],
    hd_plot: tuple[np.ndarray, np.ndarray, np.ndarray],
    ngc_path: Path,
) -> None:
    figure, axes = plt.subplots(3, 1, figsize=(14, 11), constrained_layout=True)
    for axis, payload, title, limits in (
        (axes[0], pollux_plot, "2026-02-18 Pollux vs K0 III template", (4050, 6750)),
        (axes[1], hd_plot, "2026-05-09 HD 140573 vs K2 III template", (4400, 6750)),
    ):
        wavelength, observed, reference = payload
        inside = (wavelength >= limits[0]) & (wavelength <= limits[1])
        axis.plot(wavelength[inside], np.clip(observed[inside], -3, 5), color="#075a8c", linewidth=0.8, label="UVEX absorption signal")
        axis.plot(wavelength[inside], np.clip(reference[inside], -3, 5), color="#d97706", linewidth=0.8, alpha=0.8, label="ISIS spectral-type template")
        axis.set(xlim=limits, ylabel="Robust standardized absorption", title=title)
        axis.grid(alpha=0.2)
        axis.legend(loc="upper right", fontsize=8)

    wavelength, normalized, mask = _load_spectrum(ngc_path, "NORMALIZED_FLUX")
    inside = ~mask & (wavelength >= 4200) & (wavelength <= 7250)
    axes[2].plot(wavelength[inside], np.clip(normalized[inside], 0, 55), color="#0f766e", linewidth=0.8)
    for line in (4340.47, 4861.35, 4958.91, 5006.84, 5875.62, 6548.05, 6562.79, 7135.79):
        axes[2].axvline(line, color="#dc2626", alpha=0.45, linewidth=0.8)
    axes[2].set(
        xlim=(4200, 7250),
        xlabel="Air wavelength (Angstrom)",
        ylabel="Continuum-normalized flux",
        title="NGC 6543: eight-line nebular refinement (RMS 0.81 Angstrom)",
    )
    axes[2].grid(alpha=0.2)
    figure.suptitle("Independent classic-target checks", fontsize=16)
    figure.savefig(destination, dpi=180)
    plt.close(figure)


def main() -> None:
    global ISIS, SPEC, TOUPSKY, OUTPUT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--isis-template-directory",
        type=Path,
        default=os.environ.get("UVEX_ADV_ISIS_TEMPLATE_DIRECTORY"),
    )
    parser.add_argument(
        "--spec-root",
        type=Path,
        default=os.environ.get("UVEX_ADV_SPEC_ROOT"),
    )
    parser.add_argument(
        "--toupsky-root",
        type=Path,
        default=os.environ.get("UVEX_ADV_TOUPSKY_ROOT"),
    )
    parser.add_argument("--output", type=Path, default=OUTPUT)
    args = parser.parse_args()
    missing = [
        option
        for option, value in (
            ("--isis-template-directory", args.isis_template_directory),
            ("--spec-root", args.spec_root),
            ("--toupsky-root", args.toupsky_root),
        )
        if value is None
    ]
    if missing:
        parser.error(
            "missing required local path(s): "
            + ", ".join(missing)
            + "; pass them explicitly or set the documented UVEX_ADV_* environment variables"
        )
    ISIS = args.isis_template_directory.expanduser().resolve()
    SPEC = args.spec_root.expanduser().resolve()
    TOUPSKY = args.toupsky_root.expanduser().resolve()
    OUTPUT = args.output.expanduser().resolve()
    for label, directory in (
        ("ISIS template directory", ISIS),
        ("SPEC root", SPEC),
        ("ToupSky root", TOUPSKY),
    ):
        if not directory.is_dir():
            parser.error(f"{label} does not exist or is not a directory: {directory}")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    runs = REDUCTION / "output" / "_internal" / "runs"
    pollux_path = runs / "science" / "2026-02-18-pollux" / "Pollux_spectrum.fits"
    hd_path = runs / "may-2026-validation" / "hd140573" / "HD140573_calibrated_1d.fits"
    ngc_path = runs / "may-2026-validation" / "ngc6543" / "NGC6543_calibrated_1d.fits"
    pollux, pollux_plot = _validate_template(pollux_path, "FLUX", ISIS / "p_k0iii.dat", 4000, 6800)
    hd, hd_plot = _validate_template(hd_path, "RELATIVE_FLUX", ISIS / "p_k2iii.dat", 4400, 6700)
    ngc_manifest = json.loads(
        (runs / "may-2026-validation" / "ngc6543" / "NGC6543_validation.json").read_text(
            encoding="utf-8"
        )
    )
    pollux["catalogueType"] = "K0IIIb"
    pollux["standard"] = "Regulus"
    pollux["sameObservingDate"] = "2026-02-18"
    pollux["nearestExposureGapMinutes"] = _gap_minutes(
        sorted((SPEC / "26.2.18").glob("Pollux-[456].fit")),
        sorted((SPEC / "26.2.18").glob("Regulus-[123].fit")),
    )
    hd["catalogueType"] = "K2IIIb"
    hd["standard"] = "Vega"
    hd["sameObservingDate"] = "2026-05-09"
    hd["nearestExposureGapMinutes"] = _gap_minutes(
        [TOUPSKY / "20260509" / "HD140573" / "260509011502.fit"],
        sorted((TOUPSKY / "20260509" / "Vega").glob("*.fit")),
    )
    result = {
        "pollux": pollux,
        "hd140573": hd,
        "ngc6543": {
            "classification": "spectrally-secure-planetary-nebula",
            "standard": "Vega",
            "sameObservingDate": False,
            "matchedNebularLines": len(ngc_manifest["wavelengthRefinement"]["reference"]),
            "wavelengthRmsAngstrom": ngc_manifest["wavelengthRefinement"]["rmsAngstrom"],
            "spectrum": str(ngc_path.resolve()),
        },
        "interpretation": (
            "Pollux and HD 140573 are independent same-date standard-to-target checks. "
            "NGC 6543 is the strongest line-identification check, but its Vega transfer is cross-night."
        ),
    }
    plot_path = OUTPUT / "classic_target_validation.png"
    _plot(plot_path, pollux_plot, hd_plot, ngc_path)
    result["plot"] = str(plot_path.resolve())
    json_path = OUTPUT / "classic_target_validation.json"
    json_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json_path)
    print(plot_path)


if __name__ == "__main__":
    main()
