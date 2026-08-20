from __future__ import annotations

import csv
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
from typing import Iterable

from astropy.io import fits
import matplotlib

matplotlib.use("Agg")
from matplotlib import pyplot as plt
import numpy as np
from scipy.interpolate import UnivariateSpline
from scipy.ndimage import gaussian_filter1d, median_filter
from scipy.signal import find_peaks

from .stellar import BALMER_LINES, load_stellar_template


DEFAULT_NEBULAR_LINES_ANGSTROM = np.asarray(
    [
        4101.74,  # H-delta
        4340.47,  # H-gamma
        4685.68,  # He II
        4861.35,  # H-beta
        4958.91,  # [O III]
        5006.84,  # [O III]
        5411.52,  # He II
        5875.62,  # He I
        6548.05,  # [N II]
        6562.79,  # H-alpha
        6583.45,  # [N II]
        6678.15,  # He I
        6716.44,  # [S II]
        6730.82,  # [S II]
        7135.79,  # [Ar III]
    ],
    dtype=float,
)


@dataclass(slots=True)
class ReducedSpectrumData:
    path: Path
    header: fits.Header
    pixel: np.ndarray
    wavelength_angstrom: np.ndarray
    flux_adu: np.ndarray
    uncertainty_adu: np.ndarray
    mask: np.ndarray
    exposure_s: float


@dataclass(slots=True)
class RelativeResponse:
    wavelength_angstrom: np.ndarray
    observed_rate: np.ndarray
    template_flux: np.ndarray
    raw_response: np.ndarray
    response: np.ndarray
    mask: np.ndarray
    standard_name: str
    standard_path: Path
    template_path: Path
    fractional_scatter: float


@dataclass(slots=True)
class ZeroPointCorrection:
    measured_offset_angstrom: float
    applied_offset_angstrom: float
    rms_angstrom: float
    observed_wavelengths: np.ndarray
    reference_wavelengths: np.ndarray
    scale: float = 1.0
    pivot_angstrom: float = 0.0
    method: str = "common-offset"

    def apply(self, wavelength_angstrom: np.ndarray) -> np.ndarray:
        return (
            self.pivot_angstrom
            + self.scale * (wavelength_angstrom - self.pivot_angstrom)
            + self.applied_offset_angstrom
        )


@dataclass(slots=True)
class CalibratedSpectrum:
    source: ReducedSpectrumData
    wavelength_angstrom: np.ndarray
    relative_flux: np.ndarray
    relative_uncertainty: np.ndarray
    continuum: np.ndarray
    normalized_flux: np.ndarray
    normalized_uncertainty: np.ndarray
    response: np.ndarray
    mask: np.ndarray
    response_standard: str
    response_template: Path
    zero_point: ZeroPointCorrection | None
    second_order_start_angstrom: float | None
    second_order_status: str
    second_order_empirical_onset_angstrom: float | None
    second_order_diagnostic_marker_angstrom: float | None
    second_order_assessment_path: Path | None


def load_reduced_spectrum(path: str | Path) -> ReducedSpectrumData:
    file_path = Path(path).expanduser().resolve()
    with fits.open(file_path, memmap=False) as hdul:
        if "SPECTRUM" not in hdul:
            raise ValueError(f"{file_path} has no SPECTRUM extension.")
        data = hdul["SPECTRUM"].data
        if "WAVELENGTH" not in data.names:
            raise ValueError(f"{file_path} is not wavelength calibrated.")
        header = hdul[0].header.copy()
        pixel = np.asarray(data["PIXEL"], dtype=float)
        wavelength = np.asarray(data["WAVELENGTH"], dtype=float)
        flux = np.asarray(data["FLUX"], dtype=float)
        uncertainty = np.asarray(data["UNCERTAINTY"], dtype=float)
        mask = np.asarray(data["MASK"], dtype=bool)
    exposure = _header_float(header, "EXPTIME", "EXPOSURE", "EXP_TIME")
    if exposure is None or exposure <= 0:
        raise ValueError(f"{file_path} has no positive exposure time in its primary header.")
    arrays = (pixel, wavelength, flux, uncertainty, mask)
    if any(array.ndim != 1 or array.size != wavelength.size for array in arrays):
        raise ValueError(f"{file_path} has inconsistent one-dimensional spectrum columns.")
    if not np.all(np.diff(wavelength) > 0):
        raise ValueError(f"{file_path} wavelength axis is not strictly increasing.")
    return ReducedSpectrumData(
        path=file_path,
        header=header,
        pixel=pixel,
        wavelength_angstrom=wavelength,
        flux_adu=flux,
        uncertainty_adu=uncertainty,
        mask=mask,
        exposure_s=float(exposure),
    )


