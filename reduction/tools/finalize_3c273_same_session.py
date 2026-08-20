"""Create the canonical counts-only, continuum-normalized 3C 273 product.

The immediately following NGC 6543 sequence supplies only the wavelength
solution.  It is a nebular line anchor, not a spectrophotometric response
standard, so this tool deliberately does not divide by an instrumental
response curve and never labels the result as flux calibrated.
"""

from __future__ import annotations

import argparse
import csv
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path

from astropy.io import fits
import matplotlib

matplotlib.use("Agg")
from matplotlib import pyplot as plt
import numpy as np
from scipy.ndimage import gaussian_filter1d, median_filter, percentile_filter


DEFAULT_SECOND_ORDER_WARNING_ANGSTROM = 6800.0
DEFAULT_CONTINUUM_WINDOW_ANGSTROM = 450.0
DEFAULT_CONTINUUM_PERCENTILE = 35.0


def _odd(value: int) -> int:
    value = max(9, int(value))
    return value if value % 2 else value + 1


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _filled_positive_samples(
    wavelength: np.ndarray,
    count_rate: np.ndarray,
    valid: np.ndarray,
) -> tuple[np.ndarray, float]:
    if np.count_nonzero(valid) < 100:
        raise RuntimeError("Too few valid positive samples for continuum normalization.")
    dispersion = float(np.nanmedian(np.diff(wavelength[valid])))
    if not np.isfinite(dispersion) or dispersion <= 0:
        raise RuntimeError("The wavelength axis is not strictly increasing.")
    sample = np.arange(count_rate.size, dtype=float)
    filled = np.interp(sample, sample[valid], count_rate[valid])
    return filled, dispersion


def _running_median_continuum(
    wavelength: np.ndarray,
    count_rate: np.ndarray,
    valid: np.ndarray,
    window_angstrom: float = 300.0,
) -> np.ndarray:
    """Return the historical running-median continuum for audit comparison."""
    filled, dispersion = _filled_positive_samples(wavelength, count_rate, valid)
    width = _odd(round(window_angstrom / dispersion))
    broad = median_filter(filled, size=width, mode="nearest")
    broad = gaussian_filter1d(broad, sigma=max(1.0, width / 8.0), mode="nearest")
    return broad


def _continuum(
    wavelength: np.ndarray,
    count_rate: np.ndarray,
    valid: np.ndarray,
    window_angstrom: float,
    percentile: float = DEFAULT_CONTINUUM_PERCENTILE,
) -> np.ndarray:
    """Estimate an emission-resistant pseudo-continuum.

    A central running median can ride up on the broad H-beta/Fe II/[O III]
    complex.  A lower rolling quantile leaves those positive features in the
    normalized spectrum while still following the broad instrumental shape.
    This remains a descriptive pseudo-continuum, not a physical AGN Fe II
    decomposition or a spectrophotometric response correction.
    """
    if not 5.0 <= percentile <= 50.0:
        raise ValueError("Continuum percentile must be between 5 and 50.")
    filled, dispersion = _filled_positive_samples(wavelength, count_rate, valid)
    width = _odd(round(window_angstrom / dispersion))
    broad = percentile_filter(
        filled,
        percentile=percentile,
        size=width,
        mode="nearest",
    )
    broad = gaussian_filter1d(broad, sigma=max(1.0, width / 12.0), mode="nearest")
    return broad


