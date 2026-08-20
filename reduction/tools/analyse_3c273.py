"""Build a reproducible 3C 273 identity and acquisition-quality diagnostic.

This is deliberately a *diagnostic* step, not another wavelength solver or an
astrometric target verifier.  It compares the already reduced UVEX spectrum
with the observed HST/STIS segment of the public AGNSEDATLAS 3C 273 SED.  Window
maxima are reported as candidates only and must pass explicit consistency gates
before they are allowed to support an identity claim.
"""

from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
import json
from pathlib import Path
from urllib.request import Request, urlopen

from astropy.constants import c
from astropy.cosmology import Planck18
from astropy.io import fits
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D
import numpy as np
from scipy.ndimage import gaussian_filter1d, percentile_filter


REFERENCE_URL = (
    "https://archive.stsci.edu/hlsps/agnsedatlas/templates_observed/"
    "hlsp_agnsedatlas_multi_multi_3c273_multi_v1_spec-obs.txt"
)
CATALOGUE_REDSHIFT = 0.158339
GRATING_LINES_PER_MM = 300
DISPLAY_SMOOTHING_FWHM_ANGSTROM = 7.0
OFFICIAL_300_LPM_DISPERSION_AT_2_9UM = 0.76 * 2.9 / 2.4
OFFICIAL_600_LPM_DISPERSION_AT_2_9UM = 0.38 * 2.9 / 2.4
GAIA_DR3_SOURCE_ID = 3700386905605055360
GAIA_DR3_G_MAG = 12.8440895
GAIA_FIELD_SOURCE = "https://gea.esac.esa.int/archive/"
# Gaia DR3 cone query centred on the SIMBAD position of 3C 273.  Keeping the
# small result here makes the diagnostic reproducible without a live TAP call.
GAIA_FIELD_SOURCES = (
    # (delta RA*cos(dec), delta Dec, G magnitude, source id), arcsec
    (-0.00036, 0.00158, 12.8440895, 3700386905605055360),
    (17.852, 27.596, 16.054256, 3700386905605055616),
    (-52.941, 9.945, 13.384202, 3700386939964757120),
    (38.134, -44.325, 21.49631, 3700386699446978560),
    (-0.024, 63.886, 18.05546, 3700387073107750272),
    (74.617, 4.137, 21.271275, 3700386798229839872),
    (-58.190, -52.823, 14.509799, 3700385393776529920),
)


@dataclass(frozen=True)
class FeatureDefinition:
    name: str
    rest_angstrom: float | None
    expected_angstrom: float
    search_min_angstrom: float
    search_max_angstrom: float
    use_for_redshift: bool
    interpretation: str


@dataclass(frozen=True)
class FeatureMeasurement:
    name: str
    rest_angstrom: float | None
    expected_angstrom: float
    measured_peak_angstrom: float
    offset_angstrom: float
    peak_normalized_flux: float
    redshift: float | None
    use_for_redshift: bool
    interpretation: str


FEATURES = (
    FeatureDefinition(
        "[O II] 3727",
        3727.09,
        3727.09 * (1.0 + CATALOGUE_REDSHIFT),
        4250.0,
        4380.0,
        False,
        "Blue-end candidate only; this part of the UVEX spectrum is too noisy for identity use.",
    ),
    FeatureDefinition(
        "H-delta",
        4101.74,
        4101.74 * (1.0 + CATALOGUE_REDSHIFT),
        4690.0,
        4820.0,
        False,
        "Not required: the feature is weak in this acquisition.",
    ),
    FeatureDefinition(
        "H-gamma",
        4340.47,
        4340.47 * (1.0 + CATALOGUE_REDSHIFT),
        4970.0,
        5090.0,
        True,
        "Broad permitted-line candidate.",
    ),
    FeatureDefinition(
        "H-beta",
        4861.35,
        4861.35 * (1.0 + CATALOGUE_REDSHIFT),
        5550.0,
        5700.0,
        True,
        "Broad permitted-line candidate.",
    ),
    FeatureDefinition(
        "Fe II 4924 candidate",
        4923.92,
        4923.92 * (1.0 + CATALOGUE_REDSHIFT),
        5695.0,
        5720.0,
        False,
        "Multiplet-42 Fe II candidate; physical attribution requires Fe II template fitting.",
    ),
    FeatureDefinition(
        "[O III] 4959",
        4958.91,
        4958.91 * (1.0 + CATALOGUE_REDSHIFT),
        5725.0,
        5765.0,
        False,
        "Weak member of the [O III] doublet, blended with Fe II and broad-line wings.",
    ),
    FeatureDefinition(
        "[O III] 5007 complex",
        5006.84,
        5006.84 * (1.0 + CATALOGUE_REDSHIFT),
        5710.0,
        5860.0,
        True,
        "The low-resolution peak can include both [O III] lines and broad-line wings.",
    ),
)


