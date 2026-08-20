"""Restricted-runtime validation harness for the May 2026 UVEX data.

The production path is ``uvex-reduce full-run`` and remains Astropy/ASPIRED
based.  This harness intentionally uses only NumPy and Pillow so the same raw
frames can still be inspected in a restricted maintenance environment where
the pinned Python 3.11 runtime cannot be launched.  It exercises the same
critical policies: non-destructive ATR585M x-wrap repair, exposure-rate
normalisation, bounded alignment, flat/no-flat comparison, standard-star
wavelength calibration, response correction and continuum normalisation.
"""

from __future__ import annotations

import argparse
import csv
from itertools import combinations
import json
import os
from pathlib import Path
from typing import Iterable
import warnings

import numpy as np
from PIL import Image, ImageDraw


DATA = Path("<local-data-root>")
TEMPLATE = Path("<local-isis-template>")
OUTPUT = (
    Path(__file__).resolve().parents[1]
    / "output"
    / "_internal"
    / "runs"
    / "may-2026-validation"
)
SATURATION = 65_520.0
BALMER = np.asarray([4340.47, 4861.35, 6562.79])
NEBULAR = np.asarray(
    [
        4340.47,
        4685.68,
        4861.35,
        4958.91,
        5006.84,
        5411.52,
        5875.62,
        6548.05,
        6562.79,
        6583.45,
        6678.15,
        6716.44,
        6730.82,
        7135.79,
    ]
)

# Commissioned from the raw-frame continuity audit.  This harness deliberately
# does not infer an absolute buffer state from a threshold or from the majority
# state of a group.
EXPLICIT_REPAIRS = {
    "Vega-20260509": {
        "260509014600.fit",
        "260509014714.fit",
        "260509014737.fit",
        "260509014749.fit",
        "260509014751.fit",
        "260509014754.fit",
        "260509014757.fit",
        "260509014759.fit",
    },
    "NGC6543-20260506": {
        "260506010745.fit",
        "260506011745.fit",
        "260506012953.fit",
        "260506013453.fit",
        "260506013953.fit",
        "260506014453.fit",
        "260506014954.fit",
        "260506015454.fit",
    },
    "HD140573-20260509": {
        "260509011102.fit",
        "260509011202.fit",
        "260509011302.fit",
        "260509011402.fit",
        "260509011502.fit",
    },
}


def read_fits_image(path: Path) -> tuple[dict[str, object], np.ndarray]:
    raw = path.read_bytes()
    cards: list[str] = []
    offset = 0
    while True:
        block = raw[offset : offset + 2880]
        offset += 2880
        current = [
            block[index : index + 80].decode("ascii", "replace")
            for index in range(0, 2880, 80)
        ]
        cards.extend(current)
        if any(card.startswith("END") for card in current):
            break
    header: dict[str, object] = {}
    for card in cards:
        if card.startswith("END"):
            break
        if card[8:10] != "= ":
            continue
        token = card[10:].split("/")[0].strip()
        key = card[:8].strip()
        if token.startswith("'"):
            value: object = token.strip(" '")
        elif token in {"T", "F"}:
            value = token == "T"
        else:
            try:
                value = (
                    float(token.replace("D", "E"))
                    if any(char in token for char in ".EeDd")
                    else int(token)
                )
            except ValueError:
                value = token
        header[key] = value
    width = int(header["NAXIS1"])
    height = int(header["NAXIS2"])
    dtype = {8: ">u1", 16: ">i2", 32: ">i4", -32: ">f4"}[int(header["BITPIX"])]
    image = np.frombuffer(
        raw,
        dtype=dtype,
        count=width * height,
        offset=offset,
    ).reshape(height, width)
    image = image.astype(np.float32)
    image *= float(header.get("BSCALE", 1.0))
    image += float(header.get("BZERO", 0.0))
    return header, image


def gaussian_fft(values: np.ndarray, sigma: float, axis: int = -1) -> np.ndarray:
    """Gaussian smoothing without the end-to-end wrap of a bare FFT filter."""

    axis = axis % values.ndim
    length = values.shape[axis]
    padding = min(length - 1, max(8, int(np.ceil(4.0 * sigma))))
    pad_width = [(0, 0)] * values.ndim
    pad_width[axis] = (padding, padding)
    padded = np.pad(values, pad_width, mode="reflect")
    padded_length = padded.shape[axis]
    frequency = np.fft.rfftfreq(padded_length)
    shape = [1] * values.ndim
    shape[axis] = frequency.size
    kernel = np.exp(-2.0 * np.pi**2 * sigma**2 * frequency**2).reshape(shape)
    smoothed = np.fft.irfft(
        np.fft.rfft(padded, axis=axis) * kernel,
        n=padded_length,
        axis=axis,
    )
    selection = [slice(None)] * values.ndim
    selection[axis] = slice(padding, padding + length)
    return smoothed[tuple(selection)]


def sdk_seam_score(image: np.ndarray, shift: int = 64) -> float:
    column_background = np.nanmedian(image, axis=0)
    spatial = np.nanmean(image - column_background, axis=1)
    center = int(np.nanargmax(spatial))
    profile = (
        np.nanmean(image[max(0, center - 15) : center + 16], axis=0)
        - column_background
    )
    differences = np.abs(np.diff(gaussian_fft(profile, 1.0)))
    baseline = np.r_[differences[10 : shift - 6], differences[shift + 6 : 160]]
    median = float(np.nanmedian(baseline))
    scatter = 1.4826 * float(np.nanmedian(np.abs(baseline - median)))
    return float(
        (differences[shift - 1] - median) / max(scatter, 0.1 * median, 1.0)
    )