def finalize(
    source: Path,
    output_directory: Path,
    continuum_window_angstrom: float = DEFAULT_CONTINUUM_WINDOW_ANGSTROM,
    continuum_percentile: float = DEFAULT_CONTINUUM_PERCENTILE,
    second_order_warning_angstrom: float = DEFAULT_SECOND_ORDER_WARNING_ANGSTROM,
) -> dict[str, Path]:
    source = source.expanduser().resolve()
    output_directory = output_directory.expanduser().resolve()
    output_directory.mkdir(parents=True, exist_ok=True)

    with fits.open(source, memmap=False) as hdul:
        header = hdul[0].header.copy()
        spectrum = hdul["SPECTRUM"].data
        pixel = np.asarray(spectrum["PIXEL"], dtype=float)
        wavelength = np.asarray(spectrum["WAVELENGTH"], dtype=float)
        flux = np.asarray(spectrum["FLUX"], dtype=float)
        uncertainty = np.asarray(spectrum["UNCERTAINTY"], dtype=float)
        mask = np.asarray(spectrum["MASK"], dtype=bool)
        provenance_hdus = [hdu.copy() for hdu in hdul[2:]]

    # ToupSky stores very long comments on these values.  They are already
    # truncated in the source card and otherwise trigger a FITS verification
    # warning every time the derived product is written.
    for keyword in ("CREATOR", "PROGRAM", "SWCREATE"):
        if keyword in header:
            header.comments[keyword] = "Source acquisition software"
    if "TCRFRAC" in header:
        header.comments["TCRFRAC"] = "Temporal clipping rejected fraction"

    exposure = float(header.get("EXPTIME", 0.0))
    if exposure <= 0:
        raise RuntimeError("The extracted spectrum has no positive EXPTIME.")
    valid = (
        ~mask & np.isfinite(wavelength) & np.isfinite(flux) & np.isfinite(uncertainty) & (flux > 0)
    )
    count_rate = flux / exposure
    count_rate_uncertainty = uncertainty / exposure
    continuum = _continuum(
        wavelength,
        count_rate,
        valid,
        continuum_window_angstrom,
        continuum_percentile,
    )
    legacy_continuum = _running_median_continuum(
        wavelength,
        count_rate,
        valid,
    )
    final_mask = mask | ~np.isfinite(continuum) | (continuum <= 0)
    normalized = np.divide(
        count_rate,
        continuum,
        out=np.full_like(count_rate, np.nan),
        where=~final_mask,
    )
    normalized_uncertainty = np.divide(
        count_rate_uncertainty,
        continuum,
        out=np.full_like(count_rate_uncertainty, np.nan),
        where=~final_mask,
    )
    legacy_normalized = np.divide(
        count_rate,
        legacy_continuum,
        out=np.full_like(count_rate, np.nan),
        where=np.isfinite(legacy_continuum) & (legacy_continuum > 0),
    )
    order2_risk = wavelength >= second_order_warning_angstrom

    header["WAVESRC"] = ("NGC6543", "Same-session nebular wavelength anchor")
    header["CALDATE"] = ("2026-05-05T00:17:43", "First NGC 6543 anchor exposure UTC")
    header["CALGAPM"] = (24.1833, "Start-time gap from last science frame, min")
    header["CALNLIN"] = (8, "NGC 6543 wavelength anchor line count")
    header["CALRMS"] = (0.186217, "NGC 6543 polynomial internal RMS, Angstrom")
    header["FLUXCAL"] = ("NONE", "No instrumental-response or flux calibration")
    header["RESPCOR"] = (False, "Instrumental response correction applied")
    header["ABSFLUX"] = (False, "Absolute physical flux calibration")
    header["CONTNORM"] = (True, "Counts-only continuum normalization included")
    header["CONTWIN"] = (continuum_window_angstrom, "Continuum window, Angstrom")
    header["CONTPCTL"] = (continuum_percentile, "Rolling continuum percentile")
    header["CONTALG"] = ("QNT-SMOOTH", "Emission-resistant pseudo-continuum")
    header["GRATLPMM"] = (300, "Lines/mm; inferred")
    header["GRATBAS"] = ("DISP+SPAN", "Basis for derived grating density")
    header["ORD2CONT"] = (True, "Second-order contamination risk; data retained")
    header["ORD2STRT"] = (
        second_order_warning_angstrom,
        "Estimated warning start, Angstrom",
    )
    header.add_history("NGC 6543 wavelength transfer only; no response standard was applied.")
    header.add_history(
        "Primary pseudo-continuum: smoothed rolling "
        f"{continuum_percentile:g}th percentile over {continuum_window_angstrom:g} Angstrom."
    )
    header.add_history(
        "Historical 300 Angstrom running-median normalization retained in audit columns."
    )

    table = fits.BinTableHDU.from_columns(
        [
            fits.Column(name="PIXEL", format="D", unit="pix", array=pixel),
            fits.Column(name="WAVELENGTH", format="D", unit="Angstrom", array=wavelength),
            fits.Column(name="RAW_FLUX", format="D", unit="adu", array=flux),
            fits.Column(
                name="RAW_UNCERTAINTY",
                format="D",
                unit="adu",
                array=uncertainty,
            ),
            fits.Column(name="COUNT_RATE", format="D", unit="adu/s", array=count_rate),
            fits.Column(
                name="COUNT_RATE_UNCERTAINTY",
                format="D",
                unit="adu/s",
                array=count_rate_uncertainty,
            ),
            fits.Column(name="CONTINUUM", format="D", unit="adu/s", array=continuum),
            fits.Column(name="NORMALIZED_FLUX", format="D", array=normalized),
            fits.Column(
                name="NORMALIZED_UNCERTAINTY",
                format="D",
                array=normalized_uncertainty,
            ),
            fits.Column(
                name="RUNMED_CONTINUUM",
                format="D",
                unit="adu/s",
                array=legacy_continuum,
            ),
            fits.Column(
                name="RUNMED_NORMALIZED_FLUX",
                format="D",
                array=legacy_normalized,
            ),
            fits.Column(name="ORDER2_RISK", format="L", array=order2_risk),
            fits.Column(name="MASK", format="L", array=final_mask),
        ],
        name="SPECTRUM",
    )
    table.header["AIRORVAC"] = "AIR"

    fits_path = output_directory / "3C273_same_session_normalized.fits"
    fits.HDUList([fits.PrimaryHDU(header=header), table, *provenance_hdus]).writeto(
        fits_path,
        overwrite=True,
        checksum=True,
        output_verify="silentfix",
    )

    csv_path = output_directory / "3C273_same_session_normalized.csv"
    with csv_path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "wavelength_angstrom",
                "count_rate_adu_per_s",
                "count_rate_uncertainty_adu_per_s",
                "continuum_adu_per_s",
                "normalized_flux",
                "normalized_uncertainty",
                "running_median_continuum_adu_per_s",
                "running_median_normalized_flux",
                "second_order_risk",
                "mask",
            ]
        )
        writer.writerows(
            zip(
                wavelength,
                count_rate,
                count_rate_uncertainty,
                continuum,
                normalized,
                normalized_uncertainty,
                legacy_continuum,
                legacy_normalized,
                order2_risk.astype(int),
                final_mask.astype(int),
            )
        )

    plot_path = output_directory / "3C273_same_session_normalized.png"
    plot_valid = ~final_mask & np.isfinite(normalized)
    figure, axes = plt.subplots(
        2,
        1,
        figsize=(15, 8.5),
        sharex=True,
        constrained_layout=True,
    )
    axes[0].plot(wavelength[plot_valid], count_rate[plot_valid], linewidth=0.6)
    axes[0].plot(
        wavelength[plot_valid],
        continuum[plot_valid],
        linewidth=1.6,
        color="#dc6b19",
        label=f"{continuum_percentile:g}th percentile / {continuum_window_angstrom:g} A pseudo-continuum",
    )
    axes[0].plot(
        wavelength[plot_valid],
        legacy_continuum[plot_valid],
        linewidth=1.0,
        color="#7c3aed",
        alpha=0.8,
        label="Historical 300 A running median (audit only)",
    )
    axes[0].set_ylabel("Count rate (ADU/s)")
    axes[0].legend()
    axes[1].plot(wavelength[plot_valid], normalized[plot_valid], linewidth=0.65)
    axes[1].axhline(1.0, color="0.4", linewidth=0.8)
    axes[1].set_ylabel("Continuum-normalized counts")
    axes[1].set_xlabel("Observed wavelength (Angstrom, air)")
    for axis in axes:
        axis.axvspan(
            second_order_warning_angstrom,
            float(np.nanmax(wavelength)),
            color="#f59e0b",
            alpha=0.10,
        )
        axis.grid(alpha=0.2)
    figure.suptitle("3C 273 — same-session NGC 6543 wavelength anchor; no response correction")
    figure.savefig(plot_path, dpi=180)
    plt.close(figure)

    manifest_path = output_directory / "3C273_same_session_normalized.json"
    manifest = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "source": str(source),
        "sourceSha256": _sha256(source),
        "wavelengthCalibration": {
            "source": "NGC 6543 nebular emission lines",
            "firstAnchorExposureUtc": "2026-05-05T00:17:43",
            "startTimeGapFromLastScienceFrameMinutes": 24.1833,
            "lineCount": 8,
            "internalRmsAngstrom": 0.186217,
        },
        "continuumNormalization": {
            "applied": True,
            "windowAngstrom": continuum_window_angstrom,
            "percentile": continuum_percentile,
            "method": "rolling-percentile-plus-Gaussian-smoothing",
            "interpretation": (
                "emission-resistant descriptive pseudo-continuum; not an AGN Fe II "
                "decomposition or response correction"
            ),
            "legacyAuditColumns": {
                "continuum": "RUNMED_CONTINUUM",
                "normalizedFlux": "RUNMED_NORMALIZED_FLUX",
                "method": "300-A-running-median-plus-Gaussian-smoothing",
            },
        },
        "equipment": {
            "gratingLinesPerMm": 300,
            "gratingEvidence": (
                "Inferred from 0.94398 A/pixel measured dispersion and full detector "
                "wavelength span; raw acquisition FITS has no GRATING keyword."
            ),
        },
        "responseCorrectionApplied": False,
        "absoluteFluxCalibrated": False,
        "secondOrderContamination": {
            "warningStartsAtAngstrom": second_order_warning_angstrom,
            "thresholdKind": "estimate-not-measured-cutoff",
            "dataRetained": True,
        },
        "artifacts": {
            "fits": str(fits_path),
            "csv": str(csv_path),
            "plot": str(plot_path),
        },
    }
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return {
        "fits": fits_path,
        "csv": csv_path,
        "plot": plot_path,
        "manifest": manifest_path,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("science", type=Path)
    parser.add_argument("output_directory", type=Path)
    parser.add_argument(
        "--continuum-window",
        type=float,
        default=DEFAULT_CONTINUUM_WINDOW_ANGSTROM,
    )
    parser.add_argument(
        "--second-order-warning",
        type=float,
        default=DEFAULT_SECOND_ORDER_WARNING_ANGSTROM,
    )
    parser.add_argument(
        "--continuum-percentile",
        type=float,
        default=DEFAULT_CONTINUUM_PERCENTILE,
    )
    arguments = parser.parse_args()
    products = finalize(
        arguments.science,
        arguments.output_directory,
        arguments.continuum_window,
        arguments.continuum_percentile,
        arguments.second_order_warning,
    )
    for name, path in products.items():
        print(f"{name:>10}: {path}")


if __name__ == "__main__":
    main()
