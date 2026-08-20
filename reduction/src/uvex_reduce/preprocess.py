from __future__ import annotations

from dataclasses import dataclass
from fnmatch import fnmatch
from functools import partial
from pathlib import Path
import warnings as python_warnings

from astropy.io import fits
from astropy.stats import mad_std, sigma_clip
from astropy.utils.exceptions import AstropyUserWarning
import numpy as np
from scipy.ndimage import gaussian_filter, gaussian_filter1d, shift as image_shift
from scipy.signal import correlate, correlation_lags

from .config import DetectorConfig, OrientationConfig, PreprocessConfig
from .models import AlignmentShift


@dataclass(slots=True)
class LoadedFrame:
    path: Path
    data: np.ndarray
    header: fits.Header
    exposure_s: float | None
    temperature_c: float | None
    camera_gain: float | None
    x_binning: int | None
    y_binning: int | None
    instrument: str | None


@dataclass(slots=True)
class PreprocessedStack:
    image: np.ndarray
    variance: np.ndarray
    mask: np.ndarray
    header: fits.Header
    shifts: list[AlignmentShift]
    source_files: list[Path]
    rejected_files: list[Path]
    warnings: list[str]


def read_frame(path: str | Path) -> LoadedFrame:
    file_path = Path(path).expanduser().resolve()
    with fits.open(file_path, memmap=False, do_not_scale_image_data=False) as hdul:
        if hdul[0].data is None or hdul[0].data.ndim != 2:
            raise ValueError(f"{file_path} does not contain a two-dimensional primary image.")
        data = np.asarray(hdul[0].data, dtype=np.float32)
        header = hdul[0].header.copy()
    return LoadedFrame(
        path=file_path,
        data=data,
        header=header,
        exposure_s=_header_float(header, "EXPTIME", "EXPOSURE", "EXP_TIME"),
        temperature_c=_header_float(header, "CCD-TEMP", "CCD_TEMP", "SENSOR_TEMP"),
        camera_gain=_header_float(header, "GAIN", "EGAIN"),
        x_binning=_header_int(header, "XBINNING", "BINX"),
        y_binning=_header_int(header, "YBINNING", "BINY"),
        instrument=(str(header.get("INSTRUME", "")).strip() or None),
    )


