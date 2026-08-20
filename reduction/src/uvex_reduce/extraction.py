from __future__ import annotations

from dataclasses import dataclass
import logging
from typing import Any
import warnings as python_warnings

from astropy.io import fits
import numpy as np
from scipy.ndimage import gaussian_filter, gaussian_filter1d, median_filter

from .config import DetectorConfig, ExtractionConfig
from .models import TraceResult


LOGGER = logging.getLogger(__name__)


class TraceDetectionError(RuntimeError):
    pass


@dataclass(slots=True)
class ExtractionProduct:
    flux: np.ndarray
    uncertainty: np.ndarray
    mask: np.ndarray
    trace: TraceResult
    backend: str
    warnings: list[str]
    aspired_twodspec: Any | None = None
    arc_spectrum: np.ndarray | None = None


def extract_spectrum(
    image: np.ndarray,
    variance: np.ndarray,
    mask: np.ndarray,
    header: fits.Header,
    detector: DetectorConfig,
    options: ExtractionConfig,
    arc_image: np.ndarray | None = None,
    arc_header: fits.Header | None = None,
) -> ExtractionProduct:
    warnings: list[str] = []
    if options.backend.lower() == "aspired":
        try:
            return _extract_with_aspired(
                image,
                variance,
                mask,
                header,
                detector,
                options,
                arc_image,
                arc_header,
            )
        except Exception as error:
            if not options.allow_native_fallback:
                raise
            warnings.append(f"ASPIRED extraction failed ({type(error).__name__}: {error}); native boxcar fallback used.")
            LOGGER.debug("ASPIRED extraction failed; using native fallback", exc_info=True)

    trace = trace_spectrum(image, mask, options)
    flux, uncertainty, output_mask = _boxcar_extract(image, variance, mask, trace.centers, options)
    return ExtractionProduct(flux, uncertainty, output_mask, trace, "native-boxcar", warnings)