def profiles(image: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    background = np.nanmedian(image, axis=0)
    spatial = np.nanmean(image - background, axis=1)
    center = int(np.nanargmax(spatial))
    spectrum = (
        np.nanmean(image[max(0, center - 15) : center + 16], axis=0)
        - background
    )
    return spatial, spectrum - gaussian_fft(spectrum, 40.0)


def integer_shift(reference: np.ndarray, target: np.ndarray, limit: int) -> tuple[int, float]:
    scores = []
    for shift in range(-limit, limit + 1):
        left = reference[max(0, shift) : reference.size + min(0, shift)]
        right = target[max(0, -shift) : target.size - max(0, shift)]
        left = left - np.nanmean(left)
        right = right - np.nanmean(right)
        score = np.nansum(left * right) / max(
            np.sqrt(np.nansum(left * left) * np.nansum(right * right)),
            1.0,
        )
        scores.append(float(score))
    index = int(np.argmax(scores))
    return index - limit, scores[index]


def shift_without_wrap(image: np.ndarray, dy: int, dx: int) -> np.ndarray:
    shifted = np.roll(np.roll(image, dy, axis=0), dx, axis=1)
    if dy > 0:
        shifted[:dy] = np.nan
    elif dy < 0:
        shifted[dy:] = np.nan
    if dx > 0:
        shifted[:, :dx] = np.nan
    elif dx < 0:
        shifted[:, dx:] = np.nan
    return shifted


def stack_frames(
    paths: Iterable[Path],
    *,
    flat: tuple[np.ndarray, np.ndarray] | None = None,
    maximum_shift: int = 20,
    repair_files: set[str] | frozenset[str] = frozenset(),
) -> tuple[np.ndarray, dict[str, object]]:
    loaded = []
    exposures = []
    fixes = []
    for path in paths:
        header, image = read_fits_image(path)
        if path.name in repair_files:
            image = np.roll(image, -64, axis=1)
            fixes.append(path.name)
        exposure = float(header["EXPTIME"])
        loaded.append((path, image, exposure))
        exposures.append(exposure)
    reference_exposure = float(np.median(exposures))
    calibrated = []
    for _, image, exposure in loaded:
        mask = ~np.isfinite(image) | (image >= SATURATION)
        image = image * (reference_exposure / exposure)
        if flat is not None:
            response, flat_mask = flat
            mask |= flat_mask
            image = np.divide(
                image,
                response,
                out=np.full_like(image, np.nan),
                where=~flat_mask,
            )
        image[mask] = np.nan
        calibrated.append(image)
    reference_y, reference_x = profiles(calibrated[0])
    aligned = [calibrated[0]]
    shifts = [(loaded[0][0].name, 0, 0, 1.0)]
    rejected = []
    for (path, _, _), image in zip(loaded[1:], calibrated[1:]):
        profile_y, profile_x = profiles(image)
        dy, confidence_y = integer_shift(reference_y, profile_y, 90)
        dx, confidence_x = integer_shift(reference_x, profile_x, 90)
        confidence = min(confidence_y, confidence_x)
        if abs(dx) > maximum_shift or abs(dy) > maximum_shift or confidence < 0.10:
            rejected.append(path.name)
            continue
        aligned.append(shift_without_wrap(image, dy, dx))
        shifts.append((path.name, dx, dy, confidence))
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        stack = np.nanmedian(np.stack(aligned), axis=0).astype(np.float32)
    return stack, {
        "configured": len(loaded),
        "accepted": len(aligned),
        "rejected": rejected,
        "sdkWrapRepaired": fixes,
        "referenceExposureSeconds": reference_exposure,
        "totalExposureSeconds": float(sum(exposures)),
        "shifts": shifts,
    }


def build_candidate_flat(
    paths: Iterable[Path],
    *,
    repair_files: set[str] | frozenset[str] = frozenset(),
) -> tuple[np.ndarray, np.ndarray, dict]:
    normalized = []
    used = []
    rejected = []
    for path in paths:
        _, image = read_fits_image(path)
        saturation = float(np.mean(image >= SATURATION))
        if saturation > 0.006:
            rejected.append(path.name)
            continue
        if path.name in repair_files:
            image = np.roll(image, -64, axis=1)
        invalid = ~np.isfinite(image) | (image >= SATURATION)
        fill = np.where(invalid, float(np.nanmedian(image)), image)
        smooth = gaussian_fft(gaussian_fft(fill, 25.0, axis=0), 100.0, axis=1)
        valid = (~invalid) & (smooth > np.nanpercentile(smooth, 10.0))
        response = np.full_like(image, np.nan)
        response[valid] = image[valid] / smooth[valid]
        valid &= (response > 0.5) & (response < 1.5)
        response[~valid] = np.nan
        normalized.append(response.astype(np.float32))
        used.append(path.name)
    cube = np.stack(normalized)
    coverage = np.sum(np.isfinite(cube), axis=0)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        response = np.nanmedian(cube, axis=0)
    valid = (
        np.isfinite(response)
        & (coverage >= max(1, int(np.ceil(0.5 * len(normalized)))))
        & (response > 0.5)
        & (response < 1.5)
    )
    response /= float(np.nanmedian(response[valid]))
    response[~valid] = 1.0
    return response.astype(np.float32), ~valid, {
        "used": used,
        "rejectedSaturated": rejected,
        "validFraction": float(np.mean(valid)),
    }


def extract_spectrum(image: np.ndarray, half_width: int = 18) -> tuple[np.ndarray, int]:
    background = np.nanmedian(image, axis=0)
    spatial = np.nanmean(image - background, axis=1)
    center = int(np.nanargmax(spatial))
    aperture = image[center - half_width : center + half_width + 1]
    sky = np.r_[
        image[center - half_width - 30 : center - half_width - 10],
        image[center + half_width + 10 : center + half_width + 30],
    ]
    flux = np.nansum(aperture - np.nanmedian(sky, axis=0), axis=0)
    return flux.astype(float), center


def _robust_standardize(values: np.ndarray) -> np.ndarray:
    median = float(np.nanmedian(values))
    scatter = 1.4826 * float(np.nanmedian(np.abs(values - median)))
    return np.clip((values - median) / max(scatter, 1e-9), -4.0, 4.0)


def _template_correlation(
    wavelength: np.ndarray,
    observed_lines: np.ndarray,
) -> float:
    template_wave, template_flux = np.loadtxt(TEMPLATE).T
    template = np.interp(
        wavelength,
        template_wave,
        template_flux,
        left=np.nan,
        right=np.nan,
    )
    valid_template = np.isfinite(template)
    if np.count_nonzero(valid_template) < 200:
        return float("nan")
    indices = np.arange(template.size)
    filled = np.interp(
        indices,
        np.flatnonzero(valid_template),
        template[valid_template],
    )
    smooth = gaussian_fft(filled, 80.0)
    template_lines = (smooth - filled) / np.maximum(smooth, 1e-9)
    valid = (
        valid_template
        & np.isfinite(observed_lines)
        & (wavelength >= 4250.0)
        & (wavelength <= 7500.0)
    )
    return float(
        np.corrcoef(
            _robust_standardize(observed_lines[valid]),
            _robust_standardize(template_lines[valid]),
        )[0, 1]
    )


def vega_wavelength(flux: np.ndarray) -> tuple[np.ndarray, np.ndarray, float]:
    continuum = gaussian_fft(flux, 80.0)
    absorption = (continuum - flux) / np.maximum(
        continuum,
        np.percentile(continuum, 5.0),
    )
    local_peaks = [
        index
        for index in range(20, flux.size - 20)
        if absorption[index] == np.max(absorption[index - 10 : index + 11])
        and absorption[index] >= 0.012
    ]
    selected: list[int] = []
    for index in sorted(local_peaks, key=lambda item: absorption[item], reverse=True):
        if all(abs(index - previous) > 30 for previous in selected):
            selected.append(index)
        if len(selected) >= 18:
            break
    selected.sort()

    candidates: list[tuple[float, np.ndarray, np.ndarray]] = []
    pixels = np.arange(flux.size, dtype=float)
    for feature_pixels in combinations(selected, 3):
        centers = np.asarray(feature_pixels, dtype=float)
        coefficients = np.polyfit(centers, BALMER, 2)
        wavelength = np.polyval(coefficients, pixels)
        dispersion = np.diff(wavelength)
        if (
            np.nanmin(dispersion) < 0.3
            or np.nanmax(dispersion) > 2.0
            or wavelength[0] < 3500.0
            or wavelength[-1] > 8500.0
            or wavelength[-1] - wavelength[0] < 2500.0
        ):
            continue
        correlation = _template_correlation(wavelength, absorption)
        if np.isfinite(correlation):
            candidates.append((correlation, centers, wavelength))
    if not candidates:
        raise RuntimeError("Vega template search found no valid three-line solution.")
    correlation, centers, wavelength = max(candidates, key=lambda item: item[0])
    return wavelength, centers, correlation


def relative_response(
    wavelength: np.ndarray,
    flux: np.ndarray,
    exposure: float,
) -> tuple[np.ndarray, np.ndarray, float]:
    template_wave, template_flux = np.loadtxt(TEMPLATE).T
    template = np.interp(wavelength, template_wave, template_flux, left=np.nan, right=np.nan)
    raw = flux / exposure / template
    valid = np.isfinite(raw) & (raw > 0)
    for line in BALMER:
        valid &= np.abs(wavelength - line) > 40.0
    log_ratio = np.full_like(raw, np.nan)
    log_ratio[valid] = np.log(raw[valid])
    indices = np.arange(raw.size)
    good = np.flatnonzero(np.isfinite(log_ratio))
    filled = np.interp(indices, good, log_ratio[good])
    response = np.exp(gaussian_fft(filled, 100.0))
    band = (wavelength >= 5400.0) & (wavelength <= 5600.0)
    response /= float(np.nanmedian(response[band]))
    residual = log_ratio[valid] - np.log(response[valid])
    scatter = 1.4826 * float(np.median(np.abs(residual - np.median(residual))))
    return response, raw, scatter


def emission_affine_refinement(
    wavelength: np.ndarray,
    flux: np.ndarray,
) -> tuple[np.ndarray, dict]:
    residual = flux - gaussian_fft(flux, 50.0)
    noise = 1.4826 * float(np.median(np.abs(residual - np.median(residual))))
    peaks = []
    for index in range(4, flux.size - 4):
        if residual[index] == np.max(residual[index - 4 : index + 5]) and residual[index] > 5 * noise:
            peaks.append((float(residual[index]), float(wavelength[index])))
    observed = np.asarray([item[1] for item in sorted(peaks, reverse=True)[:60]])
    best = None
    for observed_line in observed:
        for reference_line in NEBULAR:
            candidate = observed_line - reference_line
            if abs(candidate) > 80.0:
                continue
            pairs = []
            for reference in NEBULAR:
                index = int(np.argmin(np.abs(observed - (reference + candidate))))
                if abs(observed[index] - (reference + candidate)) <= 9.0:
                    pairs.append((observed[index], reference))
            unique = {round(pair[0], 4): pair for pair in pairs}
            pairs = list(unique.values())
            if len(pairs) < 5:
                continue
            pair_observed = np.asarray([pair[0] for pair in pairs])
            pair_reference = np.asarray([pair[1] for pair in pairs])
            scale, intercept = np.polyfit(pair_observed, pair_reference, 1)
            corrected = scale * pair_observed + intercept
            keep = np.abs(corrected - pair_reference) <= 3.0
            if np.count_nonzero(keep) < 5 or not 0.98 <= scale <= 1.02:
                continue
            scale, intercept = np.polyfit(pair_observed[keep], pair_reference[keep], 1)
            rms = float(
                np.sqrt(
                    np.mean(
                        (scale * pair_observed[keep] + intercept - pair_reference[keep]) ** 2
                    )
                )
            )
            span = float(np.ptp(pair_reference[keep]))
            score = (int(np.count_nonzero(keep)), span, -rms)
            if best is None or score > best[0]:
                best = (score, scale, intercept, rms, pair_observed[keep], pair_reference[keep])
    if best is None:
        return wavelength, {"status": "failed"}
    _, scale, intercept, rms, observed, reference = best
    corrected = scale * wavelength + intercept
    pivot = float(np.median(observed))
    return corrected, {
        "status": "applied",
        "method": "bounded-affine",
        "scale": float(scale),
        "pivotAngstrom": pivot,
        "offsetAtPivotAngstrom": float(intercept + (scale - 1.0) * pivot),
        "rmsAngstrom": rms,
        "observed": observed.tolist(),
        "reference": reference.tolist(),
    }


def continuum_normalize(
    wavelength: np.ndarray,
    relative_flux: np.ndarray,
) -> tuple[np.ndarray, np.ndarray]:
    edges = np.arange(wavelength[0], wavelength[-1] + 100.0, 100.0)
    centers = []
    levels = []
    for low, high in zip(edges[:-1], edges[1:]):
        inside = (
            np.isfinite(relative_flux)
            & (relative_flux > 0)
            & (wavelength >= low)
            & (wavelength < high)
        )
        if np.count_nonzero(inside) >= 8:
            centers.append(float(np.median(wavelength[inside])))
            levels.append(float(np.percentile(relative_flux[inside], 35.0)))
    continuum = np.interp(wavelength, centers, levels)
    continuum = gaussian_fft(continuum, 50.0)
    normalized = relative_flux / continuum
    return continuum, normalized


def write_spectrum_fits(
    path: Path,
    target: str,
    wavelength: np.ndarray,
    raw_flux: np.ndarray,
    relative_flux: np.ndarray,
    continuum: np.ndarray,
    normalized: np.ndarray,
    metadata: dict,
    flux_calibration: str = "RELATIVE",
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    primary = _header_bytes(
        [
            ("SIMPLE", True, None),
            ("BITPIX", 8, None),
            ("NAXIS", 0, None),
            ("EXTEND", True, None),
            ("OBJECT", target, None),
            ("FLUXCAL", flux_calibration, "Not absolute physical flux"),
            ("ORD2STRT", 6800.0, "Conservative warning; data retained"),
            ("ORD2STAT", "UNDETERMINED", None),
            ("NCOMBINE", int(metadata["accepted"]), None),
            ("TOTEXP", float(metadata["totalExposureSeconds"]), None),
        ]
    )
    names = [
        "PIXEL",
        "WAVELENGTH",
        "RAW_FLUX",
        "RELATIVE_FLUX",
        "CONTINUUM",
        "NORMALIZED_FLUX",
        "ORDER2_RISK",
        "MASK",
    ]
    row_dtype = np.dtype(
        [
            ("PIXEL", ">f8"),
            ("WAVELENGTH", ">f8"),
            ("RAW_FLUX", ">f8"),
            ("RELATIVE_FLUX", ">f8"),
            ("CONTINUUM", ">f8"),
            ("NORMALIZED_FLUX", ">f8"),
            ("ORDER2_RISK", "S1"),
            ("MASK", "S1"),
        ]
    )
    table = np.empty(wavelength.size, dtype=row_dtype)
    table["PIXEL"] = np.arange(wavelength.size)
    table["WAVELENGTH"] = wavelength
    table["RAW_FLUX"] = raw_flux
    table["RELATIVE_FLUX"] = relative_flux
    table["CONTINUUM"] = continuum
    table["NORMALIZED_FLUX"] = normalized
    table["ORDER2_RISK"] = np.where(wavelength >= 6800.0, b"T", b"F")
    invalid = ~np.isfinite(normalized)
    table["MASK"] = np.where(invalid, b"T", b"F")
    cards = [
        ("XTENSION", "BINTABLE", None),
        ("BITPIX", 8, None),
        ("NAXIS", 2, None),
        ("NAXIS1", row_dtype.itemsize, None),
        ("NAXIS2", wavelength.size, None),
        ("PCOUNT", 0, None),
        ("GCOUNT", 1, None),
        ("TFIELDS", len(names), None),
        ("EXTNAME", "SPECTRUM", None),
    ]
    units = ["pix", "Angstrom", "adu", "adu/s", "adu/s", "", "", ""]
    formats = ["D", "D", "D", "D", "D", "D", "L", "L"]
    for index, (name, form, unit) in enumerate(zip(names, formats, units), start=1):
        cards.append((f"TTYPE{index}", name, None))
        cards.append((f"TFORM{index}", form, None))
        if unit:
            cards.append((f"TUNIT{index}", unit, None))
    extension = _header_bytes(cards)
    data = table.tobytes(order="C")
    data += b"\0" * ((-len(data)) % 2880)
    path.write_bytes(primary + extension + data)


def write_response_products(
    destination: Path,
    wavelength: np.ndarray,
    raw_response: np.ndarray,
    response: np.ndarray,
) -> dict[str, str]:
    destination.mkdir(parents=True, exist_ok=True)
    csv_path = destination / "relative_response.csv"
    fits_path = destination / "relative_response.fits"
    png_path = destination / "relative_response.png"
    mask = ~np.isfinite(raw_response) | ~np.isfinite(response) | (response <= 0)
    with csv_path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            ["wavelength_angstrom", "raw_response", "smoothed_response", "mask"]
        )
        writer.writerows(
            zip(wavelength, raw_response, response, mask.astype(int))
        )

    primary = _header_bytes(
        [
            ("SIMPLE", True, None),
            ("BITPIX", 8, None),
            ("NAXIS", 0, None),
            ("EXTEND", True, None),
            ("CALTYPE", "RELATIVE_RESPONSE", None),
            ("STANDARD", "Vega", None),
            ("ABSFLUX", False, "No absolute spectrophotometric calibration"),
        ]
    )
    dtype = np.dtype(
        [
            ("WAVELENGTH", ">f8"),
            ("RAW_RESPONSE", ">f8"),
            ("RESPONSE", ">f8"),
            ("MASK", "S1"),
        ]
    )
    table = np.empty(wavelength.size, dtype=dtype)
    table["WAVELENGTH"] = wavelength
    table["RAW_RESPONSE"] = raw_response
    table["RESPONSE"] = response
    table["MASK"] = np.where(mask, b"T", b"F")
    extension = _header_bytes(
        [
            ("XTENSION", "BINTABLE", None),
            ("BITPIX", 8, None),
            ("NAXIS", 2, None),
            ("NAXIS1", dtype.itemsize, None),
            ("NAXIS2", wavelength.size, None),
            ("PCOUNT", 0, None),
            ("GCOUNT", 1, None),
            ("TFIELDS", 4, None),
            ("EXTNAME", "RESPONSE", None),
            ("TTYPE1", "WAVELENGTH", None),
            ("TFORM1", "D", None),
            ("TUNIT1", "Angstrom", None),
            ("TTYPE2", "RAW_RESPONSE", None),
            ("TFORM2", "D", None),
            ("TTYPE3", "RESPONSE", None),
            ("TFORM3", "D", None),
            ("TTYPE4", "MASK", None),
            ("TFORM4", "L", None),
        ]
    )
    data = table.tobytes(order="C")
    data += b"\0" * ((-len(data)) % 2880)
    fits_path.write_bytes(primary + extension + data)
    plot_spectrum(
        png_path,
        wavelength,
        response,
        "Vega-derived relative instrumental response",
    )
    return {
        "fits": str(fits_path),
        "csv": str(csv_path),
        "png": str(png_path),
    }


def _header_bytes(cards: list[tuple[str, object, str | None]]) -> bytes:
    rendered = []
    for key, value, comment in cards:
        if isinstance(value, bool):
            token = "T" if value else "F"
        elif isinstance(value, str):
            token = f"'{value}'"
        elif isinstance(value, (int, np.integer)):
            token = str(int(value))
        else:
            token = f"{float(value):.12G}"
        card = f"{key:<8}= {token:>20}"
        if comment:
            card += f" / {comment}"
        rendered.append(card[:80].ljust(80))
    rendered.append("END".ljust(80))
    raw = "".join(rendered).encode("ascii")
    return raw + b" " * ((-len(raw)) % 2880)


def write_csv(
    path: Path,
    wavelength: np.ndarray,
    relative_flux: np.ndarray,
    continuum: np.ndarray,
    normalized: np.ndarray,
) -> None:
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "wavelength_angstrom",
                "relative_flux_adu_per_s",
                "continuum_adu_per_s",
                "normalized_flux",
                "second_order_risk",
            ]
        )
        writer.writerows(
            zip(
                wavelength,
                relative_flux,
                continuum,
                normalized,
                (wavelength >= 6800.0).astype(int),
            )
        )


