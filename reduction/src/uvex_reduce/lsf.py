from __future__ import annotations

from dataclasses import dataclass

import numpy as np


@dataclass(frozen=True, slots=True)
class ReplicaKernelAnchor:
    """Local empirical model for a secondary copy on the low-pixel side.

    ``secondary_to_primary`` is the integrated secondary/main ratio, not the
    secondary peak height.  Coordinates and offsets are expressed in detector
    pixels after the spectrum has been oriented with wavelength increasing to
    the right.
    """

    coordinate_pixel: float
    offset_pixels: float
    secondary_to_primary: float
    secondary_blur_sigma_pixels: float = 0.0


@dataclass(slots=True)
class ReplicaRemovalResult:
    corrected: np.ndarray
    uncertainty: np.ndarray | None
    offset_pixels: np.ndarray
    secondary_to_primary: np.ndarray
    secondary_blur_sigma_pixels: np.ndarray


def add_left_shifted_replica(
    signal: np.ndarray,
    offset_pixels: float,
    secondary_to_primary: float,
    secondary_blur_sigma_pixels: float = 0.0,
) -> np.ndarray:
    """Apply a normalized, shifted-replica kernel for tests and simulations.

    A feature at pixel ``i`` receives a secondary copy at ``i-offset``.  The
    kernel is normalized, so an isolated feature away from the array edges
    retains its integrated flux.
    """

    values = _one_dimensional_finite_array(signal, "signal")
    offset, ratio, blur = _validate_kernel(
        offset_pixels,
        secondary_to_primary,
        secondary_blur_sigma_pixels,
    )
    if ratio == 0:
        return values.copy()
    return (values + ratio * _replica_operator(values, offset, blur)) / (1.0 + ratio)


def remove_left_shifted_replica(
    observed: np.ndarray,
    offset_pixels: float,
    secondary_to_primary: float,
    uncertainty: np.ndarray | None = None,
    *,
    secondary_blur_sigma_pixels: float = 0.0,
    maximum_echoes: int = 14,
) -> tuple[np.ndarray, np.ndarray | None]:
    """Invert one normalized shifted-replica kernel with a finite Neumann sum.

    This is deliberately a diagnostic operation rather than blind sharpening.
    Ratios must remain below one so the inverse is stable.  Noise is propagated
    through the same finite series; correlations introduced by interpolation
    are not modelled.
    """

    values = _one_dimensional_finite_array(observed, "observed")
    offset, ratio, blur = _validate_kernel(
        offset_pixels,
        secondary_to_primary,
        secondary_blur_sigma_pixels,
    )
    if maximum_echoes < 1:
        raise ValueError("maximum_echoes must be at least one.")

    variance = None
    if uncertainty is not None:
        errors = _one_dimensional_finite_array(uncertainty, "uncertainty")
        if errors.shape != values.shape:
            raise ValueError("uncertainty must have the same shape as observed.")
        if np.any(errors < 0):
            raise ValueError("uncertainty cannot contain negative values.")
        variance = np.square(errors)

    if ratio == 0:
        return values.copy(), None if variance is None else np.sqrt(variance)

    scale = 1.0 + ratio
    corrected = np.zeros_like(values)
    corrected_variance = None if variance is None else np.zeros_like(variance)
    shifted_values = values.copy()
    shifted_variance = None if variance is None else variance.copy()
    for echo in range(maximum_echoes + 1):
        coefficient = scale * ((-ratio) ** echo)
        corrected += coefficient * shifted_values
        if corrected_variance is not None:
            corrected_variance += coefficient**2 * shifted_variance
        shifted_values = _replica_operator(shifted_values, offset, blur)
        if shifted_variance is not None:
            shifted_variance = _replica_variance_operator(shifted_variance, offset, blur)

    corrected_uncertainty = (
        None if corrected_variance is None else np.sqrt(np.maximum(corrected_variance, 0.0))
    )
    return corrected, corrected_uncertainty


def remove_wavelength_dependent_left_replica(
    observed: np.ndarray,
    anchors: list[ReplicaKernelAnchor],
    uncertainty: np.ndarray | None = None,
    *,
    maximum_echoes: int = 14,
) -> ReplicaRemovalResult:
    """Blend locally inverted kernels between empirical wavelength anchors."""

    values = _one_dimensional_finite_array(observed, "observed")
    if not anchors:
        raise ValueError("At least one replica-kernel anchor is required.")
    ordered = sorted(anchors, key=lambda anchor: anchor.coordinate_pixel)
    coordinates = np.asarray([anchor.coordinate_pixel for anchor in ordered], dtype=float)
    if np.any(~np.isfinite(coordinates)) or np.any(np.diff(coordinates) <= 0):
        raise ValueError("Anchor coordinates must be finite and strictly increasing.")

    corrections: list[np.ndarray] = []
    propagated: list[np.ndarray | None] = []
    for anchor in ordered:
        corrected, corrected_uncertainty = remove_left_shifted_replica(
            values,
            anchor.offset_pixels,
            anchor.secondary_to_primary,
            uncertainty,
            secondary_blur_sigma_pixels=anchor.secondary_blur_sigma_pixels,
            maximum_echoes=maximum_echoes,
        )
        corrections.append(corrected)
        propagated.append(corrected_uncertainty)

    pixels = np.arange(values.size, dtype=float)
    offset_curve = np.interp(
        pixels,
        coordinates,
        [anchor.offset_pixels for anchor in ordered],
    )
    ratio_curve = np.interp(
        pixels,
        coordinates,
        [anchor.secondary_to_primary for anchor in ordered],
    )
    blur_curve = np.interp(
        pixels,
        coordinates,
        [anchor.secondary_blur_sigma_pixels for anchor in ordered],
    )
    output = _piecewise_blend(corrections, coordinates, pixels)

    output_uncertainty = None
    if uncertainty is not None:
        # All local inversions originate from the same samples and are strongly
        # correlated.  Linear interpolation is more conservative here than
        # pretending the two neighbouring estimates are independent.
        uncertainty_arrays = [item for item in propagated if item is not None]
        output_uncertainty = _piecewise_blend(uncertainty_arrays, coordinates, pixels)

    return ReplicaRemovalResult(
        corrected=output,
        uncertainty=output_uncertainty,
        offset_pixels=offset_curve,
        secondary_to_primary=ratio_curve,
        secondary_blur_sigma_pixels=blur_curve,
    )