def trace_spectrum(image: np.ndarray, mask: np.ndarray, options: ExtractionConfig) -> TraceResult:
    if image.ndim != 2:
        raise ValueError("Trace input must be a two-dimensional image.")
    height, width = image.shape
    if options.manual_trace_y is not None:
        if not 0 <= options.manual_trace_y < height:
            raise ValueError("manual_trace_y lies outside the detector.")
        centers = np.full(width, float(options.manual_trace_y))
        sigma = np.full(width, max(1.0, options.aperture_half_width / 2.355))
        return TraceResult(centers, sigma, 0, float("nan"), "manual-flat-trace", True)

    work = np.where(mask | ~np.isfinite(image), np.nan, image)
    column_background = np.nanmedian(work, axis=0, keepdims=True)
    signal = work - column_background
    x_low = max(0, int(width * 0.05))
    x_high = min(width, int(width * 0.95))
    global_profile = np.nanmedian(signal[:, x_low:x_high], axis=1)
    global_profile = gaussian_filter1d(np.nan_to_num(global_profile, nan=0.0), 2.5)
    edge = min(20, max(1, height // 20))
    search_profile = global_profile.copy()
    search_profile[:edge] = -np.inf
    search_profile[-edge:] = -np.inf
    seed = int(np.nanargmax(search_profile))
    global_snr = _profile_snr(global_profile, seed, options.trace_half_width)

    edges = np.linspace(0, width, options.trace_bins + 1, dtype=int)
    sample_x: list[float] = []
    sample_y: list[float] = []
    sample_sigma: list[float] = []
    sample_snr: list[float] = []
    current = float(seed)
    for left, right in zip(edges[:-1], edges[1:]):
        if right - left < 2:
            continue
        profile = np.nanmedian(signal[:, left:right], axis=1)
        profile = gaussian_filter1d(np.nan_to_num(profile, nan=0.0), 2.0)
        low = max(edge, int(round(current)) - options.trace_half_width)
        high = min(height - edge, int(round(current)) + options.trace_half_width + 1)
        if high - low < 5:
            continue
        local_peak = low + int(np.argmax(profile[low:high]))
        snr = _profile_snr(profile, local_peak, options.trace_half_width)
        if not np.isfinite(snr) or snr < options.minimum_trace_snr:
            continue
        center, sigma = _weighted_centroid(profile, local_peak, options.trace_half_width)
        if not np.isfinite(center):
            continue
        sample_x.append((left + right - 1) / 2.0)
        sample_y.append(center)
        sample_sigma.append(sigma)
        sample_snr.append(snr)
        current = center

    minimum_bins = max(options.minimum_valid_trace_bins, options.trace_degree + 1)
    if len(sample_x) < minimum_bins:
        if global_snr >= options.minimum_trace_snr and options.allow_low_confidence_trace:
            centers = np.full(width, float(seed))
            sigma_value = _weighted_centroid(global_profile, seed, options.trace_half_width)[1]
            return TraceResult(
                centers,
                np.full(width, sigma_value),
                len(sample_x),
                global_snr,
                "low-confidence-flat-trace",
                True,
            )
        raise TraceDetectionError(
            f"Only {len(sample_x)} reliable trace bins were found (need {minimum_bins}); "
            f"global S/N={global_snr:.2f}. Set manual_trace_y after inspecting the diagnostic."
        )

    x = np.asarray(sample_x)
    y = np.asarray(sample_y)
    keep = np.ones(x.size, dtype=bool)
    degree = min(options.trace_degree, x.size - 1)
    for _ in range(4):
        coefficients = np.polyfit(x[keep], y[keep], degree)
        residual = y - np.polyval(coefficients, x)
        median = np.median(residual[keep])
        scatter = 1.4826 * np.median(np.abs(residual[keep] - median))
        if scatter <= 0:
            break
        next_keep = np.abs(residual - median) <= max(2.0, 4.0 * scatter)
        if next_keep.sum() < minimum_bins or np.array_equal(next_keep, keep):
            break
        keep = next_keep
    coefficients = np.polyfit(x[keep], y[keep], degree)
    pixels = np.arange(width, dtype=float)
    centers = np.polyval(coefficients, pixels)
    if np.any((centers < 1) | (centers >= height - 1)):
        raise TraceDetectionError("The fitted trace leaves the detector bounds.")
    sigma_value = float(np.median(np.asarray(sample_sigma)[keep]))
    sigma = np.full(width, max(1.0, sigma_value))
    return TraceResult(
        centers=centers,
        sigma_pixels=sigma,
        valid_bins=int(keep.sum()),
        snr=float(np.median(np.asarray(sample_snr)[keep])),
        method="robust-binned-polynomial",
    )


def _extract_with_aspired(
    image: np.ndarray,
    variance: np.ndarray,
    mask: np.ndarray,
    header: fits.Header,
    detector: DetectorConfig,
    options: ExtractionConfig,
    arc_image: np.ndarray | None,
    arc_header: fits.Header | None,
) -> ExtractionProduct:
    # ASPIRED 0.5.1 emits many repetitive numerical warnings in masked edge
    # columns. Returned arrays are explicitly validated below, so keep the CLI
    # readable and report failures as one actionable fallback message.
    with python_warnings.catch_warnings():
        python_warnings.simplefilter("ignore", RuntimeWarning)
        return _extract_with_aspired_impl(
            image,
            variance,
            mask,
            header,
            detector,
            options,
            arc_image,
            arc_header,
        )


def _extract_with_aspired_impl(
    image: np.ndarray,
    variance: np.ndarray,
    mask: np.ndarray,
    header: fits.Header,
    detector: DetectorConfig,
    options: ExtractionConfig,
    arc_image: np.ndarray | None,
    arc_header: fits.Header | None,
) -> ExtractionProduct:
    from aspired import spectral_reduction

    warnings: list[str] = []
    aspired_image = _fill_masked_for_aspired(image, mask)
    twod = spectral_reduction.TwoDSpec(
        aspired_image,
        header=header,
        saxis=1,
        flip=False,
        cosmicray=False,
        gain=detector.gain_e_per_adu,
        readnoise=detector.read_noise_e,
        variance=variance,
        verbose=False,
    )
    twod.add_bad_mask(mask)
    trace: TraceResult | None = None
    if options.manual_trace_y is None:
        try:
            twod.ap_trace(
                nspec=1,
                smooth=False,
                nwindow=options.trace_bins,
                trace_width=options.trace_half_width,
                shift_tol=options.trace_half_width,
                fit_deg=options.trace_degree,
                ap_faint=options.aspired_faint_percent,
                resample_factor=1,
                display=False,
            )
            candidate = np.asarray(twod.spectrum_list[0].trace, dtype=float)
            candidate_sigma = np.asarray(twod.spectrum_list[0].trace_sigma, dtype=float)
            if candidate_sigma.ndim == 0:
                candidate_sigma = np.full(candidate.size, float(candidate_sigma))
            elif candidate_sigma.size != candidate.size:
                candidate_sigma = np.full(candidate.size, float(np.nanmedian(candidate_sigma)))
            quality = _trace_contrast(image, mask, candidate)
            if candidate.size != image.shape[1] or not np.isfinite(candidate).all() or quality < options.minimum_trace_snr:
                raise TraceDetectionError(f"ASPIRED trace validation S/N={quality:.2f}")
            trace = TraceResult(candidate, candidate_sigma, options.trace_bins, quality, "aspired-ap-trace")
        except Exception as error:
            warnings.append(f"ASPIRED automatic trace was rejected ({error}); robust trace fallback supplied to ASPIRED.")

    if trace is None:
        trace = trace_spectrum(image, mask, options)
        trace.fallback_used = True
        if twod.spectrum_list:
            for spec_id in list(twod.spectrum_list):
                twod.remove_trace(spec_id)
        twod.add_trace(trace.centers, trace.sigma_pixels, spec_id=0)

    reference_flux, reference_uncertainty, reference_mask = _boxcar_extract(
        image,
        variance,
        mask,
        trace.centers,
        options,
    )
    if options.optimal:
        try:
            _run_aspired_ap_extract(twod, options, optimal=True, variance=variance)
            flux, uncertainty, output_mask, uncertainty_warning = _read_aspired_output(
                twod,
                detector,
                options,
            )
            quality_issue = _extraction_quality_issue(
                flux,
                uncertainty,
                output_mask,
                reference_flux,
                reference_mask,
                options,
            )
            if quality_issue:
                raise RuntimeError(quality_issue)
            if uncertainty_warning:
                warnings.append(uncertainty_warning)
            backend = "aspired-horne86"
        except Exception as error:
            warnings.append(
                f"ASPIRED optimal extraction was rejected ({error}); "
                "ASPIRED tophat extraction used."
            )
            _run_aspired_ap_extract(twod, options, optimal=False, variance=variance)
            flux, uncertainty, output_mask, uncertainty_warning = _read_aspired_output(
                twod,
                detector,
                options,
            )
            if uncertainty_warning:
                warnings.append(uncertainty_warning)
            backend = "aspired-tophat-fallback"
    else:
        _run_aspired_ap_extract(twod, options, optimal=False, variance=variance)
        flux, uncertainty, output_mask, uncertainty_warning = _read_aspired_output(
            twod,
            detector,
            options,
        )
        if uncertainty_warning:
            warnings.append(uncertainty_warning)
        backend = "aspired-tophat"

    quality_issue = _extraction_quality_issue(
        flux,
        uncertainty,
        output_mask,
        reference_flux,
        reference_mask,
        options,
        compare_peaks=False,
    )
    if quality_issue:
        raise RuntimeError(f"ASPIRED {backend} output failed validation: {quality_issue}")

    if backend != "aspired-horne86":
        uncertainty = reference_uncertainty
    output_mask |= reference_mask | ~np.isfinite(uncertainty) | (uncertainty <= 0)

    arc_spectrum = None
    if arc_image is not None:
        if arc_image.shape != image.shape:
            raise ValueError("Arc and science images must have identical dimensions.")
        twod.add_arc(arc_image, header=arc_header)
        twod.extract_arc_spec(spec_width=max(3, options.aperture_half_width), display=False, spec_id=0)
        arc_spectrum = np.asarray(twod.spectrum_list[0].arc_spec, dtype=float)
    return ExtractionProduct(flux, uncertainty, output_mask, trace, backend, warnings, twod, arc_spectrum)


def _run_aspired_ap_extract(
    twod: Any,
    options: ExtractionConfig,
    *,
    optimal: bool,
    variance: np.ndarray,
) -> None:
    forced_variances = None
    if optimal:
        trace = np.asarray(twod.spectrum_list[0].trace, dtype=float)
        forced_variances = _aperture_variance_slices(
            variance,
            trace,
            options.aperture_half_width,
        )
    twod.ap_extract(
        apwidth=options.aperture_half_width,
        skysep=options.sky_separation,
        skywidth=options.sky_width,
        sky_sigma=4.0,
        optimal=optimal,
        algorithm="horne86",
        model="gauss",
        cosmicray_sigma=8.0,
        max_iter=40,
        forced=optimal,
        variances=forced_variances,
        display=False,
        spec_id=0,
    )


def _aperture_variance_slices(
    variance: np.ndarray,
    trace: np.ndarray,
    aperture_half_width: int,
) -> np.ndarray:
    height, width = variance.shape
    size = 2 * aperture_half_width + 1
    result = np.full((width, size), np.nan, dtype=float)
    for x, center in enumerate(trace):
        y = int(round(center))
        low = max(0, y - aperture_half_width)
        high = min(height, y + aperture_half_width + 1)
        destination_low = max(0, aperture_half_width - y)
        result[x, destination_low : destination_low + (high - low)] = variance[low:high, x]
    finite = result[np.isfinite(result) & (result > 0)]
    fallback = float(np.nanmedian(finite)) if finite.size else 1.0
    result[~np.isfinite(result) | (result <= 0)] = fallback
    return result


def _read_aspired_output(
    twod: Any,
    detector: DetectorConfig,
    options: ExtractionConfig,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, str | None]:
    spectrum = twod.spectrum_list[0]
    flux = np.asarray(spectrum.count, dtype=float)
    uncertainty = np.asarray(spectrum.count_err, dtype=float)
    warning = None
    if uncertainty.ndim == 0 or uncertainty.size != flux.size:
        warning = (
            "ASPIRED did not return a complete uncertainty array; "
            "a conservative Poisson estimate was used."
        )
        gain = max(detector.gain_e_per_adu, 1e-6)
        uncertainty = np.sqrt(
            np.maximum(np.abs(flux), 0.0) / gain
            + (2 * options.aperture_half_width + 1)
            * (detector.read_noise_e / gain) ** 2
        )
    output_mask = ~np.isfinite(flux) | ~np.isfinite(uncertainty) | (uncertainty <= 0)
    return flux, uncertainty, output_mask, warning


def _extraction_quality_issue(
    flux: np.ndarray,
    uncertainty: np.ndarray,
    output_mask: np.ndarray,
    reference_flux: np.ndarray,
    reference_mask: np.ndarray,
    options: ExtractionConfig,
    *,
    compare_peaks: bool = True,
) -> str | None:
    if flux.ndim != 1 or flux.size != reference_flux.size:
        return "returned flux has the wrong shape"
    valid = ~output_mask & np.isfinite(flux) & np.isfinite(uncertainty) & (uncertainty > 0)
    if valid.mean() < options.minimum_valid_fraction:
        return f"only {valid.mean():.1%} of spectral columns are valid"
    if not compare_peaks:
        return None

    common = valid & ~reference_mask & np.isfinite(reference_flux)
    if common.mean() < options.minimum_valid_fraction:
        return "too few columns overlap the independent boxcar check"
    reference_level = float(np.nanmedian(np.abs(reference_flux[common])))
    flux_level = float(np.nanmedian(np.abs(flux[common])))
    if reference_level <= 0 or flux_level <= 0:
        return "the extracted signal scale is not positive"
    scale = flux_level / reference_level
    optimal_peak = float(np.nanmax(np.abs(flux[common])))
    reference_peak = float(np.nanmax(np.abs(reference_flux[common] * scale)))
    ratio = optimal_peak / max(reference_peak, flux_level, 1e-12)
    if ratio > options.maximum_optimal_to_boxcar_peak_ratio:
        return (
            f"peak is {ratio:.1f}x the independent boxcar peak "
            f"(limit {options.maximum_optimal_to_boxcar_peak_ratio:.1f}x)"
        )
    optimal_noise = _relative_high_frequency_noise(flux[common])
    reference_noise = _relative_high_frequency_noise(reference_flux[common] * scale)
    noise_ratio = optimal_noise / max(reference_noise, 1e-8)
    if noise_ratio > options.maximum_optimal_to_boxcar_noise_ratio:
        return (
            f"high-frequency noise is {noise_ratio:.1f}x the independent boxcar result "
            f"(limit {options.maximum_optimal_to_boxcar_noise_ratio:.1f}x)"
        )
    return None


def _relative_high_frequency_noise(values: np.ndarray) -> float:
    values = np.asarray(values, dtype=float)
    smooth = median_filter(values, size=9, mode="nearest")
    residual = values - smooth
    median = float(np.median(residual))
    mad = 1.4826 * float(np.median(np.abs(residual - median)))
    level = float(np.median(np.abs(values)))
    return mad / max(level, 1e-12)


def _boxcar_extract(
    image: np.ndarray,
    variance: np.ndarray,
    mask: np.ndarray,
    centers: np.ndarray,
    options: ExtractionConfig,
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    height, width = image.shape
    flux = np.full(width, np.nan)
    uncertainty = np.full(width, np.nan)
    output_mask = np.ones(width, dtype=bool)
    aperture = options.aperture_half_width
    for x, center in enumerate(centers):
        y = int(round(center))
        source_low = max(0, y - aperture)
        source_high = min(height, y + aperture + 1)
        sky_low = max(0, source_low - options.sky_separation - options.sky_width)
        sky_low_high = max(0, source_low - options.sky_separation)
        sky_high_low = min(height, source_high + options.sky_separation)
        sky_high = min(height, sky_high_low + options.sky_width)
        source_values = image[source_low:source_high, x]
        source_mask = mask[source_low:source_high, x] | ~np.isfinite(source_values)
        sky_values = np.concatenate((image[sky_low:sky_low_high, x], image[sky_high_low:sky_high, x]))
        sky_masks = np.concatenate((mask[sky_low:sky_low_high, x], mask[sky_high_low:sky_high, x]))
        sky_valid = np.isfinite(sky_values) & ~sky_masks
        valid_source = ~source_mask
        minimum_sky = max(4, sky_values.size // 4)
        if (
            valid_source.sum() < max(3, source_values.size // 3)
            or sky_valid.sum() < minimum_sky
        ):
            continue
        background = float(np.median(sky_values[sky_valid]))
        flux[x] = float(np.sum(source_values[valid_source] - background))
        variance_sum = float(np.sum(variance[source_low:source_high, x][valid_source]))
        variance_sum += (
            float(np.var(sky_values[sky_valid]))
            * valid_source.sum() ** 2
            / sky_valid.sum()
        )
        uncertainty[x] = np.sqrt(max(variance_sum, 0.0))
        output_mask[x] = not np.isfinite(uncertainty[x]) or uncertainty[x] <= 0
    return flux, uncertainty, output_mask


def _profile_snr(profile: np.ndarray, peak: int, half_width: int) -> float:
    indices = np.arange(profile.size)
    background = profile[np.abs(indices - peak) > half_width]
    if background.size < 10:
        background = profile
    median = float(np.median(background))
    mad = 1.4826 * float(np.median(np.abs(background - median)))
    if mad <= 1e-12:
        mad = float(np.std(background))
    signal = float(profile[peak]) - median
    if mad <= 1e-12:
        return float("inf") if signal > 0 else 0.0
    return signal / mad


def _weighted_centroid(profile: np.ndarray, peak: int, half_width: int) -> tuple[float, float]:
    low = max(0, peak - half_width)
    high = min(profile.size, peak + half_width + 1)
    pixels = np.arange(low, high, dtype=float)
    values = np.asarray(profile[low:high], dtype=float)
    baseline = np.percentile(values, 20.0)
    weights = np.clip(values - baseline, 0.0, None)
    if not np.isfinite(weights).all() or weights.sum() <= 0:
        return float("nan"), float("nan")
    center = float(np.sum(pixels * weights) / np.sum(weights))
    sigma = float(np.sqrt(np.sum(weights * (pixels - center) ** 2) / np.sum(weights)))
    return center, max(1.0, min(sigma, float(half_width)))


def _trace_contrast(image: np.ndarray, mask: np.ndarray, trace: np.ndarray) -> float:
    samples: list[float] = []
    background: list[float] = []
    height, width = image.shape
    for x in np.linspace(0, width - 1, 64, dtype=int):
        y = int(round(trace[x]))
        if y < 2 or y >= height - 2:
            continue
        local = image[y - 2 : y + 3, x]
        local_mask = mask[y - 2 : y + 3, x]
        if (~local_mask & np.isfinite(local)).any():
            samples.append(float(np.nanmedian(np.where(local_mask, np.nan, local))))
            column = np.where(mask[:, x], np.nan, image[:, x])
            background.append(float(np.nanmedian(column)))
    if len(samples) < 8:
        return 0.0
    residual = np.asarray(samples) - np.asarray(background)
    scatter = 1.4826 * np.median(np.abs(residual - np.median(residual)))
    return float(np.median(residual) / max(scatter, 1e-6))


def _fill_masked_for_aspired(image: np.ndarray, mask: np.ndarray) -> np.ndarray:
    """Fill bad pixels for ASPIRED tracing while retaining its explicit bad mask."""
    valid = ~mask & np.isfinite(image)
    if not valid.any():
        raise ValueError("Every science pixel is masked; extraction cannot continue.")
    values = gaussian_filter(
        np.where(valid, image, 0.0).astype(np.float32),
        sigma=(2.0, 2.0),
        mode="nearest",
    )
    weights = gaussian_filter(valid.astype(np.float32), sigma=(2.0, 2.0), mode="nearest")
    local = np.divide(
        values,
        weights,
        out=np.full_like(values, np.nan),
        where=weights > 1e-4,
    )
    filled = np.asarray(image, dtype=np.float32).copy()
    fill_mask = ~valid
    filled[fill_mask] = local[fill_mask]
    if not np.isfinite(filled).all():
        filled[~np.isfinite(filled)] = float(np.nanmedian(image[valid]))
    return filled