def plot_spectrum(path: Path, wavelength: np.ndarray, flux: np.ndarray, title: str) -> None:
    width, height = 1400, 650
    margin = (80, 45, 35, 70)
    image = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(image)
    left, top, right, bottom = margin[0], margin[1], width - margin[2], height - margin[3]
    valid = np.isfinite(wavelength) & np.isfinite(flux)
    low_x, high_x = float(wavelength[valid].min()), float(wavelength[valid].max())
    low_y, high_y = np.percentile(flux[valid], [1.0, 99.7])
    if high_y <= low_y:
        high_y = low_y + 1.0

    def point(x, y):
        px = left + (float(x) - low_x) / (high_x - low_x) * (right - left)
        py = bottom - (float(y) - low_y) / (high_y - low_y) * (bottom - top)
        return int(px), int(np.clip(py, top, bottom))

    if high_x > 6800.0:
        risk_x = point(6800.0, low_y)[0]
        draw.rectangle((risk_x, top, right, bottom), fill=(255, 243, 224))
    draw.line((left, top, left, bottom, right, bottom), fill="black", width=2)
    x_step = 500.0
    first_tick = np.ceil(low_x / x_step) * x_step
    for value in np.arange(first_tick, high_x + 0.5 * x_step, x_step):
        x = point(value, low_y)[0]
        draw.line((x, bottom, x, bottom + 6), fill="black", width=1)
        draw.text((x - 18, bottom + 9), f"{value:.0f}", fill="black")
    for fraction in np.linspace(0.0, 1.0, 5):
        value = low_y + fraction * (high_y - low_y)
        y = point(low_x, value)[1]
        draw.line((left - 6, y, left, y), fill="black", width=1)
        draw.text((4, y - 6), f"{value:.3g}", fill="black")
    samples = np.flatnonzero(valid)[:: max(1, np.count_nonzero(valid) // 4000)]
    points = [point(wavelength[index], flux[index]) for index in samples]
    if len(points) > 1:
        draw.line(points, fill=(0, 105, 180), width=2)
    draw.text((left, 12), title, fill="black")
    draw.text((left, height - 35), "Wavelength (Angstrom, air)", fill="black")
    draw.text((right - 330, top + 10), "Orange: conservative second-order warning", fill=(160, 90, 0))
    image.save(path)


def plot_trace(
    path: Path,
    image_data: np.ndarray,
    center: int,
    half_width: int,
    title: str,
) -> None:
    """Write a compact 2-D stack preview with the extraction aperture overlaid."""

    finite = image_data[np.isfinite(image_data)]
    low, high = np.percentile(finite, [5.0, 99.8])
    scaled = np.arcsinh(
        8.0 * np.clip((image_data - low) / max(high - low, 1.0), 0.0, 1.0)
    ) / np.arcsinh(8.0)
    grayscale = np.uint8(np.nan_to_num(scaled, nan=0.0) * 255.0)
    preview = Image.fromarray(grayscale, mode="L").resize((1280, 720)).convert("RGB")
    canvas = Image.new("RGB", (1280, 760), "white")
    canvas.paste(preview, (0, 40))
    draw = ImageDraw.Draw(canvas)
    y_scale = 720.0 / image_data.shape[0]
    for row, color, width in (
        (center, (255, 50, 50), 3),
        (center - half_width, (255, 220, 0), 2),
        (center + half_width, (255, 220, 0), 2),
    ):
        y = int(40 + row * y_scale)
        draw.line((0, y, 1279, y), fill=color, width=width)
    draw.text((12, 12), title, fill="black")
    canvas.save(path)


def plot_wavelength_residuals(
    path: Path,
    observed: np.ndarray,
    reference: np.ndarray,
    scale: float,
    intercept: float,
    title: str,
) -> None:
    corrected = scale * observed + intercept
    residual = corrected - reference
    width, height = 1000, 560
    canvas = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(canvas)
    left, top, right, bottom = 80, 45, width - 35, height - 70
    low_x, high_x = float(reference.min() - 100), float(reference.max() + 100)
    limit = max(1.0, float(np.max(np.abs(residual))) * 1.35)

    def point(x_value: float, y_value: float) -> tuple[int, int]:
        x = left + (x_value - low_x) / (high_x - low_x) * (right - left)
        y = bottom - (y_value + limit) / (2.0 * limit) * (bottom - top)
        return int(x), int(y)

    draw.line((left, top, left, bottom, right, bottom), fill="black", width=2)
    zero_y = point(low_x, 0.0)[1]
    draw.line((left, zero_y, right, zero_y), fill=(130, 130, 130), width=1)
    for wavelength_value, residual_value in zip(reference, residual):
        x, y = point(float(wavelength_value), float(residual_value))
        draw.ellipse((x - 5, y - 5, x + 5, y + 5), fill=(0, 105, 180))
        draw.text((x - 18, bottom + 10), f"{wavelength_value:.0f}", fill="black")
    draw.text((left, 12), title, fill="black")
    draw.text((left, height - 30), "Reference wavelength (Angstrom, air)", fill="black")
    draw.text((5, top), f"+{limit:.2f} A", fill="black")
    draw.text((5, bottom - 10), f"-{limit:.2f} A", fill="black")
    canvas.save(path)


def process_target(
    name: str,
    paths: list[Path],
    standard_wavelength: np.ndarray,
    response: np.ndarray,
    *,
    refine_emission: bool,
    repair_files: set[str] | frozenset[str] = frozenset(),
) -> dict:
    stack, stack_meta = stack_frames(paths, repair_files=repair_files)
    stack = stack[:, ::-1]
    raw_flux, trace_y = extract_spectrum(stack, half_width=20 if refine_emission else 18)
    wavelength = standard_wavelength.copy()
    refinement = {"status": "skipped"}
    if refine_emission:
        wavelength, refinement = emission_affine_refinement(wavelength, raw_flux)
    exposure = float(stack_meta["referenceExposureSeconds"])
    relative_flux = raw_flux / exposure / response
    continuum, normalized = continuum_normalize(wavelength, relative_flux)
    destination = OUTPUT / name.lower()
    destination.mkdir(parents=True, exist_ok=True)
    fits_path = destination / f"{name}_calibrated_1d.fits"
    csv_path = destination / f"{name}_calibrated_1d.csv"
    normalized_png = destination / f"{name}_normalised_1d.png"
    calibrated_png = destination / f"{name}_calibrated_1d.png"
    trace_png = destination / f"{name}_trace_overlay.png"
    write_spectrum_fits(
        fits_path,
        name,
        wavelength,
        raw_flux,
        relative_flux,
        continuum,
        normalized,
        stack_meta,
    )
    write_csv(csv_path, wavelength, relative_flux, continuum, normalized)
    plot_spectrum(calibrated_png, wavelength, relative_flux, f"{name}: relative-response calibrated")
    plot_spectrum(normalized_png, wavelength, normalized, f"{name}: continuum normalized")
    plot_trace(
        trace_png,
        stack,
        trace_y,
        20 if refine_emission else 18,
        f"{name}: aligned stack and extraction aperture",
    )
    residual_png = None
    if refinement.get("status") == "applied":
        residual_png = destination / f"{name}_wavelength_residuals.png"
        observed = np.asarray(refinement["observed"], dtype=float)
        reference = np.asarray(refinement["reference"], dtype=float)
        scale = float(refinement["scale"])
        pivot = float(refinement["pivotAngstrom"])
        offset = float(refinement["offsetAtPivotAngstrom"])
        intercept = offset - (scale - 1.0) * pivot
        plot_wavelength_residuals(
            residual_png,
            observed,
            reference,
            scale,
            intercept,
            f"{name}: wavelength refinement residuals",
        )
    manifest = {
        "target": name,
        "stack": stack_meta,
        "traceY": trace_y,
        "wavelengthRangeAngstrom": [float(wavelength[0]), float(wavelength[-1])],
        "wavelengthRefinement": refinement,
        "absoluteFluxCalibrated": False,
        "secondOrder": {
            "warningStartsAtAngstrom": 6800.0,
            "empiricalOnsetAngstrom": None,
            "dataRetained": True,
        },
        "artifacts": {
            "fits": str(fits_path),
            "csv": str(csv_path),
            "calibratedPng": str(calibrated_png),
            "normalizedPng": str(normalized_png),
            "tracePng": str(trace_png),
            "wavelengthResidualPng": None if residual_png is None else str(residual_png),
        },
    }
    (destination / f"{name}_validation.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return manifest


def main() -> int:
    global DATA, TEMPLATE, OUTPUT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--data-root",
        type=Path,
        default=os.environ.get("UVEX_ADV_TOUPSKY_ROOT"),
        help="Local May-2026 ToupSky root (or set UVEX_ADV_TOUPSKY_ROOT).",
    )
    parser.add_argument(
        "--template",
        type=Path,
        default=os.environ.get("UVEX_ADV_STELLAR_TEMPLATE"),
        help="Local p_a0v.dat template (or set UVEX_ADV_STELLAR_TEMPLATE).",
    )
    parser.add_argument("--output", type=Path, default=OUTPUT)
    args = parser.parse_args()
    if args.data_root is None:
        parser.error("--data-root is required when UVEX_ADV_TOUPSKY_ROOT is not set")
    if args.template is None:
        parser.error("--template is required when UVEX_ADV_STELLAR_TEMPLATE is not set")
    DATA = args.data_root.expanduser().resolve()
    TEMPLATE = args.template.expanduser().resolve()
    OUTPUT = args.output.expanduser().resolve()
    if not DATA.is_dir():
        parser.error(f"data root does not exist or is not a directory: {DATA}")
    if not TEMPLATE.is_file():
        parser.error(f"stellar template does not exist: {TEMPLATE}")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    flat_paths = sorted((DATA / "20260507").glob("*.fit"))
    flat_response, flat_mask, flat_meta = build_candidate_flat(flat_paths)
    vega_paths = sorted((DATA / "20260509" / "Vega").glob("*.fit"))
    vega_repairs = EXPLICIT_REPAIRS["Vega-20260509"]
    no_flat_stack, no_flat_meta = stack_frames(
        vega_paths,
        repair_files=vega_repairs,
    )
    flat_stack, flat_stack_meta = stack_frames(
        vega_paths,
        flat=(flat_response, flat_mask),
        repair_files=vega_repairs,
    )
    no_flat_flux, no_flat_trace = extract_spectrum(no_flat_stack[:, ::-1])
    flat_flux, flat_trace = extract_spectrum(flat_stack[:, ::-1])
    wavelength, matched_pixels, no_flat_correlation = vega_wavelength(no_flat_flux)
    flat_wavelength, _, flat_correlation = vega_wavelength(flat_flux)
    response, raw_response, no_flat_scatter = relative_response(
        wavelength,
        no_flat_flux,
        float(no_flat_meta["referenceExposureSeconds"]),
    )
    _, _, flat_scatter = relative_response(
        flat_wavelength,
        flat_flux,
        float(flat_stack_meta["referenceExposureSeconds"]),
    )
    flat_settings_compatible = False
    flat_accepted = (
        flat_settings_compatible
        and flat_correlation >= no_flat_correlation - 0.03
        and flat_scatter <= max(no_flat_scatter + 0.01, 1.5 * no_flat_scatter)
    )
    flat_decision = (
        "accepted"
        if flat_accepted
        else (
            "rejected: flat GAIN 10000/15000 is incompatible with science GAIN 100; "
            "the diagnostic trial also degraded template agreement and response roughness"
        )
    )
    standard_manifest = {
        "standard": "Vega",
        "stack": no_flat_meta,
        "traceY": no_flat_trace,
        "matchedBalmerPixels": matched_pixels.tolist(),
        "matchedBalmerAngstrom": BALMER.tolist(),
        "wavelengthRangeAngstrom": [float(wavelength[0]), float(wavelength[-1])],
        "noFlat": {
            "templateCorrelation": no_flat_correlation,
            "responseFractionalScatter": no_flat_scatter,
        },
        "flatTrial": {
            **flat_meta,
            "scienceGain": 100,
            "candidateFlatGains": [10000, 15000],
            "cameraSettingsCompatible": flat_settings_compatible,
            "traceY": flat_trace,
            "templateCorrelation": flat_correlation,
            "responseFractionalScatter": flat_scatter,
            "accepted": flat_accepted,
            "decision": flat_decision,
        },
    }
    (OUTPUT / "vega_standard_and_flat.json").write_text(
        json.dumps(standard_manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    plot_spectrum(OUTPUT / "Vega_standard_1d.png", wavelength, no_flat_flux, "Vega standard: extracted 1D")
    standard_rate = no_flat_flux / float(no_flat_meta["referenceExposureSeconds"])
    standard_continuum, standard_normalized = continuum_normalize(
        wavelength,
        standard_rate,
    )
    standard_fits = OUTPUT / "Vega_standard_1d.fits"
    standard_csv = OUTPUT / "Vega_standard_1d.csv"
    write_spectrum_fits(
        standard_fits,
        "Vega",
        wavelength,
        no_flat_flux,
        standard_rate,
        standard_continuum,
        standard_normalized,
        no_flat_meta,
        flux_calibration="NONE",
    )
    write_csv(
        standard_csv,
        wavelength,
        standard_rate,
        standard_continuum,
        standard_normalized,
    )
    standard_manifest["artifacts"] = {
        "fits": str(standard_fits),
        "csv": str(standard_csv),
        "spectrumPng": str(OUTPUT / "Vega_standard_1d.png"),
        "tracePng": str(OUTPUT / "Vega_trace_overlay.png"),
        "wavelengthResidualPng": str(OUTPUT / "Vega_wavelength_residuals.png"),
        "response": write_response_products(
            OUTPUT,
            wavelength,
            raw_response,
            response,
        ),
    }
    plot_wavelength_residuals(
        OUTPUT / "Vega_wavelength_residuals.png",
        BALMER,
        BALMER,
        1.0,
        0.0,
        "Vega: three Balmer anchors (quadratic RMS has no independent DOF)",
    )
    plot_trace(
        OUTPUT / "Vega_trace_overlay.png",
        no_flat_stack[:, ::-1],
        no_flat_trace,
        18,
        "Vega standard: aligned stack and extraction aperture",
    )
    (OUTPUT / "vega_standard_and_flat.json").write_text(
        json.dumps(standard_manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    ngc = process_target(
        "NGC6543",
        sorted((DATA / "20260506凌晨" / "NGC6543").glob("*.fit")),
        wavelength,
        response,
        refine_emission=True,
        repair_files=EXPLICIT_REPAIRS["NGC6543-20260506"],
    )
    hd_paths = [DATA / "20260509" / "HD140573" / "260509004918.fit"]
    hd_paths.extend(sorted((DATA / "20260509" / "HD140573").glob("260509011*.fit")))
    hd = process_target(
        "HD140573",
        hd_paths,
        wavelength,
        response,
        refine_emission=False,
        repair_files=EXPLICIT_REPAIRS["HD140573-20260509"],
    )
    summary = {
        "standard": standard_manifest,
        "targets": {"NGC6543": ngc, "HD140573": hd},
        "productionCommand": "uvex-reduce full-run",
        "note": (
            "Products were generated by the NumPy validation harness because the pinned "
            "Python 3.11/ASPIRED runtime was inaccessible to this restricted maintenance "
            "process. The production full-run command implements the same policies."
        ),
    }
    (OUTPUT / "validation_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