def preprocess_science(
    science_paths: list[Path],
    bias_paths: list[Path],
    dark_paths: list[Path],
    flat_paths: list[Path],
    detector: DetectorConfig,
    options: PreprocessConfig,
    orientation: OrientationConfig,
) -> PreprocessedStack:
    if not science_paths:
        raise ValueError("At least one science frame is required.")

    warnings: list[str] = []
    science = [_read_frame_with_sdk_fix(path, options, warnings) for path in science_paths]
    _require_same_shape(science)
    _auto_repair_sdk_wrap_group(science, options, warnings, "science")
    _validate_science_settings(science, options)
    reference_exposure = _science_reference_exposure(science, options, warnings)
    master_bias = _master_bias(bias_paths, science[0].data.shape, options, warnings)
    master_dark = _master_dark(dark_paths, science, master_bias, options, warnings)
    master_flat, flat_mask = _master_flat(
        flat_paths,
        science[0],
        master_bias,
        detector,
        options,
        warnings,
    )
    bias_mask = _calibration_outlier_mask(master_bias, science[0].data.shape)

    corrected: list[np.ndarray] = []
    masks: list[np.ndarray] = []
    for frame in science:
        image = frame.data.copy()
        mask = ~np.isfinite(image) | (image >= detector.saturation_adu) | bias_mask
        if master_bias is not None:
            image -= master_bias
        if master_dark is not None:
            dark_image, dark_reference_exposure = master_dark
            scale = 1.0
            if frame.exposure_s is not None and dark_reference_exposure > 0:
                scale = frame.exposure_s / dark_reference_exposure
            image -= dark_image * scale
        if master_flat is not None:
            image = np.divide(
                image,
                master_flat,
                out=np.full_like(image, np.nan),
                where=~flat_mask,
            )
            mask |= flat_mask
        if options.cosmic_ray_clean:
            image, cosmic_count = _clean_cosmic_rays(image, mask, detector)
            if cosmic_count:
                warnings.append(f"{frame.path.name}: replaced {cosmic_count} cosmic-ray candidate pixels.")
        if reference_exposure is not None and frame.exposure_s is not None:
            image *= reference_exposure / frame.exposure_s
        corrected.append(image)
        masks.append(mask)

    if options.align_frames and len(corrected) > 1:
        corrected, masks, shifts, accepted_paths, rejected_paths = align_frames(
            corrected,
            masks,
            science_paths,
            options.maximum_shift_pixels,
            align_spatial=options.align_spatial,
            align_dispersion=options.align_dispersion,
            minimum_confidence=options.minimum_alignment_confidence,
            probe_shift=options.alignment_probe_shift_pixels,
            reject_unalignable=options.reject_unalignable_frames,
        )
    else:
        shifts = [AlignmentShift(path, 0.0, 0.0, 1.0) for path in science_paths]
        accepted_paths = list(science_paths)
        rejected_paths = []

    if rejected_paths:
        warnings.append(
            "Rejected unalignable science frame(s): "
            + ", ".join(path.name for path in rejected_paths)
        )

    stack = np.stack(corrected)
    stack_mask = np.stack(masks) | ~np.isfinite(stack)
    with python_warnings.catch_warnings():
        python_warnings.filterwarnings(
            "ignore",
            message="Input data contains invalid values.*",
            category=AstropyUserWarning,
        )
        clipped = sigma_clip(
            np.ma.array(stack, mask=stack_mask),
            sigma=options.sigma_clip,
            axis=0,
            maxiters=3,
            cenfunc="median",
            stdfunc=partial(
                _noise_aware_temporal_std,
                gain_e_per_adu=detector.gain_e_per_adu,
                read_noise_e=detector.read_noise_e,
            ),
            masked=True,
        )
    temporal_rejection = np.ma.getmaskarray(clipped) & ~stack_mask
    temporal_rejected_samples = int(np.count_nonzero(temporal_rejection))
    temporal_sample_count = int(np.count_nonzero(~stack_mask))
    combine_method = options.combine_method.lower()
    if combine_method == "mean":
        image = np.ma.mean(clipped, axis=0).filled(np.nan).astype(np.float32)
    else:
        image = np.ma.median(clipped, axis=0).filled(np.nan).astype(np.float32)
    effective_count = np.sum(~np.ma.getmaskarray(clipped), axis=0)
    if combine_method == "mean":
        combination_variance_factor = np.where(
            effective_count > 1,
            1.0 / np.maximum(effective_count, 1),
            1.0,
        )
    else:
        combination_variance_factor = np.where(
            effective_count > 1,
            np.pi / (2.0 * np.maximum(effective_count, 1)),
            1.0,
        )
    if len(stack) > 1:
        with python_warnings.catch_warnings():
            python_warnings.simplefilter("ignore", RuntimeWarning)
            sample_variance = np.ma.var(clipped, axis=0, ddof=1).filled(0.0)
        empirical_variance = sample_variance * combination_variance_factor
    else:
        empirical_variance = np.zeros_like(image)
    gain = max(detector.gain_e_per_adu, 1e-6)
    detector_variance = np.maximum(image, 0.0) / gain
    detector_variance += (detector.read_noise_e / gain) ** 2
    detector_variance *= combination_variance_factor
    variance = np.maximum(empirical_variance, detector_variance).astype(np.float32)
    mask = np.all(np.ma.getmaskarray(clipped), axis=0) | ~np.isfinite(image) | ~np.isfinite(variance)

    header = science[0].header.copy()
    header["NCOMBINE"] = (len(accepted_paths), "Number of aligned science exposures")
    header["BIASCOR"] = (master_bias is not None, "Bias correction applied")
    header["DARKCOR"] = (master_dark is not None, "Dark correction applied")
    header["FLATCOR"] = (master_flat is not None, "Flat correction applied")
    header["CRREJECT"] = (options.cosmic_ray_clean, "Per-frame L.A.Cosmic cleaning applied")
    header["COMBMETH"] = (combine_method.upper(), "Aligned science-frame combination")
    header["SIGCLIP"] = (options.sigma_clip, "Stack sigma-clipping threshold")
    header["TCRSAMP"] = (
        temporal_rejected_samples,
        "Samples rejected by temporal sigma clipping",
    )
    header["TCRFRAC"] = (
        temporal_rejected_samples / max(1, temporal_sample_count),
        "Fraction rejected by temporal sigma clipping",
    )
    accepted_set = set(accepted_paths)
    accepted_frames = [frame for frame in science if frame.path in accepted_set]
    accepted_exposures = [
        float(frame.exposure_s)
        for frame in accepted_frames
        if frame.exposure_s is not None and frame.exposure_s > 0
    ]
    if reference_exposure is not None:
        header["EXPTIME"] = (reference_exposure, "Reference exposure after rate scaling")
        header["EXPNORM"] = (True, "Input science frames scaled by exposure time")
    else:
        header["EXPNORM"] = (False, "Input science frames scaled by exposure time")
    if accepted_exposures:
        header["TOTEXP"] = (sum(accepted_exposures), "Total accepted exposure time, seconds")
        header["EXPMIN"] = (min(accepted_exposures), "Shortest accepted exposure, seconds")
        header["EXPMAX"] = (max(accepted_exposures), "Longest accepted exposure, seconds")
    header.add_history("UVEX-ADV reduction: bias/dark/flat calibration and robust aligned combine")
    if orientation.horizontal_flip:
        image, variance, mask = flip_horizontal(image, variance, mask)
        header["DISPFLIP"] = (True, "Horizontal flip applied before extraction")
        header.add_history("UVEX-ADV reduction: horizontal flip (raw left=red, right=blue)")
    else:
        header["DISPFLIP"] = (False, "Horizontal flip applied before extraction")
    if master_bias is not None or master_dark is not None or master_flat is not None:
        warnings.append(
            "Uncertainty includes science-frame scatter and detector noise, but not the "
            "finite-S/N uncertainty of master calibration frames."
        )
    return PreprocessedStack(
        image=image,
        variance=variance,
        mask=mask,
        header=header,
        shifts=shifts,
        source_files=accepted_paths,
        rejected_files=rejected_paths,
        warnings=warnings,
    )


def preprocess_arc(
    arc_paths: list[Path],
    bias_paths: list[Path],
    detector: DetectorConfig,
    options: PreprocessConfig,
    orientation: OrientationConfig,
) -> tuple[np.ndarray | None, fits.Header | None, list[str]]:
    if not arc_paths:
        return None, None, ["No arc frames were supplied; arc-based wavelength calibration is unavailable."]
    warnings: list[str] = []
    arcs = [_read_frame_with_sdk_fix(path, options, warnings) for path in arc_paths]
    _require_same_shape(arcs)
    _auto_repair_sdk_wrap_group(arcs, options, warnings, "arc")
    master_bias = _master_bias(bias_paths, arcs[0].data.shape, options, warnings)
    calibrated = []
    masks = []
    for arc in arcs:
        image = arc.data.copy()
        if master_bias is not None:
            image -= master_bias
        arc_mask = ~np.isfinite(image) | (arc.data >= detector.saturation_adu)
        image[arc_mask] = np.nan
        calibrated.append(image)
        masks.append(arc_mask)
    if options.align_frames and len(calibrated) > 1:
        reference_profile = _arc_profile(calibrated[0])
        aligned = [calibrated[0]]
        for arc, arc_mask, frame in zip(calibrated[1:], masks[1:], arcs[1:]):
            dx, confidence = estimate_profile_shift(
                reference_profile,
                _arc_profile(arc),
                options.maximum_shift_pixels,
            )
            if confidence < 0.2:
                dx = 0.0
            shifted, _ = _shift_masked_image(arc, arc_mask, 0.0, dx)
            aligned.append(shifted)
            warnings.append(
                f"Arc {frame.path.name}: applied dispersion shift {dx:+.3f} px "
                f"(confidence {confidence:.2f})."
            )
        calibrated = aligned
    arc_image = _robust_combine(calibrated, options.sigma_clip)
    header = arcs[0].header.copy()
    if orientation.horizontal_flip:
        arc_image = np.flip(arc_image, axis=1).copy()
        header.add_history("UVEX-ADV reduction: horizontal flip applied to arc")
    return arc_image, header, warnings