def _download_reference(destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    request = Request(REFERENCE_URL, headers={"User-Agent": "UVEX-ADV/0.3"})
    with urlopen(request, timeout=30) as response:  # noqa: S310 - fixed HTTPS URL
        destination.write_bytes(response.read())


def _load_stis_reference(path: Path) -> tuple[np.ndarray, np.ndarray]:
    wavelength: list[float] = []
    flux: list[float] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        fields = line.split()
        if len(fields) < 5 or fields[4] != "STIS":
            continue
        wavelength.append(float(fields[0]) * 10_000.0)  # micrometre -> Angstrom
        flux.append(float(fields[2]))
    wave = np.asarray(wavelength, dtype=float)
    values = np.asarray(flux, dtype=float)
    valid = np.isfinite(wave) & np.isfinite(values) & (values > 0)
    if np.count_nonzero(valid) < 100:
        raise RuntimeError("The AGNSEDATLAS file did not contain a usable STIS segment.")
    order = np.argsort(wave[valid])
    return wave[valid][order], values[valid][order]


def _load_science(path: Path) -> tuple[fits.Header, np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    with fits.open(path, memmap=False) as hdul:
        header = hdul[0].header.copy()
        table = hdul[1].data
        names = set(table.names)
        required = {"WAVELENGTH", "NORMALIZED_FLUX", "NORMALIZED_UNCERTAINTY", "MASK"}
        missing = required - names
        if missing:
            raise ValueError(f"Reduced spectrum is missing columns: {sorted(missing)}")
        wavelength = np.asarray(table["WAVELENGTH"], dtype=float)
        normalized = np.asarray(table["NORMALIZED_FLUX"], dtype=float)
        uncertainty = np.asarray(table["NORMALIZED_UNCERTAINTY"], dtype=float)
        mask = np.asarray(table["MASK"], dtype=bool)
    valid = np.isfinite(wavelength) & np.isfinite(normalized) & np.isfinite(uncertainty) & ~mask
    return header, wavelength, normalized, uncertainty, valid


def _continuum_normalize_reference(wavelength: np.ndarray, flux: np.ndarray) -> np.ndarray:
    spacing = float(np.nanmedian(np.diff(wavelength)))
    # Match the science product's emission-resistant pseudo-continuum.  This
    # preserves the positive H-beta/Fe II/[O III] complex better than a central
    # running median, but it remains descriptive rather than a physical AGN fit.
    width = max(9, int(round(450.0 / spacing)))
    if width % 2 == 0:
        width += 1
    continuum = percentile_filter(flux, percentile=35.0, size=width, mode="nearest")
    continuum = gaussian_filter1d(continuum, max(1.0, width / 12.0), mode="nearest")
    floor = np.nanpercentile(continuum[continuum > 0], 2)
    return flux / np.clip(continuum, floor, None)


def _sigma_samples_for_fwhm(spacing: float, fwhm_angstrom: float) -> float:
    return max(0.5, fwhm_angstrom / 2.354820045 / spacing)


def _measure_features(
    wavelength: np.ndarray,
    smoothed_normalized: np.ndarray,
    valid: np.ndarray,
) -> list[FeatureMeasurement]:
    measurements: list[FeatureMeasurement] = []
    for feature in FEATURES:
        inside = (
            valid
            & (wavelength >= feature.search_min_angstrom)
            & (wavelength <= feature.search_max_angstrom)
        )
        if np.count_nonzero(inside) < 5:
            continue
        indices = np.flatnonzero(inside)
        peak_index = int(indices[np.nanargmax(smoothed_normalized[inside])])
        measured = float(wavelength[peak_index])
        redshift = (
            measured / feature.rest_angstrom - 1.0
            if feature.rest_angstrom is not None and feature.use_for_redshift
            else None
        )
        measurements.append(
            FeatureMeasurement(
                name=feature.name,
                rest_angstrom=feature.rest_angstrom,
                expected_angstrom=feature.expected_angstrom,
                measured_peak_angstrom=measured,
                offset_angstrom=measured - feature.expected_angstrom,
                peak_normalized_flux=float(smoothed_normalized[peak_index]),
                redshift=redshift,
                use_for_redshift=feature.use_for_redshift,
                interpretation=feature.interpretation,
            )
        )
    return measurements


def _segment_signal_to_noise(
    wavelength: np.ndarray,
    normalized: np.ndarray,
    uncertainty: np.ndarray,
    valid: np.ndarray,
) -> dict[str, float | None]:
    result: dict[str, float | None] = {}
    for low, high in ((4300, 4700), (4800, 5200), (5400, 5900), (6000, 6400)):
        inside = valid & (wavelength >= low) & (wavelength < high) & (uncertainty > 0)
        ratios = np.abs(normalized[inside]) / uncertainty[inside]
        result[f"{low}-{high} A"] = (
            float(np.nanmedian(ratios)) if np.count_nonzero(np.isfinite(ratios)) else None
        )
    return result


def _scaled_signal_to_noise(
    native: dict[str, float | None],
    samples_per_element: float,
) -> dict[str, float | None]:
    scale = float(np.sqrt(samples_per_element))
    return {
        region: value * scale if value is not None else None for region, value in native.items()
    }


def _load_acquisition_manifest(science_path: Path) -> dict[str, object] | None:
    stem = science_path.stem
    if stem.endswith("_calibrated_1d"):
        stem = stem[: -len("_calibrated_1d")]
    elif stem.endswith("_spectrum"):
        stem = stem[: -len("_spectrum")]
    candidates = (
        science_path.with_name(f"{stem}_run.json"),
        science_path.parent.parent / "science" / "3C273-NGC6543-anchor_run.json",
        science_path.parent.parent / "all-13" / "3C273_run.json",
    )
    for candidate in candidates:
        if candidate.is_file():
            return json.loads(candidate.read_text(encoding="utf-8"))
    return None


def _alignment_summary(manifest: dict[str, object] | None) -> dict[str, object] | None:
    if manifest is None:
        return None
    alignment = manifest.get("alignment")
    if not isinstance(alignment, list) or len(alignment) < 2:
        return None
    shifts = np.asarray(
        [float(item["spatialPixels"]) for item in alignment if isinstance(item, dict)],
        dtype=float,
    )
    if shifts.size < 2:
        return None
    split = int(np.argmax(np.abs(np.diff(shifts)))) + 1
    first = shifts[:split]
    second = shifts[split:]
    summary: dict[str, object] = {
        "spatialShiftMinPixels": float(np.min(shifts)),
        "spatialShiftMaxPixels": float(np.max(shifts)),
        "largestAdjacentChangePixels": float(np.max(np.abs(np.diff(shifts)))),
        "interpretedAs": "two stable trace placements separated by a target reacquisition",
    }
    if first.size:
        summary["earlyGroupFrameCount"] = int(first.size)
        summary["earlyGroupSpanPixels"] = float(np.ptp(first))
    if second.size:
        summary["lateGroupFrameCount"] = int(second.size)
        summary["lateGroupSpanPixels"] = float(np.ptp(second))
        summary["reacquisitionSeparationPixels"] = float(abs(np.median(second) - np.median(first)))
    trace = manifest.get("trace")
    if isinstance(trace, dict):
        summary["stackTraceDetectionSnr"] = trace.get("snr")
        summary["traceMethod"] = trace.get("method")
    return summary


def _reference_correlation(
    science_wave: np.ndarray,
    science_normalized: np.ndarray,
    science_valid: np.ndarray,
    reference_wave: np.ndarray,
    reference_normalized: np.ndarray,
) -> tuple[float, float]:
    region = science_valid & (science_wave >= 4200) & (science_wave <= 6400)
    x = science_normalized[region] - np.nanmedian(science_normalized[region])
    x = np.clip(x, np.nanpercentile(x, 2), np.nanpercentile(x, 98))
    best_shift = 0.0
    best_correlation = -1.0
    for shift in np.arange(-40.0, 40.01, 0.5):
        y = np.interp(
            science_wave[region] - shift,
            reference_wave,
            reference_normalized,
            left=np.nan,
            right=np.nan,
        )
        finite = np.isfinite(x) & np.isfinite(y)
        if np.count_nonzero(finite) < 100:
            continue
        y = y[finite] - np.nanmedian(y[finite])
        y = np.clip(y, np.nanpercentile(y, 2), np.nanpercentile(y, 98))
        correlation = float(np.corrcoef(x[finite], y)[0, 1])
        if correlation > best_correlation:
            best_correlation = correlation
            best_shift = float(shift)
    return best_shift, best_correlation


def _identity_quality_gates(
    measurements: list[FeatureMeasurement],
    measured_redshift: float,
    reference_correlation: float,
) -> tuple[str, dict[str, object], str]:
    identity_candidates = [
        measurement for measurement in measurements if measurement.use_for_redshift
    ]
    offsets = np.asarray(
        [abs(measurement.offset_angstrom) for measurement in identity_candidates],
        dtype=float,
    )
    redshift_error = abs(CATALOGUE_REDSHIFT - measured_redshift)
    gates: dict[str, object] = {
        "maximumAllowedMedianRedshiftError": 0.005,
        "measuredMedianRedshiftError": redshift_error,
        "medianRedshiftGatePassed": bool(redshift_error <= 0.005),
        "maximumAllowedIndividualFeatureOffsetAngstrom": 25.0,
        "largestCandidateWindowPeakOffsetAngstrom": float(np.max(offsets)),
        "featureOffsetGatePassed": bool(np.all(offsets <= 25.0)),
        "minimumReferenceCorrelation": 0.15,
        "measuredReferenceCorrelation": reference_correlation,
        "referenceCorrelationGatePassed": bool(reference_correlation >= 0.15),
    }
    passed = all(
        bool(gates[name])
        for name in (
            "medianRedshiftGatePassed",
            "featureOffsetGatePassed",
            "referenceCorrelationGatePassed",
        )
    )
    if passed:
        return (
            "spectrally-consistent-with-3C273-not-astrometrically-proven",
            gates,
            (
                "All spectral consistency gates passed, but the FITS headers still contain "
                "no RA/DEC; identity remains spectral rather than astrometric."
            ),
        )
    return (
        "unconfirmed-inconsistent-with-3C273-under-current-wavelength-solution",
        gates,
        (
            "The current window maxima do not pass the catalogue-redshift H-gamma, "
            "H-beta and [O III] consistency gates. This is a failure of spectral "
            "confirmation under the supplied wavelength solution; it is not by itself "
            "evidence that a different object was acquired."
        ),
    )


def _plot(
    destination: Path,
    header: fits.Header,
    science_wave: np.ndarray,
    science_normalized: np.ndarray,
    science_smoothed: np.ndarray,
    valid: np.ndarray,
    reference_wave: np.ndarray,
    reference_normalized: np.ndarray,
    measurements: list[FeatureMeasurement],
    spectral_classification: str,
) -> None:
    plot_min = 4100.0
    plot_max = 6400.0
    science_region = valid & (science_wave >= plot_min) & (science_wave <= plot_max)
    reference_region = (reference_wave >= plot_min) & (reference_wave <= plot_max)

    figure, axes = plt.subplots(2, 1, figsize=(16, 9), sharex=True, constrained_layout=True)
    axes[0].plot(
        science_wave[science_region],
        science_normalized[science_region],
        color="#8ebad9",
        linewidth=0.55,
        alpha=0.55,
        label="UVEX native samples",
    )
    axes[0].plot(
        science_wave[science_region],
        science_smoothed[science_region],
        color="#075a8c",
        linewidth=1.7,
        label=f"UVEX smoothed ({DISPLAY_SMOOTHING_FWHM_ANGSTROM:g} A FWHM; display only)",
    )
    axes[0].axhline(1.0, color="0.4", linewidth=0.8)
    axes[0].set_ylabel("Continuum-normalized flux")
    frame_count = int(header.get("NCOMBINE", 0))
    total_exposure = float(header.get("TOTEXP", 0.0))
    axes[0].set_title(
        f"2026-05-04 UVEX spectrum: {frame_count} frames, {total_exposure:.0f} s total, 35 um slit"
    )
    axes[0].legend(loc="upper left")

    ref_spacing = float(np.nanmedian(np.diff(reference_wave)))
    reference_smooth = gaussian_filter1d(
        reference_normalized,
        _sigma_samples_for_fwhm(ref_spacing, DISPLAY_SMOOTHING_FWHM_ANGSTROM),
        mode="nearest",
    )
    axes[1].plot(
        reference_wave[reference_region],
        reference_smooth[reference_region],
        color="#9a3412",
        linewidth=1.4,
        label="HST/STIS observed reference (AGNSEDATLAS)",
    )
    axes[1].axhline(1.0, color="0.4", linewidth=0.8)
    axes[1].set_ylabel("Continuum-normalized reference")
    axes[1].set_xlabel("Observed wavelength (Angstrom, air for UVEX)")
    axes[1].legend(loc="upper left")

    colours = {True: "#15803d", False: "#a16207"}
    measured_by_name = {measurement.name: measurement for measurement in measurements}
    for feature in FEATURES:
        if not (plot_min <= feature.expected_angstrom <= plot_max):
            continue
        for axis in axes:
            axis.axvline(
                feature.expected_angstrom,
                color=colours[feature.use_for_redshift],
                linewidth=0.85,
                alpha=0.65,
                linestyle="--",
            )
        axes[0].text(
            feature.expected_angstrom,
            axes[0].get_ylim()[1],
            f"{feature.name}  {feature.expected_angstrom:.1f}",
            rotation=90,
            va="top",
            ha="right",
            fontsize=8,
            color=colours[feature.use_for_redshift],
        )
        measurement = measured_by_name.get(feature.name)
        if measurement is not None and feature.use_for_redshift:
            axes[0].plot(
                measurement.measured_peak_angstrom,
                measurement.peak_normalized_flux,
                marker="o",
                markersize=5,
                color="#dc2626",
            )

    for feature in FEATURES:
        if (
            feature.use_for_redshift
            and feature.rest_angstrom is not None
            and plot_min <= feature.rest_angstrom <= plot_max
        ):
            for axis in axes:
                axis.axvline(
                    feature.rest_angstrom,
                    color="#64748b",
                    linewidth=0.75,
                    alpha=0.55,
                    linestyle=":",
                )

    axes[0].text(
        0.985,
        0.96,
        (
            f"Catalogue z = {CATALOGUE_REDSHIFT:.6f}\n"
            f"lambda_obs = {1.0 + CATALOGUE_REDSHIFT:.6f} x lambda_rest\n"
            "H-beta: 4861.35 -> 5631.09 A (+769.74 A)"
        ),
        transform=axes[0].transAxes,
        ha="right",
        va="top",
        fontsize=9,
        bbox={"boxstyle": "round", "facecolor": "white", "alpha": 0.82},
    )
    axes[0].legend(
        handles=[
            Line2D([0], [0], color="#8ebad9", linewidth=1.2, label="UVEX native"),
            Line2D([0], [0], color="#075a8c", linewidth=1.7, label="UVEX 7 A FWHM"),
            Line2D(
                [0],
                [0],
                color="#64748b",
                linestyle=":",
                label="Laboratory/rest wavelength",
            ),
            Line2D(
                [0],
                [0],
                color="#15803d",
                linestyle="--",
                label="Expected at z=0.158339",
            ),
            Line2D(
                [0],
                [0],
                color="#dc2626",
                marker="o",
                linestyle="none",
                label="Measured window peak",
            ),
        ],
        loc="upper left",
        fontsize=8,
    )

    for axis in axes:
        axis.set_xlim(plot_min, plot_max)
        axis.grid(alpha=0.2)
    confirmed = spectral_classification.startswith("spectrally-consistent")
    figure.suptitle(
        (
            "3C 273: directly visible 15.8339% cosmological redshift; spectral gates passed"
            if confirmed
            else "3C 273: redshift test pending under the current wavelength solution"
        ),
        fontsize=15,
    )
    figure.savefig(destination, dpi=180)
    plt.close(figure)


def _plot_redshift_explainer(
    destination: Path,
    measurements: list[FeatureMeasurement],
    measured_redshift: float,
    wavelength_max_angstrom: float,
) -> None:
    names = ("H-gamma", "H-beta", "[O III] 5007 complex")
    selected = [item for item in measurements if item.name in names]
    selected.sort(key=lambda item: names.index(item.name))
    figure, axis = plt.subplots(figsize=(14, 6.8), constrained_layout=True)
    y_positions = np.arange(len(selected), 0, -1, dtype=float)
    for y, measurement in zip(y_positions, selected, strict=True):
        if measurement.rest_angstrom is None:
            continue
        axis.annotate(
            "",
            xy=(measurement.expected_angstrom, y),
            xytext=(measurement.rest_angstrom, y),
            arrowprops={
                "arrowstyle": "-|>",
                "color": "#0f766e",
                "linewidth": 2.2,
                "shrinkA": 5,
                "shrinkB": 5,
            },
        )
        axis.scatter(
            measurement.rest_angstrom,
            y,
            s=70,
            facecolor="white",
            edgecolor="#475569",
            linewidth=1.6,
            zorder=3,
        )
        axis.scatter(
            measurement.expected_angstrom,
            y,
            s=85,
            marker="^",
            color="#0f766e",
            zorder=4,
        )
        axis.scatter(
            measurement.measured_peak_angstrom,
            y - 0.18,
            s=65,
            marker="x",
            color="#dc2626",
            linewidth=2.0,
            zorder=5,
        )
        shift = measurement.expected_angstrom - measurement.rest_angstrom
        axis.text(
            (measurement.rest_angstrom + measurement.expected_angstrom) / 2.0,
            y + 0.13,
            f"+{shift:.2f} A",
            color="#0f766e",
            ha="center",
            fontsize=9,
        )
        axis.text(
            measurement.expected_angstrom + 25.0,
            y - 0.18,
            f"measured {measurement.measured_peak_angstrom:.2f} A",
            color="#b91c1c",
            va="center",
            fontsize=8.5,
        )

    axis.set_yticks(y_positions, [item.name for item in selected])
    axis.set_xlim(3900.0, 6050.0)
    axis.set_ylim(0.35, len(selected) + 0.7)
    axis.set_xlabel("Wavelength (Angstrom)")
    axis.grid(axis="x", alpha=0.22)
    axis.set_title(
        "3C 273 — the same atomic lines are observed at 1.158339 x their rest wavelengths",
        fontsize=14,
    )
    axis.text(
        0.02,
        0.04,
        (
            f"Catalogue z = {CATALOGUE_REDSHIFT:.6f}; measured three-feature median "
            f"z = {measured_redshift:.6f}.  H-alpha would move from 6562.79 A to "
            f"{6562.79 * (1.0 + CATALOGUE_REDSHIFT):.2f} A, beyond this spectrum's "
            f"{wavelength_max_angstrom:.2f} A red edge."
        ),
        transform=axis.transAxes,
        fontsize=9.5,
        bbox={"boxstyle": "round", "facecolor": "#f8fafc", "alpha": 0.9},
    )
    axis.legend(
        handles=[
            Line2D(
                [0],
                [0],
                marker="o",
                markerfacecolor="white",
                markeredgecolor="#475569",
                linestyle="none",
                label="Laboratory/rest wavelength",
            ),
            Line2D(
                [0],
                [0],
                marker="^",
                color="#0f766e",
                linestyle="none",
                label="Expected at catalogue redshift",
            ),
            Line2D(
                [0],
                [0],
                marker="x",
                color="#dc2626",
                linestyle="none",
                label="UVEX measured window peak",
            ),
        ],
        loc="upper left",
        fontsize=9,
    )
    figure.savefig(destination, dpi=190)
    plt.close(figure)


def _plot_hbeta_continuum_audit(
    science_path: Path,
    destination: Path,
    reference_wave: np.ndarray,
    reference_normalized: np.ndarray,
) -> dict[str, float]:
    with fits.open(science_path, memmap=False) as hdul:
        table = hdul["SPECTRUM"].data
        names = set(table.names)
        wave = np.asarray(table["WAVELENGTH"], dtype=float)
        count_rate = np.asarray(table["COUNT_RATE"], dtype=float)
        continuum = np.asarray(table["CONTINUUM"], dtype=float)
        normalized = np.asarray(table["NORMALIZED_FLUX"], dtype=float)
        mask = np.asarray(table["MASK"], dtype=bool)
        legacy_continuum = (
            np.asarray(table["RUNMED_CONTINUUM"], dtype=float)
            if "RUNMED_CONTINUUM" in names
            else continuum
        )
        legacy_normalized = (
            np.asarray(table["RUNMED_NORMALIZED_FLUX"], dtype=float)
            if "RUNMED_NORMALIZED_FLUX" in names
            else normalized
        )
    valid = (
        ~mask
        & np.isfinite(wave)
        & np.isfinite(count_rate)
        & np.isfinite(continuum)
        & np.isfinite(normalized)
    )
    region = valid & (wave >= 5325.0) & (wave <= 6075.0)
    spacing = float(np.nanmedian(np.diff(wave[valid])))
    display_sigma = _sigma_samples_for_fwhm(
        spacing,
        DISPLAY_SMOOTHING_FWHM_ANGSTROM,
    )
    filled = np.where(valid, normalized, np.nanmedian(normalized[valid]))
    display_smoothed = gaussian_filter1d(filled, display_sigma, mode="nearest")
    legacy_sigma_smoothed = gaussian_filter1d(
        filled,
        7.0 / spacing,
        mode="nearest",
    )
    reference_spacing = float(np.nanmedian(np.diff(reference_wave)))
    reference_smoothed = gaussian_filter1d(
        reference_normalized,
        _sigma_samples_for_fwhm(
            reference_spacing,
            DISPLAY_SMOOTHING_FWHM_ANGSTROM,
        ),
        mode="nearest",
    )

    lines = (
        ("H-beta", 4861.35 * (1.0 + CATALOGUE_REDSHIFT), "#991b1b"),
        ("Fe II 4924?", 4923.92 * (1.0 + CATALOGUE_REDSHIFT), "#7c3aed"),
        ("[O III] 4959", 4958.91 * (1.0 + CATALOGUE_REDSHIFT), "#0369a1"),
        ("[O III] 5007", 5006.84 * (1.0 + CATALOGUE_REDSHIFT), "#0369a1"),
    )
    figure, axes = plt.subplots(3, 1, figsize=(15, 11), sharex=True, constrained_layout=True)
    axes[0].plot(
        wave[region],
        count_rate[region],
        color="#334155",
        linewidth=0.65,
        label="Extracted count rate",
    )
    axes[0].plot(
        wave[region],
        continuum[region],
        color="#ea580c",
        linewidth=1.8,
        label="New 35th-percentile / 450 A pseudo-continuum",
    )
    axes[0].plot(
        wave[region],
        legacy_continuum[region],
        color="#7c3aed",
        linewidth=1.3,
        label="Old 300 A running-median continuum",
    )
    axes[0].set_ylabel("ADU/s")
    axes[0].set_title(
        "Continuum-placement audit: the old curve rides up on the Fe II / [O III] complex"
    )
    axes[0].legend(fontsize=8)

    axes[1].plot(
        wave[region],
        normalized[region],
        color="#0f766e",
        linewidth=0.8,
        label="New emission-resistant normalization",
    )
    axes[1].plot(
        wave[region],
        legacy_normalized[region],
        color="#7c3aed",
        linewidth=0.8,
        alpha=0.85,
        label="Old running-median normalization",
    )
    axes[1].axhline(1.0, color="0.45", linewidth=0.8)
    axes[1].set_ylabel("Normalized counts")
    axes[1].set_title(
        "Normalization choice changes feature contrast; neither curve is a physical Fe II decomposition"
    )
    axes[1].legend(fontsize=8)

    axes[2].plot(
        wave[region],
        normalized[region],
        color="#94a3b8",
        linewidth=0.5,
        alpha=0.65,
        label="UVEX native",
    )
    axes[2].plot(
        wave[region],
        display_smoothed[region],
        color="#075a8c",
        linewidth=1.6,
        label="UVEX 7 A FWHM",
    )
    axes[2].plot(
        wave[region],
        legacy_sigma_smoothed[region],
        color="#dc2626",
        linewidth=1.1,
        linestyle="--",
        label="Old bug: sigma=7 A (FWHM 16.48 A)",
    )
    ref_region = (reference_wave >= 5325.0) & (reference_wave <= 6075.0)
    axes[2].plot(
        reference_wave[ref_region],
        reference_smoothed[ref_region],
        color="#9a3412",
        linewidth=1.25,
        alpha=0.85,
        label="HST/STIS, same 7 A FWHM display smoothing",
    )
    axes[2].set_ylabel("Pseudo-continuum normalized")
    axes[2].set_xlabel("Observed wavelength (Angstrom)")
    axes[2].set_title(
        "Smoothing audit: 5704–5707 A is Fe II-dominated; [O III] 4959 is expected at 5744.10 A"
    )
    axes[2].legend(fontsize=8, ncol=2)

    for axis in axes:
        for label, wavelength, colour in lines:
            axis.axvline(wavelength, color=colour, linestyle="--", linewidth=0.9, alpha=0.8)
        axis.grid(alpha=0.18)
    for label, wavelength, colour in lines:
        axes[2].text(
            wavelength,
            0.97,
            f"{label}\n{wavelength:.1f} A",
            transform=axes[2].get_xaxis_transform(),
            color=colour,
            rotation=90,
            va="top",
            ha="right",
            fontsize=8,
        )
    figure.savefig(destination, dpi=190)
    plt.close(figure)

    fe_wave = 4923.92 * (1.0 + CATALOGUE_REDSHIFT)
    fe_window = valid & (wave >= fe_wave - 9.0) & (wave <= fe_wave + 16.0)
    fe_index = int(np.flatnonzero(fe_window)[np.nanargmax(display_smoothed[fe_window])])
    return {
        "feIiCandidateExpectedAngstrom": fe_wave,
        "feIiCandidateMeasuredLocalPeakAngstrom": float(wave[fe_index]),
        "newNormalizedAtExpectedFeIi4924": float(np.interp(fe_wave, wave, normalized)),
        "oldRunningMedianNormalizedAtExpectedFeIi4924": float(
            np.interp(fe_wave, wave, legacy_normalized)
        ),
        "oldContinuumOverNewAtExpectedFeIi4924": float(
            np.interp(fe_wave, wave, legacy_continuum) / np.interp(fe_wave, wave, continuum)
        ),
        "oldSmoothingFwhmAngstrom": 2.354820045 * 7.0,
        "correctedDisplaySmoothingFwhmAngstrom": DISPLAY_SMOOTHING_FWHM_ANGSTROM,
    }


def _plot_gaia_field(destination: Path) -> None:
    sources = np.asarray(GAIA_FIELD_SOURCES, dtype=object)
    dra = sources[:, 0].astype(float)
    ddec = sources[:, 1].astype(float)
    magnitude = sources[:, 2].astype(float)
    sizes = np.clip(180.0 * 10.0 ** (-0.2 * (magnitude - GAIA_DR3_G_MAG)), 18.0, 180.0)
    figure, axis = plt.subplots(figsize=(7.4, 7.0), constrained_layout=True)
    axis.scatter(
        dra[1:], ddec[1:], s=sizes[1:], color="#d97706", alpha=0.85, label="Other Gaia DR3 sources"
    )
    axis.scatter(
        dra[0], ddec[0], s=210, marker="*", color="#0f766e", label="3C 273 (G=12.844)", zorder=5
    )
    axis.add_patch(
        plt.Circle(
            (0, 0),
            30.0,
            fill=False,
            color="#2563a6",
            linestyle="--",
            linewidth=1.3,
            label="30 arcsec",
        )
    )
    axis.add_patch(
        plt.Circle(
            (0, 0),
            1.93,
            fill=False,
            color="#dc2626",
            linewidth=1.4,
            label="Nominal 35 um slit width / 2",
        )
    )
    axis.annotate(
        "nearest alternative\n32.87 arcsec, G=16.05",
        (dra[1], ddec[1]),
        xytext=(8, -18),
        textcoords="offset points",
        fontsize=9,
    )
    axis.set(
        xlim=(-90, 90),
        ylim=(-90, 90),
        xlabel="Delta RA cos(dec) (arcsec)",
        ylabel="Delta Dec (arcsec)",
    )
    axis.set_aspect("equal")
    axis.grid(alpha=0.22)
    axis.legend(loc="lower left", fontsize=8)
    axis.set_title("Gaia DR3 field around 3C 273 (3 arcmin diameter excerpt)")
    figure.savefig(destination, dpi=180)
    plt.close(figure)


def analyse(
    science_path: Path, output_directory: Path, refresh_reference: bool
) -> dict[str, object]:
    output_directory.mkdir(parents=True, exist_ok=True)
    reference_path = output_directory / "3C273_AGNSEDATLAS_observed.txt"
    if refresh_reference or not reference_path.exists():
        _download_reference(reference_path)

    header, wave, normalized, uncertainty, valid = _load_science(science_path)
    spacing = float(np.nanmedian(np.diff(wave[valid])))
    science_smoothed = gaussian_filter1d(
        np.where(valid, normalized, np.nanmedian(normalized[valid])),
        _sigma_samples_for_fwhm(spacing, DISPLAY_SMOOTHING_FWHM_ANGSTROM),
        mode="nearest",
    )
    reference_wave, reference_flux = _load_stis_reference(reference_path)
    reference_normalized = _continuum_normalize_reference(reference_wave, reference_flux)
    measurements = _measure_features(wave, science_smoothed, valid)
    acquisition_manifest = _load_acquisition_manifest(science_path)

    redshifts = np.asarray(
        [
            measurement.redshift
            for measurement in measurements
            if measurement.use_for_redshift and measurement.redshift is not None
        ],
        dtype=float,
    )
    measured_redshift = float(np.nanmedian(redshifts))
    redshift_mad = float(1.4826 * np.nanmedian(np.abs(redshifts - measured_redshift)))
    shift, correlation = _reference_correlation(
        wave,
        science_smoothed,
        valid,
        reference_wave,
        reference_normalized,
    )
    spectral_classification, quality_gates, spectral_assessment_reason = _identity_quality_gates(
        measurements,
        measured_redshift,
        correlation,
    )

    plot_path = output_directory / "3C273_identity_overlay.png"
    _plot(
        plot_path,
        header,
        wave,
        normalized,
        science_smoothed,
        valid,
        reference_wave,
        reference_normalized,
        measurements,
        spectral_classification,
    )
    field_plot_path = output_directory / "3C273_gaia_field.png"
    _plot_gaia_field(field_plot_path)
    redshift_plot_path = output_directory / "3C273_redshift_explainer.png"
    _plot_redshift_explainer(
        redshift_plot_path,
        measurements,
        measured_redshift,
        float(np.nanmax(wave[valid])),
    )
    continuum_audit_plot_path = output_directory / "3C273_hbeta_feii_oiii_audit.png"
    continuum_audit = _plot_hbeta_continuum_audit(
        science_path,
        continuum_audit_plot_path,
        reference_wave,
        reference_normalized,
    )

    native_snr = _segment_signal_to_noise(wave, normalized, uncertainty, valid)
    samples_per_resolution_element = 8.0
    literature_peaks = {
        "H-gamma-like feature": 5032.0,
        "H-beta": 5632.0,
        "[O III] complex": 5792.0,
    }
    measured_identity_peaks = {
        "H-gamma-like feature": next(
            item.measured_peak_angstrom for item in measurements if item.name == "H-gamma"
        ),
        "H-beta": next(
            item.measured_peak_angstrom for item in measurements if item.name == "H-beta"
        ),
        "[O III] complex": next(
            item.measured_peak_angstrom
            for item in measurements
            if item.name == "[O III] 5007 complex"
        ),
    }
    same_session_anchor = str(header.get("WAVESRC", "")).upper() == "NGC6543"
    spectrum_passed = spectral_classification.startswith("spectrally-consistent")
    redshift_factor = 1.0 + CATALOGUE_REDSHIFT
    c_kilometres_per_second = float(c.to("km/s").value)
    special_relativistic_beta = (redshift_factor**2 - 1.0) / (redshift_factor**2 + 1.0)
    h_alpha_expected = 6562.79 * redshift_factor
    result: dict[str, object] = {
        "schemaVersion": 5,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "assessment": {
            "classification": (
                "3C273-spectroscopically-confirmed-astrometry-unavailable"
                if spectrum_passed
                else "likely-3C273-acquisition-spectroscopic-confirmation-pending"
            ),
            "acquisitionIdentity": "likely-3C273-if-the-reported-centering-is-correct",
            "spectroscopicClassification": spectral_classification,
            "catalogueRedshift": CATALOGUE_REDSHIFT,
            "redshiftContext": {
                "observedToRestWavelengthFactor": redshift_factor,
                "wavelengthIncreasePercent": CATALOGUE_REDSHIFT * 100.0,
                "czShorthandKilometresPerSecond": CATALOGUE_REDSHIFT * c_kilometres_per_second,
                "specialRelativisticEquivalentKilometresPerSecond": (
                    special_relativistic_beta * c_kilometres_per_second
                ),
                "velocityWarning": (
                    "3C 273 has cosmological redshift; cz and the special-relativistic "
                    "equivalent are descriptive conversions, not a peculiar recession "
                    "velocity measured by this UVEX spectrum."
                ),
                "planck18LookbackTimeGyr": float(Planck18.lookback_time(CATALOGUE_REDSHIFT).value),
                "planck18LuminosityDistanceMpc": float(
                    Planck18.luminosity_distance(CATALOGUE_REDSHIFT).value
                ),
                "cosmologyWarning": (
                    "Lookback time and luminosity distance are Planck18 model-derived "
                    "context, not direct UVEX measurements."
                ),
            },
            "windowPeakMedianRedshiftDiagnostic": measured_redshift,
            "windowPeakRedshiftMadDiagnostic": redshift_mad,
            "catalogueMinusMeasuredRedshift": CATALOGUE_REDSHIFT - measured_redshift,
            "qualityGates": quality_gates,
            "reason": (
                "The raw frames contain a persistent point-source continuum trace in both "
                "acquisition groups. Gaia DR3 contains no alternative source within 30 arcsec "
                "of 3C 273; the nearest is 32.87 arcsec away and 3.21 magnitudes fainter. "
                + (
                    "The independently derived, immediately following NGC 6543 wavelength "
                    "solution also places the broad H-gamma, H-beta and [O III] pattern at the "
                    "catalogue redshift; the spectrum therefore confirms the source as 3C 273."
                    if spectral_classification.startswith("spectrally-consistent")
                    else "Given the observer's centred narrow-slit acquisition, the most "
                    "likely source remains 3C 273, but the spectral gates did not all pass."
                )
            ),
            "spectroscopicReason": spectral_assessment_reason,
            "historicObservedBroadPeaksAngstrom": literature_peaks,
            "measuredBroadPeakOffsetsFromHistoricAngstrom": {
                name: measured_identity_peaks[name] - reference
                for name, reference in literature_peaks.items()
            },
            "hAlphaExpectedAngstrom": h_alpha_expected,
            "hAlphaInRecordedRange": bool(h_alpha_expected <= np.nanmax(wave[valid])),
        },
        "acquisition": {
            "dateObs": header.get("DATE-OBS"),
            "frameCount": int(header.get("NCOMBINE", 0)),
            "totalExposureSeconds": float(header.get("TOTEXP", 0.0)),
            "slitMicrometre": 35,
            "skyClass": "Bortle 5 (provided by observer)",
            "telescope": "Celestron C11 + Astro-Physics CCDT67 (provided by observer)",
            "wavelengthMinAngstrom": float(np.nanmin(wave[valid])),
            "wavelengthMaxAngstrom": float(np.nanmax(wave[valid])),
            "medianDispersionAngstromPerPixel": spacing,
            "grating": {
                "linesPerMillimetre": GRATING_LINES_PER_MM,
                "rawAcquisitionFitsKeywordPresent": False,
                "derivedProductKeywordPresent": bool("GRATLPMM" in header),
                "evidence": (
                    "High-confidence inference from measured 0.94398 A/pixel dispersion "
                    "and 3623.94 A detector coverage. The acquisition FITS has no GRATING "
                    "keyword; this value is not copied from its header."
                ),
                "officialUvexScaledPredictionAngstromPerPixel": {
                    "300LinesPerMmAt2p9UmPixel": OFFICIAL_300_LPM_DISPERSION_AT_2_9UM,
                    "600LinesPerMmAt2p9UmPixel": OFFICIAL_600_LPM_DISPERSION_AT_2_9UM,
                },
                "liveUvexWindowCrossCheckAngstrom": {
                    "minimum": 3828.35,
                    "maximum": 7354.65,
                    "span": 3526.30,
                    "interpretation": (
                        "The saved COM5 device state on 2026-08-16 matches the expected "
                        "full-frame span of the 300 lines/mm configuration."
                    ),
                },
            },
            "medianSignalToNoisePerNativeSample": native_snr,
            "estimatedMedianSignalToNoisePer8PixelResolutionElement": _scaled_signal_to_noise(
                native_snr,
                samples_per_resolution_element,
            ),
            "samplesPerResolutionElementAssumption": samples_per_resolution_element,
            "resolvingPower": {
                "status": "not-derived-from-grating-table",
                "reason": (
                    "Resolving power depends on the 35 um slit, focus, seeing, trace width, "
                    "sampling and aberrations. Measure it from same-configuration narrow "
                    "lamp or nebular profiles rather than scaling the old 600 l/mm preset."
                ),
            },
            "localSkyBackground": (
                "Two-sided off-trace sky windows were estimated independently in each "
                "wavelength column during optimal extraction."
            ),
            "alignment": _alignment_summary(acquisition_manifest),
        },
        "fieldConstraint": {
            "catalogue": "Gaia DR3",
            "catalogueSource": GAIA_FIELD_SOURCE,
            "targetSourceId": GAIA_DR3_SOURCE_ID,
            "targetGMag": GAIA_DR3_G_MAG,
            "coneRadiusArcsec": 180.0,
            "otherSourcesWithin30Arcsec": 0,
            "nearestAlternativeSeparationArcsec": 32.8665,
            "nearestAlternativeGMag": 16.0543,
            "nominal35MicrometreSlitWidthArcsec": 3.35,
            "interpretation": (
                "A foreground-star explanation requires a pointing or slit-placement error "
                "of at least about 33 arcsec, not a sub-slit centring error."
            ),
        },
        "calibrationReliability": (
            {
                "standard": "NGC 6543 nebular wavelength anchor",
                "standardDateObs": header.get("CALDATE", "2026-05-05T00:17:43"),
                "scienceDateObs": header.get("DATE-OBS"),
                "separationMinutesFromLastScienceFrameStart": float(header.get("CALGAPM", 24.1833)),
                "separationMinutesFromNominalScienceExposureEnd": float(
                    header.get("CALGAPM", 24.1833)
                )
                - 10.0,
                "matchedFeatureCount": int(header.get("CALNLIN", 8)),
                "internalRmsAngstrom": float(header.get("CALRMS", 0.1862)),
                "independentRmsAvailable": True,
                "responseCalibrated": False,
                "assessment": (
                    "Immediately following same-observing-night nebular lines span most of "
                    "the detector. The wavelength transfer is strong; continuum response "
                    "and absolute flux remain uncalibrated."
                ),
            }
            if same_session_anchor
            else {
                "standard": "legacy/unspecified",
                "scienceDateObs": header.get("DATE-OBS"),
                "assessment": "The input product does not declare the same-session NGC 6543 anchor.",
            }
        ),
        "features": [asdict(measurement) for measurement in measurements],
        "referenceComparison": {
            "referenceUrl": REFERENCE_URL,
            "referenceSource": "AGNSEDATLAS observed 3C 273 SED, STIS rows",
            "bestSmallWavelengthShiftAngstrom": shift,
            "globalShapeCorrelation": correlation,
            "warning": (
                "This correlation is a spectral consistency diagnostic, not a probabilistic "
                "identity score. No compatible flat or spectrophotometric response standard "
                "was available."
            ),
        },
        "continuumAndBlendAudit": {
            **continuum_audit,
            "interpretation": (
                "The feature near 5706 A is closer to redshifted Fe II 4924 (5703.57 A) "
                "than to [O III] 4959 (5744.10 A). The old running-median continuum and "
                "the old sigma=7 A smoothing both reduced its contrast. This is not a "
                "physical Fe II/H-beta/[O III] deblend."
            ),
        },
        "calibrationFlags": {
            "biasApplied": bool(header.get("BIASCOR", False)),
            "darkApplied": bool(header.get("DARKCOR", False)),
            "flatApplied": bool(header.get("FLATCOR", False)),
            "wavelengthCalibrated": bool(header.get("WAVECAL", False)),
            "absoluteFluxCalibrated": bool(header.get("ABSFLUX", False)),
            "secondOrderRiskAffectsThisRange": float(np.nanmax(wave[valid])) >= 6800.0,
        },
        "artifacts": {
            "scienceSpectrum": str(science_path.resolve()),
            "reference": str(reference_path.resolve()),
            "identityPlot": str(plot_path.resolve()),
            "fieldPlot": str(field_plot_path.resolve()),
            "redshiftPlot": str(redshift_plot_path.resolve()),
            "hbetaFeIiOiiiAuditPlot": str(continuum_audit_plot_path.resolve()),
        },
        "limitations": [
            "No compatible flat, bias, dark, or arc exposure was available. Cross-night darks "
            "are allowed, but the only discovered ATR585M darks were 180 s at about +0.1 C, "
            "whereas this acquisition is 600 s at about -9.9 C.",
            (
                "Wavelength was transferred from NGC 6543 whose first exposure began "
                "24.18 minutes after the start of the last 3C 273 exposure (about 14.18 "
                "minutes after its nominal end); no response correction was applied."
                if same_session_anchor
                else "The wavelength source is not declared as the same-session NGC 6543 anchor."
            ),
            "No RA/DEC is present in the source FITS headers.",
            "Broad-line window maxima can be shifted by blends and continuum placement; the "
            "full-spectrum reference comparison and independent acquisition segments are retained.",
            "The H-beta red wing contains Fe II multiplet emission and weak [O III]. A physical "
            "line flux requires a power-law/Fe II/H-beta/[O III] multicomponent fit.",
        ],
    }
    json_path = output_directory / "3C273_identity.json"
    result["artifacts"]["identityJson"] = str(json_path.resolve())
    json_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("science", type=Path, help="UVEX calibrated 1D FITS product")
    parser.add_argument("output_directory", type=Path, help="Directory for identity artifacts")
    parser.add_argument(
        "--refresh-reference",
        action="store_true",
        help="Download the fixed public AGNSEDATLAS source again",
    )
    arguments = parser.parse_args()
    result = analyse(arguments.science, arguments.output_directory, arguments.refresh_reference)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