def _piecewise_blend(
    values: list[np.ndarray],
    coordinates: np.ndarray,
    pixels: np.ndarray,
) -> np.ndarray:
    if len(values) == 1:
        return values[0].copy()
    output = np.empty_like(values[0])
    output[pixels <= coordinates[0]] = values[0][pixels <= coordinates[0]]
    output[pixels >= coordinates[-1]] = values[-1][pixels >= coordinates[-1]]
    for index in range(len(values) - 1):
        left = coordinates[index]
        right = coordinates[index + 1]
        # Include both anchor coordinates.  Leaving an exact integer anchor out
        # of every interval would retain uninitialised output at precisely the
        # high-S/N line used to define the kernel.
        selected = (pixels >= left) & (pixels <= right)
        weight = (pixels[selected] - left) / (right - left)
        output[selected] = (
            (1.0 - weight) * values[index][selected]
            + weight * values[index + 1][selected]
        )
    return output


def _sample_to_left(values: np.ndarray, displacement: float) -> np.ndarray:
    if displacement == 0:
        return values.copy()
    pixels = np.arange(values.size, dtype=float)
    return np.interp(pixels + displacement, pixels, values, left=0.0, right=0.0)


def _sample_variance_to_left(variance: np.ndarray, displacement: float) -> np.ndarray:
    if displacement == 0:
        return variance.copy()
    size = variance.size
    positions = np.arange(size, dtype=float) + displacement
    lower = np.floor(positions).astype(int)
    fraction = positions - lower
    output = np.zeros_like(variance)
    valid_lower = (lower >= 0) & (lower < size)
    output[valid_lower] += np.square(1.0 - fraction[valid_lower]) * variance[lower[valid_lower]]
    upper = lower + 1
    valid_upper = (upper >= 0) & (upper < size)
    output[valid_upper] += np.square(fraction[valid_upper]) * variance[upper[valid_upper]]
    return output


def _replica_operator(values: np.ndarray, offset: float, blur_sigma: float) -> np.ndarray:
    blurred = _blur_signal(values, blur_sigma)
    return _sample_to_left(blurred, offset)


def _replica_variance_operator(
    variance: np.ndarray,
    offset: float,
    blur_sigma: float,
) -> np.ndarray:
    blurred = _blur_variance(variance, blur_sigma)
    return _sample_variance_to_left(blurred, offset)


def _blur_signal(values: np.ndarray, sigma: float) -> np.ndarray:
    if sigma == 0:
        return values.copy()
    kernel = _gaussian_kernel(sigma)
    return np.convolve(values, kernel, mode="same")


def _blur_variance(variance: np.ndarray, sigma: float) -> np.ndarray:
    if sigma == 0:
        return variance.copy()
    kernel = _gaussian_kernel(sigma)
    return np.convolve(variance, np.square(kernel), mode="same")


def _gaussian_kernel(sigma: float) -> np.ndarray:
    radius = max(1, int(np.ceil(4.0 * sigma)))
    coordinate = np.arange(-radius, radius + 1, dtype=float)
    kernel = np.exp(-0.5 * np.square(coordinate / sigma))
    return kernel / np.sum(kernel)


def _one_dimensional_finite_array(values: np.ndarray, name: str) -> np.ndarray:
    array = np.asarray(values, dtype=float)
    if array.ndim != 1:
        raise ValueError(f"{name} must be one-dimensional.")
    if np.any(~np.isfinite(array)):
        raise ValueError(f"{name} must contain only finite values.")
    return array


def _validate_kernel(
    offset_pixels: float,
    secondary_to_primary: float,
    secondary_blur_sigma_pixels: float,
) -> tuple[float, float, float]:
    offset = float(offset_pixels)
    ratio = float(secondary_to_primary)
    blur = float(secondary_blur_sigma_pixels)
    if not np.isfinite(offset) or offset <= 0:
        raise ValueError("offset_pixels must be finite and greater than zero.")
    if not np.isfinite(ratio) or ratio < 0 or ratio >= 1:
        raise ValueError("secondary_to_primary must be finite and in [0, 1).")
    if not np.isfinite(blur) or blur < 0:
        raise ValueError("secondary_blur_sigma_pixels must be finite and non-negative.")
    return offset, ratio, blur