def flip_horizontal(
    image: np.ndarray,
    variance: np.ndarray,
    mask: np.ndarray,
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    return (
        np.flip(image, axis=1).copy(),
        np.flip(variance, axis=1).copy(),
        np.flip(mask, axis=1).copy(),
    )


def align_frames(
    images: list[np.ndarray],
    masks: list[np.ndarray],
    paths: list[Path],
    maximum_shift: float,
    *,
    align_spatial: bool = True,
    align_dispersion: bool = True,
    minimum_confidence: float = 0.10,
    probe_shift: float | None = None,
    reject_unalignable: bool = True,
) -> tuple[
    list[np.ndarray],
    list[np.ndarray],
    list[AlignmentShift],
    list[Path],
    list[Path],
]:
    reference = images[0]
    reference_y, reference_x = _alignment_profiles(reference)
    aligned_images = [reference]
    aligned_masks = [masks[0]]
    shifts = [AlignmentShift(paths[0], 0.0, 0.0, 1.0)]
    accepted_paths = [paths[0]]
    rejected_paths: list[Path] = []
    search_shift = max(maximum_shift, probe_shift or maximum_shift)
    for path, image, mask in zip(paths[1:], images[1:], masks[1:]):
        profile_y, profile_x = _alignment_profiles(image)
        if align_spatial:
            dy, confidence_y = estimate_profile_shift(reference_y, profile_y, search_shift)
            peak_dy, peak_confidence = estimate_trace_peak_shift(
                reference_y,
                profile_y,
                search_shift,
            )
            # Fixed-pattern rows can dominate a whole-profile correlation even
            # when the astronomical trace itself is unambiguous.  A pair of
            # >3-MAD trace peaks is the safer spatial registration anchor.  The
            # threshold is deliberately modest because the profile MAD also
            # contains real long-slit illumination structure; both frames must
            # independently agree on a dominant peak.
            if peak_confidence >= 0.5:
                dy, confidence_y = peak_dy, peak_confidence
        else:
            dy, confidence_y = 0.0, 1.0
        if align_dispersion:
            dx, confidence_x = estimate_profile_shift(reference_x, profile_x, search_shift)
        else:
            dx, confidence_x = 0.0, 1.0
        enabled_confidences = []
        if align_spatial:
            enabled_confidences.append(confidence_y)
        if align_dispersion:
            enabled_confidences.append(confidence_x)
        confidence = min(enabled_confidences) if enabled_confidences else 1.0
        outside_limit = (
            (align_spatial and abs(dy) > maximum_shift)
            or (align_dispersion and abs(dx) > maximum_shift)
        )
        low_confidence = confidence < minimum_confidence
        if reject_unalignable and (outside_limit or low_confidence):
            rejected_paths.append(path)
            continue
        if confidence_y < minimum_confidence:
            dy = 0.0
        if confidence_x < minimum_confidence:
            dx = 0.0
        shifted, shifted_mask = _shift_masked_image(image, mask, dy, dx)
        aligned_images.append(shifted.astype(np.float32))
        aligned_masks.append(shifted_mask)
        shifts.append(AlignmentShift(path, dx, dy, confidence))
        accepted_paths.append(path)
    return aligned_images, aligned_masks, shifts, accepted_paths, rejected_paths


def estimate_profile_shift(reference: np.ndarray, target: np.ndarray, maximum_shift: float) -> tuple[float, float]:
    reference = _normalise_profile(reference)
    target = _normalise_profile(target)
    correlation = correlate(target, reference, mode="full", method="fft")
    lags = correlation_lags(target.size, reference.size, mode="full")
    allowed = np.abs(lags) <= maximum_shift
    local = correlation[allowed]
    local_lags = lags[allowed]
    if local.size < 3 or not np.isfinite(local).any():
        return 0.0, 0.0
    index = int(np.nanargmax(local))
    lag = float(local_lags[index])
    if 0 < index < local.size - 1:
        left, center, right = local[index - 1 : index + 2]
        denominator = left - 2.0 * center + right
        if np.isfinite(denominator) and abs(denominator) > 1e-12:
            lag += float(0.5 * (left - right) / denominator)
    confidence = float(np.clip(local[index], -1.0, 1.0))
    return -lag, confidence


def estimate_trace_peak_shift(
    reference: np.ndarray,
    target: np.ndarray,
    maximum_shift: float,
) -> tuple[float, float]:
    """Estimate a spatial shift from a significant long-slit trace peak.

    The returned confidence reaches 0.5 at three robust sigma and saturates at
    one by six sigma.  A zero confidence asks the caller to retain the more
    general correlation estimate.
    """

    reference_center, reference_snr = _significant_profile_peak(reference)
    target_center, target_snr = _significant_profile_peak(target)
    minimum_snr = min(reference_snr, target_snr)
    if minimum_snr < 3.0:
        return 0.0, 0.0
    shift = reference_center - target_center
    if abs(shift) > maximum_shift:
        return shift, float(np.clip(minimum_snr / 6.0, 0.5, 1.0))
    return shift, float(np.clip(minimum_snr / 6.0, 0.5, 1.0))


def _significant_profile_peak(profile: np.ndarray) -> tuple[float, float]:
    clean = np.asarray(profile, dtype=float).copy()
    finite = np.isfinite(clean)
    if np.count_nonzero(finite) < 20:
        return 0.0, 0.0
    fill = float(np.median(clean[finite]))
    clean[~finite] = fill
    edge = min(20, max(1, clean.size // 20))
    search = clean.copy()
    search[:edge] = -np.inf
    search[-edge:] = -np.inf
    index = int(np.argmax(search))
    median = float(np.median(clean[edge:-edge]))
    mad = 1.4826 * float(np.median(np.abs(clean[edge:-edge] - median)))
    if not np.isfinite(mad) or mad <= 0:
        return float(index), 0.0
    center = float(index)
    if 0 < index < clean.size - 1:
        left, middle, right = clean[index - 1 : index + 2]
        denominator = left - 2.0 * middle + right
        if np.isfinite(denominator) and abs(denominator) > 1e-12:
            center += float(np.clip(0.5 * (left - right) / denominator, -0.5, 0.5))
    return center, float((clean[index] - median) / mad)


def _master_bias(
    paths: list[Path],
    expected_shape: tuple[int, int],
    options: PreprocessConfig,
    warnings: list[str],
) -> np.ndarray | None:
    if not options.use_bias or not paths:
        warnings.append("No bias correction applied." if not paths else "Bias correction disabled by configuration.")
        return None
    frames = [_read_frame_with_sdk_fix(path, options, warnings) for path in paths]
    _auto_repair_sdk_wrap_group(frames, options, warnings, "bias")
    if any(frame.data.shape != expected_shape for frame in frames):
        warnings.append("Bias frames do not match the science dimensions; bias correction skipped.")
        return None
    return _robust_combine([frame.data for frame in frames], options.sigma_clip)


def _master_dark(
    paths: list[Path],
    science: list[LoadedFrame],
    master_bias: np.ndarray | None,
    options: PreprocessConfig,
    warnings: list[str],
) -> tuple[np.ndarray, float] | None:
    if not options.use_dark or not paths:
        warnings.append("No dark correction applied." if not paths else "Dark correction disabled by configuration.")
        return None
    master_flags = [path.stem.lower() in {"dark", "masterdark"} for path in paths]
    if any(master_flags) and len(paths) > 1:
        warnings.append("Master dark and raw dark files were selected together; dark correction skipped.")
        return None
    darks = [_read_frame_with_sdk_fix(path, options, warnings) for path in paths]
    _auto_repair_sdk_wrap_group(darks, options, warnings, "dark")
    if any(frame.data.shape != science[0].data.shape for frame in darks):
        warnings.append("Dark frames do not match the science dimensions; dark correction skipped.")
        return None
    # A dark library is intentionally reusable across nights.  Its acquisition
    # date is therefore not a compatibility criterion, but the detector/readout
    # identity is: subtracting a same-sized frame from another gain, binning, or
    # camera can imprint more structure than it removes.
    reference = science[0]
    compatible: list[LoadedFrame] = []
    incompatible: list[str] = []
    for frame in darks:
        reasons = _camera_compatibility_reasons(reference, frame)
        if reasons:
            incompatible.append(f"{frame.path.name} ({'; '.join(reasons)})")
        else:
            compatible.append(frame)
    if incompatible:
        warnings.append(
            "Rejected calibration-incompatible dark frame(s): "
            + ", ".join(incompatible)
        )
    darks = compatible
    if not darks:
        warnings.append("No camera-compatible dark frames remain; dark correction skipped.")
        return None
    science_temperatures = [frame.temperature_c for frame in science if frame.temperature_c is not None]
    dark_temperatures = [frame.temperature_c for frame in darks if frame.temperature_c is not None]
    if science_temperatures and len(dark_temperatures) != len(darks):
        warnings.append("One or more dark temperatures are missing; dark correction skipped.")
        return None
    if dark_temperatures and len(science_temperatures) != len(science):
        warnings.append("One or more science temperatures are missing; dark correction skipped.")
        return None
    if science_temperatures and dark_temperatures:
        delta = abs(float(np.median(science_temperatures)) - float(np.median(dark_temperatures)))
        if delta > options.maximum_dark_temperature_delta_c:
            warnings.append(
                f"Dark temperature differs from science by {delta:.1f} C; limit is "
                f"{options.maximum_dark_temperature_delta_c:.1f} C, so dark correction was skipped."
            )
            return None
    dark_exposures = [frame.exposure_s for frame in darks]
    science_exposures = [frame.exposure_s for frame in science]
    if any(value is None or value <= 0 for value in dark_exposures):
        warnings.append("Dark exposure time is unknown; dark correction skipped.")
        return None
    if any(value is None or value <= 0 for value in science_exposures):
        warnings.append("Science exposure time is unknown; dark correction skipped.")
        return None
    dark_exposures = [float(value) for value in dark_exposures if value is not None]
    science_exposures = [float(value) for value in science_exposures if value is not None]
    reference_exposure = float(np.median(dark_exposures))
    relative_delta = max(
        abs(value - reference_exposure) / reference_exposure for value in science_exposures
    )
    dark_spread = max(
        abs(value - reference_exposure) / reference_exposure for value in dark_exposures
    )
    scaling_required = relative_delta > 0.02 or dark_spread > 0.02
    if scaling_required and not options.allow_dark_exposure_scaling:
        warnings.append(
            "Dark exposure does not match science and scaling is disabled; dark correction skipped."
        )
        return None
    if scaling_required and master_bias is None:
        warnings.append("Dark scaling requires bias-subtracted darks; dark correction skipped.")
        return None
    images = []
    for frame, exposure in zip(darks, dark_exposures):
        image = frame.data - master_bias if master_bias is not None else frame.data.copy()
        image = image * (reference_exposure / exposure)
        images.append(image)
    return _robust_combine(images, options.sigma_clip), reference_exposure


def _camera_compatibility_reasons(
    reference: LoadedFrame,
    calibration: LoadedFrame,
) -> list[str]:
    """Return detector/readout mismatches, requiring known calibration metadata."""

    reasons: list[str] = []
    if reference.camera_gain is not None:
        if calibration.camera_gain is None:
            reasons.append("GAIN is missing")
        elif not np.isclose(
            reference.camera_gain,
            calibration.camera_gain,
            rtol=0,
            atol=1e-6,
        ):
            reasons.append(
                f"GAIN {calibration.camera_gain:g} != science {reference.camera_gain:g}"
            )
    if reference.x_binning is not None and reference.y_binning is not None:
        if calibration.x_binning is None or calibration.y_binning is None:
            reasons.append("binning metadata is missing")
        elif (calibration.x_binning, calibration.y_binning) != (
            reference.x_binning,
            reference.y_binning,
        ):
            reasons.append(
                f"binning {calibration.x_binning}x{calibration.y_binning} != science "
                f"{reference.x_binning}x{reference.y_binning}"
            )
    if reference.instrument:
        if not calibration.instrument:
            reasons.append("camera identity is missing")
        elif reference.instrument.casefold() != calibration.instrument.casefold():
            reasons.append(
                f"camera {calibration.instrument!r} != science {reference.instrument!r}"
            )
    return reasons


def _master_flat(
    paths: list[Path],
    reference: LoadedFrame,
    master_bias: np.ndarray | None,
    detector: DetectorConfig,
    options: PreprocessConfig,
    warnings: list[str],
) -> tuple[np.ndarray | None, np.ndarray]:
    expected_shape = reference.data.shape
    if not options.use_flat or not paths:
        warnings.append("No flat correction applied." if not paths else "Flat correction disabled by configuration.")
        return None, np.zeros((1, 1), dtype=bool)
    flats = [_read_frame_with_sdk_fix(path, options, warnings) for path in paths]
    _require_same_shape(flats)
    compatible: list[LoadedFrame] = []
    incompatible: list[str] = []
    for frame in flats:
        reasons: list[str] = []
        if (
            reference.camera_gain is not None
            and frame.camera_gain is not None
            and not np.isclose(reference.camera_gain, frame.camera_gain, rtol=0, atol=1e-6)
        ):
            reasons.append(
                f"GAIN {frame.camera_gain:g} != science {reference.camera_gain:g}"
            )
        if (
            reference.x_binning is not None
            and reference.y_binning is not None
            and frame.x_binning is not None
            and frame.y_binning is not None
            and (frame.x_binning, frame.y_binning)
            != (reference.x_binning, reference.y_binning)
        ):
            reasons.append(
                f"binning {frame.x_binning}x{frame.y_binning} != science "
                f"{reference.x_binning}x{reference.y_binning}"
            )
        if (
            reference.instrument
            and frame.instrument
            and reference.instrument.casefold() != frame.instrument.casefold()
        ):
            reasons.append(
                f"camera {frame.instrument!r} != science {reference.instrument!r}"
            )
        if reasons:
            incompatible.append(f"{frame.path.name} ({'; '.join(reasons)})")
        else:
            compatible.append(frame)
    if incompatible:
        warnings.append(
            "Rejected calibration-incompatible flat frame(s): "
            + ", ".join(incompatible)
        )
    flats = compatible
    if len(flats) < options.minimum_flat_frames:
        warnings.append(
            f"Only {len(flats)} camera-compatible flat frame(s) remain; at least "
            f"{options.minimum_flat_frames} are required, so flat correction was skipped."
        )
        return None, np.zeros((1, 1), dtype=bool)
    _auto_repair_sdk_wrap_group(flats, options, warnings, "flat")
    if flats[0].data.shape != expected_shape:
        warnings.append("Flat frames do not match the science dimensions; flat correction skipped.")
        return None, np.zeros((1, 1), dtype=bool)
    normalised_frames: list[np.ndarray] = []
    rejected: list[str] = []
    for frame in flats:
        image = frame.data - master_bias if master_bias is not None else frame.data.copy()
        image = image.astype(np.float32, copy=False)
        invalid = ~np.isfinite(image) | (frame.data >= detector.saturation_adu)
        saturation_fraction = float(np.mean(frame.data >= detector.saturation_adu))
        if saturation_fraction > options.maximum_flat_saturation_fraction:
            rejected.append(
                f"{frame.path.name} ({saturation_fraction:.2%} saturated)"
            )
            continue
        image[invalid] = np.nan
        smooth = _normalised_gaussian_filter(image, sigma=(25.0, 100.0))
        illumination_floor = float(np.nanpercentile(smooth, 10.0))
        valid = (
            np.isfinite(image)
            & np.isfinite(smooth)
            & (smooth > illumination_floor)
        )
        normalised = np.full_like(image, np.nan, dtype=np.float32)
        normalised[valid] = image[valid] / smooth[valid]
        valid &= (normalised > 0.5) & (normalised < 1.5)
        normalised[~valid] = np.nan
        normalised_frames.append(normalised)

    if rejected:
        warnings.append("Rejected saturated flat frame(s): " + ", ".join(rejected))
    if len(normalised_frames) < options.minimum_flat_frames:
        warnings.append(
            f"Only {len(normalised_frames)} usable flat frame(s) remain; at least "
            f"{options.minimum_flat_frames} are required, so flat correction was skipped."
        )
        return None, np.zeros((1, 1), dtype=bool)

    coverage = np.zeros(expected_shape, dtype=np.uint16)
    for frame in normalised_frames:
        coverage += np.isfinite(frame)
    master = _robust_combine(normalised_frames, options.sigma_clip)
    minimum_coverage = max(1, int(np.ceil(0.5 * len(normalised_frames))))
    valid = (
        np.isfinite(master)
        & (coverage >= minimum_coverage)
        & (master > 0.5)
        & (master < 1.5)
    )
    valid_fraction = float(np.mean(valid))
    if valid_fraction < options.minimum_flat_valid_fraction:
        warnings.append(
            f"Master flat covers only {valid_fraction:.1%} of detector pixels; the "
            f"configured minimum is {options.minimum_flat_valid_fraction:.1%}, so flat "
            "correction was skipped."
        )
        return None, np.zeros((1, 1), dtype=bool)
    normalizer = float(np.nanmedian(master[valid]))
    master = master / normalizer
    flat_mask = ~valid
    master[flat_mask] = 1.0
    warnings.append(
        f"Master flat used {len(normalised_frames)} individually illumination-normalised "
        f"frame(s) and covers {valid_fraction:.1%} of detector pixels; validate its "
        "effect on the standard-star template before accepting it."
    )
    return master.astype(np.float32), flat_mask


def _calibration_outlier_mask(
    master_bias: np.ndarray | None,
    shape: tuple[int, int],
) -> np.ndarray:
    if master_bias is None:
        return np.zeros(shape, dtype=bool)
    finite = master_bias[np.isfinite(master_bias)]
    if finite.size == 0:
        return np.ones(shape, dtype=bool)
    median = float(np.median(finite))
    mad = 1.4826 * float(np.median(np.abs(finite - median)))
    threshold = max(50.0, 20.0 * mad)
    return ~np.isfinite(master_bias) | (np.abs(master_bias - median) > threshold)


def _clean_cosmic_rays(
    image: np.ndarray,
    mask: np.ndarray,
    detector: DetectorConfig,
) -> tuple[np.ndarray, int]:
    from astropy.nddata import CCDData
    from ccdproc import cosmicray_lacosmic

    ccd = CCDData(
        np.asarray(image, dtype=np.float32),
        unit="adu",
        mask=np.asarray(mask, dtype=bool),
    )
    cleaned = cosmicray_lacosmic(
        ccd,
        gain=detector.gain_e_per_adu,
        readnoise=detector.read_noise_e,
        satlevel=detector.saturation_adu,
        sigclip=5.0,
        sigfrac=0.3,
        objlim=8.0,
        niter=3,
        sepmed=True,
        cleantype="medmask",
        fsmode="median",
        gain_apply=False,
        verbose=False,
    )
    cosmic_mask = np.asarray(cleaned.mask, dtype=bool) & ~mask
    return np.asarray(cleaned.data, dtype=np.float32), int(np.count_nonzero(cosmic_mask))


def _normalised_gaussian_filter(
    image: np.ndarray,
    sigma: tuple[float, float],
) -> np.ndarray:
    valid = np.isfinite(image)
    values = gaussian_filter(
        np.where(valid, image, 0.0).astype(np.float32),
        sigma=sigma,
        mode="nearest",
    )
    weights = gaussian_filter(valid.astype(np.float32), sigma=sigma, mode="nearest")
    return np.divide(
        values,
        weights,
        out=np.full_like(values, np.nan),
        where=weights > 1e-4,
    )


def _read_frame_with_sdk_fix(
    path: str | Path,
    options: PreprocessConfig,
    warnings: list[str],
) -> LoadedFrame:
    """Read one frame and apply the documented 64-pixel SDK repair when selected.

    The repair mirrors ``Desktop/fix.py`` but is performed in memory, leaving the
    original FITS untouched.  Matching accepts a basename, an absolute path, or a
    glob against either representation.  A prior HISTORY marker prevents a second
    application to an already repaired file.
    """

    frame = read_frame(path)
    if not _matches_sdk_fix(frame.path, options.sdk_wrap_fix_files):
        return frame

    history = str(frame.header.get("HISTORY", ""))
    if "Fixed 64-pixel wrap-around bug" in history or "UVEX-ADV SDK wrap fix" in history:
        warnings.append(f"{frame.path.name}: SDK wrap fix already present in FITS HISTORY; not applied twice.")
        return frame

    shift = int(options.sdk_wrap_shift_pixels)
    if frame.data.shape[-1] <= shift:
        raise ValueError(
            f"{frame.path}: image width {frame.data.shape[-1]} is too small for the "
            f"configured {shift}-pixel SDK wrap repair."
        )
    direction = options.sdk_wrap_fix_direction.lower()
    applied_shift = -shift if direction == "left" else shift
    _apply_sdk_wrap_repair(frame, applied_shift)
    warnings.append(
        f"{frame.path.name}: applied documented ATR585M SDK wrap repair "
        f"(cyclic x shift {applied_shift:+d} px, direction={direction}); "
        "original FITS was not modified."
    )
    return frame


def _auto_repair_sdk_wrap_group(
    frames: list[LoadedFrame],
    options: PreprocessConfig,
    warnings: list[str],
    group_name: str,
) -> None:
    """Detect the documented ATR585M x-wrap from its discontinuity at column 64.

    A genuinely wrapped frame has the detector's right edge followed by its left
    edge at the configured wrap column.  This produces a strong discontinuity at
    exactly that boundary in the extracted/illumination profile.  The repair is
    limited to ATR585M headers, is performed only in memory, and is recorded in
    HISTORY; source FITS files are never rewritten.
    """

    if not options.auto_detect_sdk_wrap:
        return
    if len(frames) < 2:
        warnings.append(
            f"Automatic ATR585M SDK x-wrap repair was not attempted for the single-frame "
            f"{group_name} group; absolute wrap state requires a group reference or an "
            "explicit sdk_wrap_fix_files entry."
        )
        return
    shift = int(options.sdk_wrap_shift_pixels)
    scores = [_sdk_wrap_seam_score(frame.data, shift) for frame in frames]
    anchor_index = next(
        (
            index
            for index, score in enumerate(scores)
            if np.isfinite(score) and score < options.sdk_wrap_seam_sigma
        ),
        None,
    )
    anchor_profile = (
        _alignment_profiles(frames[anchor_index].data)[1]
        if anchor_index is not None
        else None
    )
    for frame, score in zip(frames, scores):
        history = str(frame.header.get("HISTORY", ""))
        if "UVEX-ADV SDK wrap fix" in history or "Fixed 64-pixel wrap-around bug" in history:
            continue
        instrument = (frame.instrument or "").casefold()
        if "atr585" not in instrument:
            continue
        if not np.isfinite(score) or score < options.sdk_wrap_seam_sigma:
            continue
        # Near-threshold seams in faint targets need independent evidence.  If
        # the group contains a low-seam anchor, require the characteristic
        # approximately 64-pixel relative displacement before changing data.
        if anchor_profile is not None and score < 10.0:
            profile = _alignment_profiles(frame.data)[1]
            relative_shift, confidence = estimate_profile_shift(
                anchor_profile,
                profile,
                shift + 20.0,
            )
            if confidence < 0.10 or abs(abs(relative_shift) - shift) > 12.0:
                continue
        _apply_sdk_wrap_repair(frame, -shift)
        warnings.append(
            f"{frame.path.name}: automatically detected ATR585M SDK x-wrap in the "
            f"{group_name} group (seam score {score:.1f} sigma) and applied cyclic "
            f"x shift {-shift:+d} px in memory; original FITS was not modified."
        )


def _sdk_wrap_seam_score(image: np.ndarray, shift_pixels: int = 64) -> float:
    if image.ndim != 2 or image.shape[1] <= shift_pixels + 16:
        return float("nan")
    finite = np.where(np.isfinite(image), image, np.nan)
    column_background = np.nanmedian(finite, axis=0)
    spatial = np.nanmean(finite - column_background, axis=1)
    if not np.isfinite(spatial).any():
        return float("nan")
    center = int(np.nanargmax(spatial))
    low = max(0, center - 15)
    high = min(image.shape[0], center + 16)
    profile = np.nanmean(finite[low:high], axis=0) - column_background
    profile = gaussian_filter1d(
        np.nan_to_num(profile, nan=float(np.nanmedian(profile))),
        sigma=1.0,
        mode="nearest",
    )
    differences = np.abs(np.diff(profile))
    seam_index = shift_pixels - 1
    high = min(differences.size, max(160, 3 * shift_pixels))
    indices = np.arange(8, high)
    indices = indices[np.abs(indices - seam_index) > 5]
    baseline = differences[indices]
    baseline = baseline[np.isfinite(baseline)]
    if baseline.size < 20 or not np.isfinite(differences[seam_index]):
        return float("nan")
    center = float(np.median(baseline))
    scatter = 1.4826 * float(np.median(np.abs(baseline - center)))
    scale = max(scatter, 0.1 * center, 1.0)
    return float((differences[seam_index] - center) / scale)


def _apply_sdk_wrap_repair(frame: LoadedFrame, applied_shift: int) -> None:
    frame.data = np.roll(frame.data, applied_shift, axis=-1).copy()
    frame.header["SDKWRAP"] = (True, "UVEX-ADV repaired cyclic SDK x-wrap in memory")
    frame.header.add_history(
        f"UVEX-ADV SDK wrap fix: cyclic x shift {applied_shift:+d} px applied in memory"
    )


def _matches_sdk_fix(path: Path, patterns: list[str]) -> bool:
    candidates = (str(path).casefold(), path.as_posix().casefold(), path.name.casefold())
    for pattern in patterns:
        folded = str(pattern).casefold()
        if any(fnmatch(candidate, folded) for candidate in candidates):
            return True
    return False


def _shift_masked_image(
    image: np.ndarray,
    mask: np.ndarray,
    dy: float,
    dx: float,
) -> tuple[np.ndarray, np.ndarray]:
    valid = ~mask & np.isfinite(image)
    shifted_values = image_shift(
        np.where(valid, image, 0.0),
        shift=(dy, dx),
        order=1,
        mode="constant",
        cval=0.0,
        prefilter=False,
    )
    shifted_weights = image_shift(
        valid.astype(np.float32),
        shift=(dy, dx),
        order=1,
        mode="constant",
        cval=0.0,
        prefilter=False,
    )
    shifted = np.divide(
        shifted_values,
        shifted_weights,
        out=np.full_like(shifted_values, np.nan),
        where=shifted_weights > 1e-4,
    )
    # Conservatively mask any output pixel whose interpolation footprint
    # included a bad/input-edge pixel.
    shifted_mask = shifted_weights < 0.999
    return shifted.astype(np.float32), shifted_mask


def _mad_std_with_zero_fallback(
    values: np.ndarray | np.ma.MaskedArray,
    axis: int | tuple[int, ...] | None = None,
) -> np.ndarray | float:
    """Use MAD for outliers, but retain a finite scale when the MAD is zero.

    Short, quantized stacks can contain repeated values.  A literal zero MAD
    would reject every differing sample regardless of the configured sigma;
    the ordinary standard deviation is a conservative fallback only at those
    pixels.
    """

    robust = np.asanyarray(mad_std(values, axis=axis, ignore_nan=True))
    fallback = np.asanyarray(np.ma.filled(np.ma.std(values, axis=axis), np.nan))
    scale = np.where(np.isfinite(robust) & (robust > 0), robust, fallback)
    return float(scale) if scale.ndim == 0 else scale


def _noise_aware_temporal_std(
    values: np.ndarray | np.ma.MaskedArray,
    axis: int | tuple[int, ...] | None = None,
    *,
    gain_e_per_adu: float,
    read_noise_e: float,
) -> np.ndarray | float:
    """MAD scale with a conservative detector-noise lower bound.

    The 1.5 multiplier makes a configured 4.5-sigma rejection equivalent to
    at least 6.75 expected Poisson/read-noise sigmas.  This matters for 3--7
    frame stacks, whose sample MAD can otherwise be spuriously small.
    """

    robust = np.asanyarray(mad_std(values, axis=axis, ignore_nan=True))
    median = np.asanyarray(np.ma.filled(np.ma.median(values, axis=axis), np.nan))
    gain = max(float(gain_e_per_adu), 1e-9)
    read_adu = float(read_noise_e) / gain
    detector_noise = np.sqrt(np.maximum(median, 0.0) / gain + read_adu**2)
    floor = 1.5 * detector_noise
    fallback = np.asanyarray(np.ma.filled(np.ma.std(values, axis=axis), np.nan))
    scale = np.where(np.isfinite(robust) & (robust > 0), robust, fallback)
    scale = np.maximum(scale, floor)
    return float(scale) if scale.ndim == 0 else scale


def _robust_combine(images: list[np.ndarray], sigma: float) -> np.ndarray:
    stack = np.stack(images).astype(np.float32, copy=False)
    with python_warnings.catch_warnings():
        python_warnings.filterwarnings(
            "ignore",
            message="Input data contains invalid values.*",
            category=AstropyUserWarning,
        )
        python_warnings.simplefilter("ignore", RuntimeWarning)
        clipped = sigma_clip(
            stack,
            sigma=sigma,
            axis=0,
            maxiters=3,
            cenfunc="median",
            stdfunc=_mad_std_with_zero_fallback,
            masked=True,
        )
        return np.ma.median(clipped, axis=0).filled(np.nan).astype(np.float32)


def _alignment_profiles(image: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    finite = np.where(np.isfinite(image), image, np.nan)
    column_background = np.nanmedian(finite, axis=0, keepdims=True)
    spatial = np.nanmedian(finite - column_background, axis=1)
    spatial = gaussian_filter1d(np.nan_to_num(spatial, nan=0.0), sigma=3.0)
    center = int(np.nanargmax(spatial)) if np.isfinite(spatial).any() else image.shape[0] // 2
    low = max(0, center - 40)
    high = min(image.shape[0], center + 41)
    spectrum = np.nanmedian(finite[low:high], axis=0) - np.nanmedian(finite, axis=0)
    continuum = gaussian_filter1d(np.nan_to_num(spectrum, nan=0.0), sigma=40.0)
    return spatial, np.nan_to_num(spectrum, nan=0.0) - continuum


def _arc_profile(image: np.ndarray) -> np.ndarray:
    profile = np.nanmedian(image, axis=0)
    clean = np.nan_to_num(profile, nan=float(np.nanmedian(profile)))
    return clean - gaussian_filter1d(clean, sigma=40.0)


def _normalise_profile(profile: np.ndarray) -> np.ndarray:
    clean = np.nan_to_num(np.asarray(profile, dtype=float), nan=0.0)
    clean -= np.median(clean)
    scale = np.linalg.norm(clean)
    return clean if scale <= 0 else clean / scale


def _validate_science_settings(
    frames: list[LoadedFrame],
    options: PreprocessConfig,
) -> None:
    if not frames:
        return
    binnings = {
        (frame.x_binning, frame.y_binning)
        for frame in frames
        if frame.x_binning is not None and frame.y_binning is not None
    }
    if len(binnings) > 1:
        rendered = ", ".join(f"{x}x{y}" for x, y in sorted(binnings))
        raise ValueError(
            f"Science frames use mixed camera binning ({rendered}); split them into "
            "separate reductions."
        )
    gains = {
        round(float(frame.camera_gain), 6)
        for frame in frames
        if frame.camera_gain is not None
    }
    if options.reject_mixed_camera_gain and len(gains) > 1:
        rendered = ", ".join(f"{value:g}" for value in sorted(gains))
        raise ValueError(
            f"Science frames use mixed camera GAIN settings ({rendered}); their ADU "
            "scales/noise models are not safely interchangeable, so split them into "
            "separate reductions."
        )


def _science_reference_exposure(
    frames: list[LoadedFrame],
    options: PreprocessConfig,
    warnings: list[str],
) -> float | None:
    exposures = [frame.exposure_s for frame in frames]
    known = [float(value) for value in exposures if value is not None and value > 0]
    if not options.normalize_science_exposure or not known:
        return None
    if len(known) != len(frames):
        raise ValueError(
            "Science frames mix known and missing/non-positive exposure times; they "
            "cannot be safely rate-normalised before stacking."
        )
    reference = float(np.median(known))
    spread = max(known) / min(known)
    if spread > 1.001:
        warnings.append(
            f"Science exposures span {min(known):g}--{max(known):g} s; every calibrated "
            f"frame was scaled to the median {reference:g} s reference exposure before "
            "alignment and robust combination."
        )
    return reference


def _require_same_shape(frames: list[LoadedFrame]) -> None:
    if not frames:
        return
    shape = frames[0].data.shape
    mismatched = [str(frame.path) for frame in frames if frame.data.shape != shape]
    if mismatched:
        raise ValueError(f"FITS dimensions do not match: {', '.join(mismatched)}")


def _header_float(header: fits.Header, *keys: str) -> float | None:
    for key in keys:
        try:
            value = header.get(key)
            if value is not None:
                return float(value)
        except (TypeError, ValueError):
            continue
    return None


def _header_int(header: fits.Header, *keys: str) -> int | None:
    value = _header_float(header, *keys)
    return None if value is None else int(round(value))
