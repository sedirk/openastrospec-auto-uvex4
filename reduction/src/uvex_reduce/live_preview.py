"""Image-only NINA quick-look adapter; no acquisition, calibration discovery or writes.

Uses the same extraction and cosmic-ray code as the offline pipeline. Raw detector
orientation is retained; output is ADU per aperture, NOT a calibrated spectrum.
"""
from __future__ import annotations

from contextlib import redirect_stdout
import json
import sys

from astropy.io import fits
import numpy as np

from .config import DetectorConfig, ExtractionConfig
from .extraction import _boxcar_extract, extract_spectrum
from .preprocess import _clean_cosmic_rays


def extract_preview(
    pixels: np.ndarray,
    saturation: float,
    trace_center: float,
    trace_half_width: float,
    *,
    backend: str = "aspired",
) -> dict:
    image = np.array(pixels, dtype=np.float32, copy=True)
    if image.ndim != 2 or min(image.shape) < 32 or image.size > 32_000_000:
        raise ValueError("Unsupported preview dimensions")
    if not np.isfinite(saturation) or saturation <= 0:
        raise ValueError("Invalid saturation level")
    if not np.isfinite(trace_center) or not 0 <= trace_center < image.shape[0]:
        raise ValueError("No measured trace in this ROI")
    if not np.isfinite(trace_half_width) or not 2 <= trace_half_width <= 128:
        raise ValueError("No usable extraction aperture")

    # Bounded spatial strip, but all wavelength pixels. Nothing is resampled or
    # spectrally smoothed. The measured center is only a search hint, not a trace.
    half_width = int(np.ceil(trace_half_width))
    margin = max(80, half_width + 60)
    start = max(0, int(trace_center) - margin)
    stop = min(image.shape[0], int(trace_center) + margin + 1)
    image = image[start:stop].copy()
    mask = ~np.isfinite(image) | (image >= saturation * 0.999)
    # A display-only noise model, deliberately not advertised as measured e-/ADU.
    detector = DetectorConfig(saturation_adu=saturation)
    column_background = np.nanmedian(np.where(mask, np.nan, image), axis=0)
    noise = 1.4826 * np.nanmedian(
        np.abs(np.where(mask, np.nan, image) - column_background), axis=0
    )
    noise = np.maximum(np.nan_to_num(noise, nan=6.5), 1.0)
    variance = np.maximum(image - column_background, 0) + noise**2
    safe_image = np.where(np.isfinite(image), image, column_background)
    safe_image = np.nan_to_num(safe_image, nan=0.0)
    # Camera Gain=100 is a driver setting, NOT 100 e-/ADU. In a quick-look
    # without a commissioned detector noise model use measured local sky
    # variance, not the offline default 6.5e- to classify ordinary noise as CRs.
    cleaned, cosmic_count = _clean_cosmic_rays(safe_image, mask, detector, variance=variance)
    if cosmic_count > image.size * 0.02:
        raise ValueError("More than 2% of the preview strip flagged as outliers; cleaning is not trustworthy")
    options = ExtractionConfig(
        backend=backend, aperture_half_width=half_width,
        trace_half_width=min(40, margin // 2), sky_separation=8, sky_width=14,
        allow_low_confidence_trace=False, optimal=False,
    )
    # With only an estimated noise model, the existing Gaussian optimal profile
    # can change narrow-line contrast. Use the offline engine's flux-summing
    # aperture extraction; real narrow lines must not be traded for a smoother plot.
    product = extract_spectrum(cleaned, variance, mask, fits.Header(), detector, options)
    centers = product.trace.centers
    # Do not silently switch to a neighbouring trace far from the current frame's hint.
    if np.median(np.abs(centers - (trace_center - start))) > max(12, half_width * 2):
        raise ValueError("Reduction trace disagrees with the measured preview trace")
    raw_flux, _, raw_mask = _boxcar_extract(image, variance, mask, centers, options)
    # Saturation is missing information, not something cleaning can reconstruct.
    clipped = np.zeros(image.shape[1], dtype=bool)
    for x, center in enumerate(centers):
        y = int(round(center))
        low, high = max(0, y - half_width), min(image.shape[0], y + half_width + 1)
        clipped[x] = np.any(mask[low:high, x]) or low == 0 or high == image.shape[0]
    bad = product.mask | clipped | ~np.isfinite(product.flux)
    raw_bad = raw_mask | clipped | ~np.isfinite(raw_flux)
    if np.mean(~bad) < 0.5:
        raise ValueError("Fewer than half of the spectral columns are valid")

    def nullable(values: np.ndarray, invalid: np.ndarray) -> list:
        return [None if invalid[i] else float(value) for i, value in enumerate(values)]

    return {
        "schema": 1, "algorithm": "reduction-live-preview-v1", "previewOnly": True,
        "backend": product.backend, "flux": nullable(product.flux, bad),
        "rawFlux": nullable(raw_flux, raw_bad),
        "uncertainty": nullable(product.uncertainty, bad),
        "cosmicPixels": cosmic_count, "maskedColumns": int(bad.sum()),
        "traceMethod": product.trace.method,
        "warnings": product.warnings,
        "calibrated": False, "spectralSmoothing": False,
    }


def main() -> None:
    stream = sys.stdin.buffer
    line = stream.readline(4097)
    if len(line) > 4096:
        raise ValueError("Oversized request")
    request = json.loads(line)
    width, height = int(request["width"]), int(request["height"])
    if request["schema"] != 1 or min(width, height) < 32 or width * height > 32_000_000:
        raise ValueError("Invalid image contract")
    data = stream.read(width * height * 2 + 1)
    if len(data) != width * height * 2:
        raise ValueError("Image payload size mismatch")
    image = np.frombuffer(data, dtype="<u2").reshape(height, width)
    # Third-party diagnostics must never corrupt the one-response JSON channel.
    with redirect_stdout(sys.stderr):
        result = extract_preview(image, request["saturation"], request["traceCenter"], request["traceHalfWidth"])
    sys.stdout.write(json.dumps(result, allow_nan=False))


if __name__ == "__main__":
    main()