def derive_relative_response(
    standard: ReducedSpectrumData,
    template_path: str | Path,
    standard_name: str,
    smoothing_angstrom: float = 100.0,
) -> RelativeResponse:
    """Derive a smooth, relative instrumental response from a standard star.

    The ISIS Pickles spectra carry a useful broad spectral energy distribution,
    but this workflow lacks slit-loss and atmospheric-extinction information.
    Consequently the result is deliberately normalized and labelled *relative*,
    never as an absolute physical flux calibration.
    """

    template = load_stellar_template(template_path)
    wavelength = standard.wavelength_angstrom
    template_flux = np.interp(
        wavelength,
        template.wavelength_angstrom,
        template.flux,
        left=np.nan,
        right=np.nan,
    )
    observed_rate = standard.flux_adu / standard.exposure_s
    invalid = (
        standard.mask
        | ~np.isfinite(observed_rate)
        | ~np.isfinite(template_flux)
        | (observed_rate <= 0)
        | (template_flux <= 0)
    )
    raw_response = np.divide(
        observed_rate,
        template_flux,
        out=np.full_like(observed_rate, np.nan),
        where=~invalid,
    )

    fit_valid = ~invalid
    for line in BALMER_LINES:
        fit_valid &= np.abs(wavelength - line) > 35.0
    # Deep terrestrial bands and detector edges should not shape the response fit.
    for low, high in ((6270.0, 6335.0), (6850.0, 6960.0), (7150.0, 7350.0)):
        fit_valid &= ~((wavelength >= low) & (wavelength <= high))
    edge = max(8, wavelength.size // 100)
    fit_valid[:edge] = False
    fit_valid[-edge:] = False
    if np.count_nonzero(fit_valid) < max(100, wavelength.size // 5):
        raise RuntimeError("Too little valid standard-star coverage to derive a response curve.")

    log_ratio = np.full_like(raw_response, np.nan)
    log_ratio[fit_valid] = np.log(raw_response[fit_valid])
    filled = _interpolate_valid(log_ratio, fit_valid)
    dispersion = float(np.median(np.diff(wavelength)))
    window = _odd_window(max(31, int(round(smoothing_angstrom / dispersion))))
    broad = median_filter(filled, size=window, mode="nearest")
    broad = gaussian_filter1d(broad, sigma=max(3.0, window / 6.0), mode="nearest")
    response = np.exp(broad)

    reference_band = fit_valid & (wavelength >= 5400.0) & (wavelength <= 5600.0)
    if np.count_nonzero(reference_band) < 10:
        reference_band = fit_valid
    normalizer = float(np.nanmedian(response[reference_band]))
    if not np.isfinite(normalizer) or normalizer <= 0:
        raise RuntimeError("The derived response curve has no positive normalization level.")
    response /= normalizer
    raw_response /= normalizer

    residual = raw_response[fit_valid] / response[fit_valid] - 1.0
    scatter = 1.4826 * float(np.nanmedian(np.abs(residual - np.nanmedian(residual))))
    response_mask = invalid | ~np.isfinite(response) | (response <= 0)
    return RelativeResponse(
        wavelength_angstrom=wavelength.copy(),
        observed_rate=observed_rate,
        template_flux=template_flux,
        raw_response=raw_response,
        response=response,
        mask=response_mask,
        standard_name=standard_name,
        standard_path=standard.path,
        template_path=template.path,
        fractional_scatter=scatter,
    )


def refine_emission_zero_point(
    science: ReducedSpectrumData,
    reference_lines_angstrom: Iterable[float] = DEFAULT_NEBULAR_LINES_ANGSTROM,
    maximum_offset_angstrom: float = 60.0,
    match_tolerance_angstrom: float = 3.0,
    minimum_matches: int = 4,
) -> ZeroPointCorrection:
    """Refine a transferred wavelength solution from known emission lines.

    A common offset is preferred for compact line sets.  When at least five
    reliable lines span 1000 Angstrom, a tightly bounded affine correction is
    also allowed.  This handles the small dispersion-scale change seen when a
    UVEX grating returns close to, but not exactly to, a prior position.
    """

    wavelength = science.wavelength_angstrom
    flux = science.flux_adu
    valid = ~science.mask & np.isfinite(flux)
    filled = _interpolate_valid(flux, valid)
    continuum = gaussian_filter1d(filled, sigma=80.0, mode="nearest")
    residual = filled - continuum
    noise = 1.4826 * float(np.median(np.abs(residual[valid] - np.median(residual[valid]))))
    if not np.isfinite(noise) or noise <= 0:
        reported = science.uncertainty_adu[valid]
        reported = reported[np.isfinite(reported) & (reported > 0)]
        noise = float(np.median(reported)) if reported.size else 0.0
    if not np.isfinite(noise) or noise <= 0:
        raise RuntimeError("Science spectrum has no measurable emission-line contrast.")
    peaks, properties = find_peaks(
        residual,
        prominence=5.0 * noise,
        distance=5,
        width=1,
    )
    if peaks.size < minimum_matches:
        raise RuntimeError(
            f"Only {peaks.size} emission peaks were detected; at least {minimum_matches} are required."
        )
    prominence = np.asarray(properties["prominences"], dtype=float)
    keep = np.argsort(prominence)[-min(60, prominence.size) :]
    peaks = peaks[keep]
    prominence = prominence[keep]
    observed = wavelength[peaks]

    references = np.asarray(list(reference_lines_angstrom), dtype=float)
    references = references[
        np.isfinite(references)
        & (references >= wavelength[0] - maximum_offset_angstrom)
        & (references <= wavelength[-1] + maximum_offset_angstrom)
    ]
    if references.size < minimum_matches:
        raise RuntimeError("Too few reference emission lines overlap the science wavelength range.")

    candidates = []
    for observed_line in observed:
        offsets = observed_line - references
        candidates.extend(offsets[np.abs(offsets) <= maximum_offset_angstrom])
    best: tuple[
        tuple[int, float, float, float],
        np.ndarray,
        np.ndarray,
        float,
        float,
        str,
    ] | None = None
    initial_tolerance = max(8.0, 3.0 * match_tolerance_angstrom)
    for candidate in candidates:
        matched_observed, matched_reference = _match_shifted_lines(
            observed,
            prominence,
            references,
            float(candidate),
            initial_tolerance,
        )
        count = matched_observed.size
        if count < minimum_matches:
            continue
        fitted = _fit_emission_wavelength_correction(
            matched_observed,
            matched_reference,
            match_tolerance_angstrom,
            minimum_matches,
        )
        if fitted is None:
            continue
        matched_observed, matched_reference, scale, applied_offset, method = fitted
        pivot = float(np.median(matched_observed))
        corrected = pivot + scale * (matched_observed - pivot) + applied_offset
        correction_residual = corrected - matched_reference
        rms = float(np.sqrt(np.mean(correction_residual**2)))
        span = float(np.ptp(matched_reference)) if count > 1 else 0.0
        score = (
            matched_observed.size,
            span,
            -rms,
            -abs(scale - 1.0),
        )
        if best is None or score > best[0]:
            best = (
                score,
                matched_observed,
                matched_reference,
                scale,
                applied_offset,
                method,
            )
    if best is None:
        raise RuntimeError("No coherent wavelength correction matched the reference lines.")

    _, matched_observed, matched_reference, scale, applied_offset, method = best
    pivot = float(np.median(matched_observed))
    corrected = pivot + scale * (matched_observed - pivot) + applied_offset
    correction_residual = corrected - matched_reference
    rms = float(np.sqrt(np.mean(correction_residual**2)))
    if rms > match_tolerance_angstrom:
        raise RuntimeError(
            f"Emission-line wavelength-correction RMS {rms:.2f} Angstrom exceeds "
            f"the {match_tolerance_angstrom:.2f} Angstrom tolerance."
        )
    return ZeroPointCorrection(
        measured_offset_angstrom=-applied_offset,
        applied_offset_angstrom=applied_offset,
        rms_angstrom=rms,
        observed_wavelengths=matched_observed,
        reference_wavelengths=matched_reference,
        scale=scale,
        pivot_angstrom=pivot,
        method=method,
    )


def apply_response_and_normalize(
    science: ReducedSpectrumData,
    response: RelativeResponse,
    zero_point: ZeroPointCorrection | None = None,
    continuum_bin_angstrom: float = 100.0,
    continuum_percentile: float = 35.0,
    second_order_start_angstrom: float | None = 6800.0,
    second_order_status: str = "not_tested",
    second_order_empirical_onset_angstrom: float | None = None,
    second_order_diagnostic_marker_angstrom: float | None = 7292.0,
    second_order_assessment_path: str | Path | None = None,
) -> CalibratedSpectrum:
    if science.flux_adu.size != response.response.size:
        raise ValueError("Standard and science spectra have different detector widths/ROIs.")
    wavelength = science.wavelength_angstrom.copy()
    if zero_point is not None:
        wavelength = zero_point.apply(wavelength)
    science_rate = science.flux_adu / science.exposure_s
    science_uncertainty_rate = science.uncertainty_adu / science.exposure_s
    mask = (
        science.mask
        | response.mask
        | ~np.isfinite(science_rate)
        | ~np.isfinite(science_uncertainty_rate)
        | (response.response <= 0)
    )
    relative_flux = np.divide(
        science_rate,
        response.response,
        out=np.full_like(science_rate, np.nan),
        where=~mask,
    )
    relative_uncertainty = np.divide(
        science_uncertainty_rate,
        response.response,
        out=np.full_like(science_uncertainty_rate, np.nan),
        where=~mask,
    )
    continuum = fit_robust_continuum(
        wavelength,
        relative_flux,
        mask,
        bin_width_angstrom=continuum_bin_angstrom,
        percentile=continuum_percentile,
    )
    mask |= ~np.isfinite(continuum) | (continuum <= 0)
    normalized_flux = np.divide(
        relative_flux,
        continuum,
        out=np.full_like(relative_flux, np.nan),
        where=~mask,
    )
    normalized_uncertainty = np.divide(
        relative_uncertainty,
        continuum,
        out=np.full_like(relative_uncertainty, np.nan),
        where=~mask,
    )
    return CalibratedSpectrum(
        source=science,
        wavelength_angstrom=wavelength,
        relative_flux=relative_flux,
        relative_uncertainty=relative_uncertainty,
        continuum=continuum,
        normalized_flux=normalized_flux,
        normalized_uncertainty=normalized_uncertainty,
        response=response.response.copy(),
        mask=mask,
        response_standard=response.standard_name,
        response_template=response.template_path,
        zero_point=zero_point,
        second_order_start_angstrom=second_order_start_angstrom,
        second_order_status=second_order_status,
        second_order_empirical_onset_angstrom=second_order_empirical_onset_angstrom,
        second_order_diagnostic_marker_angstrom=second_order_diagnostic_marker_angstrom,
        second_order_assessment_path=(
            None
            if second_order_assessment_path is None
            else Path(second_order_assessment_path).expanduser().resolve()
        ),
    )


def fit_robust_continuum(
    wavelength: np.ndarray,
    flux: np.ndarray,
    mask: np.ndarray,
    bin_width_angstrom: float = 100.0,
    percentile: float = 35.0,
) -> np.ndarray:
    """Fit a positive smooth continuum while rejecting narrow emission peaks."""

    if bin_width_angstrom <= 0:
        raise ValueError("continuum bin width must be positive.")
    if not 5.0 <= percentile <= 95.0:
        raise ValueError("continuum percentile must be between 5 and 95.")
    valid = ~mask & np.isfinite(wavelength) & np.isfinite(flux) & (flux > 0)
    if np.count_nonzero(valid) < 100:
        raise RuntimeError("Too few positive science samples remain for continuum fitting.")
    edges = np.arange(
        float(wavelength[valid].min()),
        float(wavelength[valid].max()) + bin_width_angstrom,
        bin_width_angstrom,
    )
    centers: list[float] = []
    levels: list[float] = []
    for low, high in zip(edges[:-1], edges[1:]):
        inside = valid & (wavelength >= low) & (wavelength < high)
        if np.count_nonzero(inside) < 8:
            continue
        level = float(np.percentile(flux[inside], percentile))
        if np.isfinite(level) and level > 0:
            centers.append(float(np.median(wavelength[inside])))
            levels.append(level)
    if len(centers) < 5:
        raise RuntimeError("Too few populated wavelength bins remain for continuum fitting.")
    x = np.asarray(centers)
    y = np.log(np.asarray(levels))
    trend = median_filter(y, size=_odd_window(min(7, len(y))), mode="nearest")
    residual = y - trend
    scatter = 1.4826 * float(np.median(np.abs(residual - np.median(residual))))
    smoothing = max(len(x) * max(scatter, 0.02) ** 2, 1e-6)
    spline = UnivariateSpline(x, y, k=min(3, len(x) - 1), s=smoothing, ext=3)
    return np.exp(spline(wavelength))


def write_calibration_products(
    response: RelativeResponse,
    calibrated: CalibratedSpectrum,
    output_dir: str | Path,
    target_name: str,
) -> dict[str, Path]:
    destination = Path(output_dir).expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=True)
    stem = _safe_stem(target_name)
    paths = {
        "response_fits": destination / "relative_response.fits",
        "response_csv": destination / "relative_response.csv",
        "response_png": destination / "relative_response.png",
        "calibrated_fits": destination / f"{stem}_calibrated_1d.fits",
        "calibrated_csv": destination / f"{stem}_calibrated_1d.csv",
        "calibrated_png": destination / f"{stem}_calibrated_1d.png",
        "normalised_png": destination / f"{stem}_normalised_1d.png",
        "manifest": destination / f"{stem}_calibration.json",
    }
    _write_response_fits(response, paths["response_fits"])
    _write_response_csv(response, paths["response_csv"])
    _plot_response(
        response,
        paths["response_png"],
        calibrated.second_order_start_angstrom,
    )
    _write_calibrated_fits(calibrated, paths["calibrated_fits"])
    _write_calibrated_csv(calibrated, paths["calibrated_csv"])
    _plot_calibrated(calibrated, paths["calibrated_png"], normalized=False)
    _plot_calibrated(calibrated, paths["normalised_png"], normalized=True)
    payload = {
        "schemaVersion": 1,
        "calibrationType": "relative-response-and-continuum-normalization",
        "target": target_name,
        "scienceProduct": str(calibrated.source.path),
        "scienceSourceProvenance": _source_provenance(calibrated.source.path),
        "standardProduct": str(response.standard_path),
        "standardName": response.standard_name,
        "template": str(response.template_path),
        "absoluteFluxCalibrated": False,
        "responseFractionalScatter": response.fractional_scatter,
        "wavelengthZeroPoint": (
            None
            if calibrated.zero_point is None
            else {
                "measuredOffsetAngstrom": calibrated.zero_point.measured_offset_angstrom,
                "appliedOffsetAngstrom": calibrated.zero_point.applied_offset_angstrom,
                "scale": calibrated.zero_point.scale,
                "pivotAngstrom": calibrated.zero_point.pivot_angstrom,
                "method": calibrated.zero_point.method,
                "rmsAngstrom": calibrated.zero_point.rms_angstrom,
                "matchedLineCount": int(calibrated.zero_point.reference_wavelengths.size),
                "observedAngstrom": calibrated.zero_point.observed_wavelengths.tolist(),
                "referenceAngstrom": calibrated.zero_point.reference_wavelengths.tolist(),
            }
        ),
        "validFraction": float(np.mean(~calibrated.mask)),
        "artifacts": {key: str(value) for key, value in paths.items() if key != "manifest"},
        "secondOrderContamination": (
            None
            if calibrated.second_order_start_angstrom is None
            else {
                "warningStartsAtAngstrom": calibrated.second_order_start_angstrom,
                "warningThresholdKind": "conservative-estimate-not-measured-cutoff",
                "empiricalStatus": calibrated.second_order_status,
                "empiricalOnsetAngstrom": (
                    calibrated.second_order_empirical_onset_angstrom
                ),
                "diagnosticBalmerMarkerAngstrom": (
                    calibrated.second_order_diagnostic_marker_angstrom
                ),
                "assessment": (
                    None
                    if calibrated.second_order_assessment_path is None
                    else str(calibrated.second_order_assessment_path)
                ),
                "dataRetained": True,
                "includedInBadPixelMask": False,
                "reason": "No long-pass order-sorting filter was installed.",
            }
        ),
        "limitations": [
            "Relative response only: slit loss, atmospheric extinction, and standard-star absolute flux were not modelled.",
            "Response-curve uncertainty is not yet propagated into the science uncertainty column.",
            "Continuum normalization is algorithmic and should be reviewed near broad/strong lines.",
            (
                "No second-order contamination threshold was supplied."
                if calibrated.second_order_start_angstrom is None
                else f"{calibrated.second_order_start_angstrom:.1f} Angstrom is a "
                "conservative second-order warning threshold, not a measured cutoff; "
                "flagged values are retained for qualitative viewing."
            ),
        ],
    }
    paths["manifest"].write_text(
        json.dumps(payload, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return paths


def _source_provenance(path: Path) -> dict[str, object]:
    source = path.expanduser().resolve()
    provenance: dict[str, object] = {
        "path": str(source),
        "exists": source.is_file(),
    }
    if not source.is_file():
        return provenance

    stat = source.stat()
    digest = hashlib.sha256()
    with source.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    provenance.update(
        {
            "sizeBytes": stat.st_size,
            "modifiedUtc": datetime.fromtimestamp(
                stat.st_mtime,
                tz=timezone.utc,
            ).isoformat(),
            "sha256": digest.hexdigest(),
        }
    )

    stem = source.stem
    if stem.endswith("_spectrum"):
        stem = stem[: -len("_spectrum")]
    run_manifest = source.with_name(f"{stem}_run.json")
    provenance["runManifest"] = str(run_manifest)
    if run_manifest.is_file():
        try:
            run = json.loads(run_manifest.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            provenance["runManifestReadable"] = False
        else:
            provenance.update(
                {
                    "runManifestReadable": True,
                    "pipelineVersion": run.get("pipelineVersion"),
                    "runCreatedUtc": run.get("createdUtc"),
                    "sdkWrapRepairs": run.get("sdkWrapRepairs"),
                    "sdkWrapWarnings": [
                        warning
                        for warning in run.get("warnings", [])
                        if "SDK wrap repair" in str(warning)
                        or "SDK x-wrap" in str(warning)
                    ],
                }
            )
    return provenance


def _fit_emission_wavelength_correction(
    observed: np.ndarray,
    reference: np.ndarray,
    tolerance: float,
    minimum_matches: int,
) -> tuple[np.ndarray, np.ndarray, float, float, str] | None:
    offsets = observed - reference
    center = float(np.median(offsets))
    offset_keep = np.abs(offsets - center) <= max(tolerance, 3.0)
    offset_rms = float("inf")
    if np.count_nonzero(offset_keep) >= minimum_matches:
        offset_center = float(np.mean(offsets[offset_keep]))
        offset_rms = float(
            np.sqrt(np.mean((offsets[offset_keep] - offset_center) ** 2))
        )
    else:
        offset_center = center

    affine_result = None
    if observed.size >= max(5, minimum_matches) and np.ptp(reference) >= 1000.0:
        keep = np.ones(observed.size, dtype=bool)
        for _ in range(5):
            scale, intercept = np.polyfit(observed[keep], reference[keep], 1)
            residual = scale * observed + intercept - reference
            scatter = 1.4826 * float(
                np.median(np.abs(residual[keep] - np.median(residual[keep])))
            )
            limit = max(tolerance, 4.0 * scatter)
            updated = np.abs(residual) <= limit
            if (
                np.count_nonzero(updated) < max(5, minimum_matches)
                or np.array_equal(updated, keep)
            ):
                keep = updated if np.count_nonzero(updated) >= minimum_matches else keep
                break
            keep = updated
        scale, intercept = np.polyfit(observed[keep], reference[keep], 1)
        residual = scale * observed + intercept - reference
        keep &= np.abs(residual) <= tolerance
        if np.count_nonzero(keep) >= max(5, minimum_matches) and 0.98 <= scale <= 1.02:
            scale, intercept = np.polyfit(observed[keep], reference[keep], 1)
            residual = scale * observed[keep] + intercept - reference[keep]
            rms = float(np.sqrt(np.mean(residual**2)))
            pivot = float(np.median(observed[keep]))
            applied_at_pivot = float(intercept + (scale - 1.0) * pivot)
            affine_result = (
                observed[keep],
                reference[keep],
                float(scale),
                applied_at_pivot,
                "bounded-affine",
                rms,
            )

    if affine_result is not None and (
        not np.isfinite(offset_rms) or affine_result[-1] <= 0.8 * offset_rms
    ):
        return affine_result[:-1]
    if np.count_nonzero(offset_keep) < minimum_matches:
        return None
    return (
        observed[offset_keep],
        reference[offset_keep],
        1.0,
        -offset_center,
        "common-offset",
    )


def _match_shifted_lines(
    observed: np.ndarray,
    prominence: np.ndarray,
    references: np.ndarray,
    offset: float,
    tolerance: float,
) -> tuple[np.ndarray, np.ndarray]:
    pairs: list[tuple[float, float, float]] = []
    for reference in references:
        distance = np.abs(observed - (reference + offset))
        index = int(np.argmin(distance))
        if distance[index] <= tolerance:
            pairs.append((float(prominence[index]), float(observed[index]), float(reference)))
    pairs.sort(reverse=True)
    used: set[float] = set()
    selected: list[tuple[float, float]] = []
    for _, observed_line, reference in pairs:
        if observed_line in used:
            continue
        used.add(observed_line)
        selected.append((observed_line, reference))
    selected.sort(key=lambda pair: pair[1])
    if not selected:
        return np.asarray([], dtype=float), np.asarray([], dtype=float)
    return (
        np.asarray([pair[0] for pair in selected]),
        np.asarray([pair[1] for pair in selected]),
    )


def _write_response_fits(response: RelativeResponse, path: Path) -> None:
    header = fits.Header()
    header["CALTYPE"] = ("RELATIVE", "Relative response; not absolute flux")
    header["STDSTAR"] = response.standard_name
    header["STDFILE"] = response.standard_path.name
    header["TMPLFILE"] = response.template_path.name
    header["RESPSCAT"] = (response.fractional_scatter, "Robust fractional response scatter")
    table = fits.BinTableHDU.from_columns(
        [
            fits.Column(name="WAVELENGTH", format="D", unit="Angstrom", array=response.wavelength_angstrom),
            fits.Column(name="OBS_RATE", format="D", unit="adu/s", array=response.observed_rate),
            fits.Column(name="TEMPLATE", format="D", array=response.template_flux),
            fits.Column(name="RAW_RESPONSE", format="D", array=response.raw_response),
            fits.Column(name="RESPONSE", format="D", array=response.response),
            fits.Column(name="MASK", format="L", array=response.mask),
        ],
        name="RESPONSE",
    )
    fits.HDUList([fits.PrimaryHDU(header=header), table]).writeto(
        path,
        overwrite=True,
        checksum=True,
    )


def _write_response_csv(response: RelativeResponse, path: Path) -> None:
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            ["wavelength_angstrom", "observed_adu_per_s", "template_flux", "raw_response", "response", "mask"]
        )
        writer.writerows(
            zip(
                response.wavelength_angstrom,
                response.observed_rate,
                response.template_flux,
                response.raw_response,
                response.response,
                response.mask.astype(int),
            )
        )


def _write_calibrated_fits(calibrated: CalibratedSpectrum, path: Path) -> None:
    header = calibrated.source.header.copy()
    header["FLUXCAL"] = ("RELATIVE", "Relative response calibration applied")
    header["ABSFLUX"] = (False, "Absolute physical flux calibration")
    header["RESPSTAR"] = calibrated.response_standard
    header["RESPTMPL"] = calibrated.response_template.name
    header["CONTNORM"] = (True, "Continuum-normalized spectrum included")
    if calibrated.zero_point is not None:
        header["WAVEOFF"] = (
            calibrated.zero_point.applied_offset_angstrom,
            "Applied wavelength zero-point offset, Angstrom",
        )
        header["WAVEOFRM"] = (
            calibrated.zero_point.rms_angstrom,
            "Emission-line zero-point RMS, Angstrom",
        )
        header["WAVESCL"] = (
            calibrated.zero_point.scale,
            "Emission-line wavelength affine scale",
        )
        header["WAVEPIV"] = (
            calibrated.zero_point.pivot_angstrom,
            "Wavelength correction pivot, Angstrom",
        )
        header["WAVECORM"] = (
            calibrated.zero_point.method[:16],
            "Emission-line correction method",
        )
    if calibrated.second_order_start_angstrom is not None:
        header["ORD2CONT"] = (True, "Second-order contamination risk; data retained")
        header["ORD2STRT"] = (
            calibrated.second_order_start_angstrom,
            "Conservative warning threshold, Angstrom",
        )
        header["ORD2STAT"] = (
            calibrated.second_order_status.upper()[:16],
            "Empirical second-order test status",
        )
        header["ORD2MEAS"] = (
            calibrated.second_order_empirical_onset_angstrom is not None,
            "Empirical onset measured",
        )
        if calibrated.second_order_empirical_onset_angstrom is not None:
            header["ORD2ONST"] = (
                calibrated.second_order_empirical_onset_angstrom,
                "Empirical second-order onset, Angstrom",
            )
        if calibrated.second_order_diagnostic_marker_angstrom is not None:
            header["ORD2BALM"] = (
                calibrated.second_order_diagnostic_marker_angstrom,
                "Twice Balmer discontinuity, Angstrom",
            )
    order2_risk = (
        np.zeros(calibrated.wavelength_angstrom.size, dtype=bool)
        if calibrated.second_order_start_angstrom is None
        else calibrated.wavelength_angstrom >= calibrated.second_order_start_angstrom
    )
    columns = [
        fits.Column(name="PIXEL", format="D", unit="pix", array=calibrated.source.pixel),
        fits.Column(name="WAVELENGTH", format="D", unit="Angstrom", array=calibrated.wavelength_angstrom),
        fits.Column(name="RAW_FLUX", format="D", unit="adu", array=calibrated.source.flux_adu),
        fits.Column(name="RAW_UNCERTAINTY", format="D", unit="adu", array=calibrated.source.uncertainty_adu),
        fits.Column(name="RESPONSE", format="D", array=calibrated.response),
        fits.Column(name="RELATIVE_FLUX", format="D", unit="adu/s", array=calibrated.relative_flux),
        fits.Column(name="RELATIVE_UNCERTAINTY", format="D", unit="adu/s", array=calibrated.relative_uncertainty),
        fits.Column(name="CONTINUUM", format="D", unit="adu/s", array=calibrated.continuum),
        fits.Column(name="NORMALIZED_FLUX", format="D", array=calibrated.normalized_flux),
        fits.Column(name="NORMALIZED_UNCERTAINTY", format="D", array=calibrated.normalized_uncertainty),
        fits.Column(name="ORDER2_RISK", format="L", array=order2_risk),
        fits.Column(name="MASK", format="L", array=calibrated.mask),
    ]
    table = fits.BinTableHDU.from_columns(columns, name="SPECTRUM")
    table.header["AIRORVAC"] = "AIR"
    hdus = [fits.PrimaryHDU(header=header), table]
    if calibrated.zero_point is not None:
        zero = calibrated.zero_point
        zero_table = fits.BinTableHDU.from_columns(
            [
                fits.Column(name="OBSERVED", format="D", unit="Angstrom", array=zero.observed_wavelengths),
                fits.Column(name="REFERENCE", format="D", unit="Angstrom", array=zero.reference_wavelengths),
                fits.Column(
                    name="RESIDUAL",
                    format="D",
                    unit="Angstrom",
                    array=zero.apply(zero.observed_wavelengths)
                    - zero.reference_wavelengths,
                ),
            ],
            name="ZEROPOINT",
        )
        hdus.append(zero_table)
    fits.HDUList(hdus).writeto(path, overwrite=True, checksum=True)


def _write_calibrated_csv(calibrated: CalibratedSpectrum, path: Path) -> None:
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "wavelength_angstrom",
                "relative_flux_adu_per_s",
                "relative_uncertainty_adu_per_s",
                "continuum_adu_per_s",
                "normalized_flux",
                "normalized_uncertainty",
                "response",
                "second_order_risk",
                "mask",
            ]
        )
        writer.writerows(
            zip(
                calibrated.wavelength_angstrom,
                calibrated.relative_flux,
                calibrated.relative_uncertainty,
                calibrated.continuum,
                calibrated.normalized_flux,
                calibrated.normalized_uncertainty,
                calibrated.response,
                (
                    np.zeros(calibrated.wavelength_angstrom.size, dtype=int)
                    if calibrated.second_order_start_angstrom is None
                    else (
                        calibrated.wavelength_angstrom
                        >= calibrated.second_order_start_angstrom
                    ).astype(int)
                ),
                calibrated.mask.astype(int),
            )
        )


def _plot_response(
    response: RelativeResponse,
    path: Path,
    second_order_start_angstrom: float | None,
) -> None:
    valid = ~response.mask
    figure, axes = plt.subplots(2, 1, figsize=(13, 8), sharex=True, constrained_layout=True)
    template_scale = np.nanmedian(response.observed_rate[valid]) / np.nanmedian(
        response.template_flux[valid]
    )
    axes[0].plot(
        response.wavelength_angstrom,
        np.where(valid, response.observed_rate, np.nan),
        linewidth=0.7,
        label="Observed standard (ADU/s)",
    )
    axes[0].plot(
        response.wavelength_angstrom,
        response.template_flux * template_scale,
        linewidth=1.0,
        alpha=0.8,
        label="ISIS template (scaled)",
    )
    axes[0].set(ylabel="Relative signal", title=f"Response standard: {response.standard_name}")
    axes[0].legend()
    axes[1].plot(
        response.wavelength_angstrom,
        np.where(valid, response.raw_response, np.nan),
        linewidth=0.45,
        alpha=0.45,
        label="Raw ratio",
    )
    axes[1].plot(response.wavelength_angstrom, response.response, linewidth=1.4, label="Smoothed response")
    axes[1].set(xlabel="Wavelength (Angstrom)", ylabel="Normalized response")
    axes[1].legend()
    for axis in axes:
        _shade_second_order(axis, second_order_start_angstrom)
        axis.grid(alpha=0.2)
        axis.legend()
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _plot_calibrated(calibrated: CalibratedSpectrum, path: Path, normalized: bool) -> None:
    valid = ~calibrated.mask
    figure, axis = plt.subplots(figsize=(14, 5), constrained_layout=True)
    if normalized:
        axis.plot(
            calibrated.wavelength_angstrom,
            np.where(valid, calibrated.normalized_flux, np.nan),
            linewidth=0.75,
        )
        axis.axhline(1.0, color="0.5", linewidth=0.8)
        axis.set(ylabel="Continuum-normalized flux", title="Normalized 1D spectrum")
    else:
        axis.plot(
            calibrated.wavelength_angstrom,
            np.where(valid, calibrated.relative_flux, np.nan),
            linewidth=0.75,
            label="Relative-response corrected",
        )
        axis.plot(
            calibrated.wavelength_angstrom,
            np.where(valid, calibrated.continuum, np.nan),
            linewidth=1.2,
            label="Robust continuum",
        )
        axis.set(ylabel="Relative flux (ADU/s)", title="Relative-response calibrated 1D spectrum")
        axis.legend()
    axis.set_xlabel("Wavelength (Angstrom, air)")
    _shade_second_order(axis, calibrated.second_order_start_angstrom)
    axis.grid(alpha=0.2)
    axis.legend(loc="upper right")
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _interpolate_valid(values: np.ndarray, valid: np.ndarray) -> np.ndarray:
    indices = np.arange(values.size)
    good = np.flatnonzero(valid & np.isfinite(values))
    if good.size < 2:
        raise RuntimeError("At least two finite samples are required for interpolation.")
    return np.interp(indices, good, values[good])


def _shade_second_order(axis, start_angstrom: float | None) -> None:
    if start_angstrom is None:
        return
    right = axis.get_xlim()[1]
    axis.axvspan(
        start_angstrom,
        max(start_angstrom, right),
        color="#ff9800",
        alpha=0.12,
        label="Conservative second-order warning (data retained)",
    )


def _odd_window(value: int) -> int:
    value = max(3, int(value))
    return value if value % 2 else value + 1


def _header_float(header: fits.Header, *keys: str) -> float | None:
    for key in keys:
        value = header.get(key)
        try:
            if value is not None:
                return float(value)
        except (TypeError, ValueError):
            continue
    return None


def _safe_stem(value: str) -> str:
    import re

    cleaned = re.sub(r"[^A-Za-z0-9_.-]+", "_", value.strip()).strip("._")
    return cleaned or "uvex"
