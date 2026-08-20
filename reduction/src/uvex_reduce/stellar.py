from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re

from astropy.io import fits
import numpy as np
from scipy.ndimage import gaussian_filter1d
from scipy.signal import find_peaks

from .config import WavelengthConfig
from .extraction import ExtractionProduct
from .models import WavelengthSolution


# Air wavelengths in Angstrom.  The distinctive spacing of the Balmer series is
# used to generate candidate pixel solutions; the local stellar template then
# decides between otherwise plausible aliases.
BALMER_LINES = np.asarray(
    [
        6562.79,  # H-alpha
        4861.35,  # H-beta
        4340.47,  # H-gamma
        4101.74,  # H-delta
        3970.07,  # H-epsilon (blended with Ca H at low resolution)
        3889.05,  # H8
        3835.38,  # H9
        3797.90,  # H10
        3770.63,  # H11
        3750.15,  # H12
    ],
    dtype=float,
)


# Broad Pickles templates in the ISIS distribution cover roughly 1150--10620 A
# and are much better coarse-calibration references than the named UVEX files,
# which only cover the H-alpha region.  The latter remain useful for later
# high-resolution refinement.
TEMPLATE_ALIASES: dict[str, str] = {
    "regulus": "p_b8v.dat",
    "castor": "p_a0v.dat",
    "procyon": "p_f5iv.dat",
    "arcturus": "p_k2iii.dat",
    "pollux": "p_k0iii.dat",
    "sun": "p_g2v.dat",
    "solar": "p_g2v.dat",
    "jupiter": "p_g2v.dat",
    "sirius": "p_a0v.dat",
    "vega": "p_a0v.dat",
    "altair": "p_a7v.dat",
    "rigel": "p_b8i.dat",
    "bellatrix": "p_b2iii.dat",
}

NAMED_TEMPLATE_ALIASES: dict[str, str] = {
    "regulus": "UVEX_regulus.fits",
    "castor": "UVEX_castor.fits",
    "procyon": "UVEX_procyon.fits",
    "arcturus": "UVEX_arcturus.fits",
    "altair": "UVEX_altair.fits",
    "rigel": "UVEX_rigel.fits",
    "bellatrix": "UVEX_bellatrix.fits",
}


@dataclass(slots=True)
class StellarTemplate:
    path: Path
    wavelength_angstrom: np.ndarray
    flux: np.ndarray


@dataclass(slots=True)
class _Candidate:
    coefficients: np.ndarray
    pixels: np.ndarray
    wavelengths: np.ndarray
    residuals: np.ndarray
    rms: float
    line_score: float
    correlation: float = float("-inf")
    method: str = "stellar-template-balmer"
    exact_template_correlation: float | None = None


def calibrate_stellar_template(
    extraction: ExtractionProduct,
    options: WavelengthConfig,
    target_name: str,
    data_root: Path | None = None,
) -> WavelengthSolution:
    """Calibrate a hot/common standard star against an ISIS spectral template.

    Candidate solutions come from the Balmer-series spacing.  This is much less
    ambiguous than unconstrained cross-correlation over a several-thousand-Angstrom
    detector span.  Candidates are then ranked and quality-gated using the selected
    local template, so a coincidental set of detector defects cannot silently become
    a wavelength solution.
    """

    template = resolve_stellar_template(options, target_name, data_root)
    flux = np.asarray(extraction.flux, dtype=float)
    mask = np.asarray(extraction.mask, dtype=bool) | ~np.isfinite(flux)
    pixels, prominences = _observed_absorption_peaks(flux, mask, options)
    candidates = _balmer_candidates(pixels, prominences, flux.size, options)
    if not candidates:
        candidates = [
            _global_template_candidate(
                flux,
                mask,
                pixels,
                prominences,
                template,
                options,
                options.template_star or target_name,
                data_root,
            )
        ]

    observed_signal = _absorption_signal(flux, mask, 80.0)
    # Line scoring is cheap; template correlation is evaluated only for the best
    # distinct candidates to keep GUI runs responsive.
    candidates.sort(key=lambda candidate: candidate.line_score, reverse=True)
    for candidate in candidates[:256]:
        if candidate.method == "stellar-template-global-linear":
            continue
        axis = np.polyval(candidate.coefficients, np.arange(flux.size, dtype=float))
        candidate.correlation = _template_correlation(
            observed_signal,
            mask,
            axis,
            template,
        )

    finite_candidates = [
        candidate for candidate in candidates[:256] if np.isfinite(candidate.correlation)
    ]
    if not finite_candidates:
        candidates = [
            _global_template_candidate(
                flux,
                mask,
                pixels,
                prominences,
                template,
                options,
                options.template_star or target_name,
                data_root,
            )
        ]
        finite_candidates = candidates
    best = max(
        finite_candidates,
        key=lambda candidate: (
            candidate.correlation,
            -candidate.rms,
            candidate.pixels.size,
        ),
    )
    if (
        best.correlation < options.minimum_template_correlation
        or best.rms > options.maximum_rms_angstrom
    ):
        # A narrower grating setting can contain only H-alpha and H-beta.  In
        # that case the multi-line Balmer solver is intentionally unable to
        # pass.  A global template search may still provide a safe *linear*
        # two-line solution, but it is subjected to stricter broad-template and
        # named H-alpha-template correlation gates.
        best = _global_template_candidate(
            flux,
            mask,
            pixels,
            prominences,
            template,
            options,
            options.template_star or target_name,
            data_root,
        )
    if best.correlation < options.minimum_template_correlation:
        raise RuntimeError(
            f"Best stellar-template correlation {best.correlation:.3f} is below the configured "
            f"{options.minimum_template_correlation:.3f} quality limit."
        )
    if best.rms > options.maximum_rms_angstrom:
        raise RuntimeError(
            f"Stellar-line RMS {best.rms:.3f} Angstrom exceeds the configured "
            f"{options.maximum_rms_angstrom:.3f} Angstrom limit."
        )
    pixel_span = float(np.ptp(best.pixels)) / max(1.0, flux.size - 1.0)
    if pixel_span < options.minimum_pixel_span_fraction:
        raise RuntimeError(
            f"Matched stellar lines span only {pixel_span:.1%} of the detector; "
            f"at least {options.minimum_pixel_span_fraction:.1%} is required."
        )

    old_axis = np.polyval(best.coefficients, np.arange(flux.size, dtype=float))
    difference = np.diff(old_axis)
    if not (np.all(difference > 0) or np.all(difference < 0)):
        raise RuntimeError("The stellar wavelength candidate is not monotonic across the detector.")

    output_reversed = bool(np.all(difference < 0))
    if output_reversed and not options.auto_reverse_output:
        raise RuntimeError(
            "The stellar template found red-left/blue-right data, but auto_reverse_output is disabled."
        )

    if output_reversed:
        axis = old_axis[::-1].copy()
        matched_pixels = (flux.size - 1.0) - best.pixels
        order = np.argsort(matched_pixels)
        matched_pixels = matched_pixels[order]
        matched_wavelengths = best.wavelengths[order]
        coefficients = np.polyfit(
            np.arange(flux.size, dtype=float),
            axis,
            best.coefficients.size - 1,
        )
    else:
        axis = old_axis
        order = np.argsort(best.pixels)
        matched_pixels = best.pixels[order]
        matched_wavelengths = best.wavelengths[order]
        coefficients = best.coefficients

    residuals = matched_wavelengths - np.polyval(coefficients, matched_pixels)
    rms = (
        float(np.sqrt(np.mean(residuals**2)))
        if matched_pixels.size > coefficients.size
        else float("nan")
    )
    if not np.all(np.diff(axis) > 0):
        raise RuntimeError("Automatic orientation correction did not produce an increasing wavelength axis.")

    return WavelengthSolution(
        wavelength_angstrom=axis,
        coefficients=np.asarray(coefficients, dtype=float),
        matched_pixels=np.asarray(matched_pixels, dtype=float),
        matched_wavelengths=np.asarray(matched_wavelengths, dtype=float),
        residuals_angstrom=np.asarray(residuals, dtype=float),
        rms_angstrom=rms,
        degree=coefficients.size - 1,
        method=best.method,
        medium=options.medium.lower(),
        coefficient_order="descending",
        template_path=str(template.path),
        template_correlation=float(best.correlation),
        output_reversed=output_reversed,
    )


def resolve_stellar_template(
    options: WavelengthConfig,
    target_name: str,
    data_root: Path | None,
) -> StellarTemplate:
    if options.template_path is not None:
        path = Path(options.template_path).expanduser().resolve()
        if not path.is_file():
            raise FileNotFoundError(f"Configured stellar template does not exist: {path}")
        return load_stellar_template(path)

    star = options.template_star or target_name
    normalized = re.sub(r"[^a-z0-9]+", "", star.casefold())
    filename = TEMPLATE_ALIASES.get(normalized)
    if filename is None:
        supported = ", ".join(sorted(TEMPLATE_ALIASES))
        raise ValueError(
            f"No automatic stellar template mapping exists for {star!r}. "
            f"Set wavelength.template_path explicitly. Known aliases: {supported}."
        )

    directories: list[Path] = []
    if options.template_directory is not None:
        directories.append(Path(options.template_directory).expanduser().resolve())
    if data_root is not None:
        root = Path(data_root).expanduser().resolve()
        directories.extend(
            [
                root / "isis_6_1_1" / "isis_data3",
                root / "isis_data3",
            ]
        )
    for directory in dict.fromkeys(directories):
        candidate = directory / filename
        if candidate.is_file():
            return load_stellar_template(candidate)
    searched = ", ".join(str(path) for path in directories) or "no directory configured"
    raise FileNotFoundError(
        f"Could not find ISIS template {filename!r} for {star!r}; searched: {searched}."
    )


def load_stellar_template(path: str | Path) -> StellarTemplate:
    file_path = Path(path).expanduser().resolve()
    if file_path.suffix.casefold() in {".fit", ".fits", ".fts"}:
        with fits.open(file_path, memmap=False) as hdul:
            data = np.asarray(hdul[0].data, dtype=float).squeeze()
            header = hdul[0].header
        if data.ndim != 1:
            raise ValueError(f"Stellar template must be one-dimensional: {file_path}")
        increment = header.get("CDELT1", header.get("CD1_1"))
        if increment is None or "CRVAL1" not in header:
            raise ValueError(f"Stellar FITS template has no linear wavelength WCS: {file_path}")
        pixel = np.arange(data.size, dtype=float) + 1.0
        wavelength = float(header["CRVAL1"]) + (
            pixel - float(header.get("CRPIX1", 1.0))
        ) * float(increment)
        flux = data
    else:
        table = np.loadtxt(file_path, dtype=float, comments="#", usecols=(0, 1))
        if table.ndim != 2 or table.shape[1] != 2:
            raise ValueError(f"Stellar template must contain wavelength and flux columns: {file_path}")
        wavelength, flux = table[:, 0], table[:, 1]

    valid = np.isfinite(wavelength) & np.isfinite(flux) & (flux > 0)
    wavelength = np.asarray(wavelength[valid], dtype=float)
    flux = np.asarray(flux[valid], dtype=float)
    if wavelength.size < 20:
        raise ValueError(f"Stellar template contains too few valid samples: {file_path}")
    order = np.argsort(wavelength)
    wavelength, flux = wavelength[order], flux[order]
    unique = np.concatenate(([True], np.diff(wavelength) > 0))
    return StellarTemplate(file_path, wavelength[unique], flux[unique])


def _observed_absorption_peaks(
    flux: np.ndarray,
    mask: np.ndarray,
    options: WavelengthConfig,
) -> tuple[np.ndarray, np.ndarray]:
    signal = _absorption_signal(flux, mask, 80.0)
    peaks, properties = find_peaks(
        signal,
        prominence=options.stellar_feature_prominence,
        distance=20,
        width=6,
    )
    if peaks.size < 2:
        raise RuntimeError(
            f"Only {peaks.size} broad absorption features were detected; "
            "at least two are required for stellar-template calibration."
        )
    order = np.argsort(properties["prominences"])[::-1][:24]
    refined = np.asarray([_subpixel_peak(signal, int(peaks[index])) for index in order])
    return refined, np.asarray(properties["prominences"][order], dtype=float)


def _balmer_candidates(
    observed_pixels: np.ndarray,
    prominences: np.ndarray,
    length: int,
    options: WavelengthConfig,
) -> list[_Candidate]:
    candidates: dict[tuple[float, ...], _Candidate] = {}
    # A degree-N solution mathematically needs N+1 lines.  When exactly that
    # many are available (as in the May Vega setting with H-gamma/H-beta/H-alpha),
    # the broad-template correlation remains the independent quality gate and
    # the final RMS is explicitly reported as unavailable rather than as zero.
    minimum_lines = max(options.minimum_matched_lines, options.polynomial_degree + 1)
    tolerance = options.stellar_feature_tolerance_pixels

    for first_index in range(observed_pixels.size):
        for second_index in range(first_index + 1, observed_pixels.size):
            first_pixel = observed_pixels[first_index]
            second_pixel = observed_pixels[second_index]
            pixel_separation = second_pixel - first_pixel
            if abs(pixel_separation) < 100:
                continue
            for first_line in BALMER_LINES:
                for second_line in BALMER_LINES:
                    if first_line == second_line:
                        continue
                    dispersion = (second_line - first_line) / pixel_separation
                    absolute_dispersion = abs(dispersion)
                    if not (
                        options.minimum_abs_dispersion_angstrom_per_pixel
                        <= absolute_dispersion
                        <= options.maximum_abs_dispersion_angstrom_per_pixel
                    ):
                        continue
                    if absolute_dispersion * (length - 1) < options.minimum_wavelength_span_angstrom:
                        continue
                    intercept = first_line - dispersion * first_pixel
                    endpoints = intercept + dispersion * np.asarray([0.0, length - 1.0])
                    low, high = float(np.min(endpoints)), float(np.max(endpoints))
                    if high < options.minimum_angstrom or low > options.maximum_angstrom:
                        continue
                    if low < options.minimum_angstrom - 750 or high > options.maximum_angstrom + 750:
                        continue

                    matched_pixels: list[float] = []
                    matched_wavelengths: list[float] = []
                    matched_prominences: list[float] = []
                    used_observed: set[int] = set()
                    for wavelength in BALMER_LINES:
                        predicted = (wavelength - intercept) / dispersion
                        if not 0 <= predicted < length:
                            continue
                        distances = np.abs(observed_pixels - predicted)
                        observed_index = int(np.argmin(distances))
                        if distances[observed_index] > tolerance or observed_index in used_observed:
                            continue
                        used_observed.add(observed_index)
                        matched_pixels.append(float(observed_pixels[observed_index]))
                        matched_wavelengths.append(float(wavelength))
                        matched_prominences.append(float(prominences[observed_index]))
                    if len(matched_pixels) < minimum_lines:
                        continue

                    pixels = np.asarray(matched_pixels, dtype=float)
                    wavelengths = np.asarray(matched_wavelengths, dtype=float)
                    coefficients = np.polyfit(pixels, wavelengths, options.polynomial_degree)
                    residuals = wavelengths - np.polyval(coefficients, pixels)
                    rms = float(np.sqrt(np.mean(residuals**2)))
                    pixel_span = float(np.ptp(pixels)) / max(1.0, length - 1.0)
                    line_score = (
                        2.0 * pixels.size
                        + 4.0 * pixel_span
                        + float(np.sum(matched_prominences))
                        - 0.5 * rms
                    )
                    key = tuple(np.round(coefficients, 5))
                    candidate = _Candidate(
                        coefficients=coefficients,
                        pixels=pixels,
                        wavelengths=wavelengths,
                        residuals=residuals,
                        rms=rms,
                        line_score=line_score,
                    )
                    previous = candidates.get(key)
                    if previous is None or candidate.line_score > previous.line_score:
                        candidates[key] = candidate
    return list(candidates.values())


def _global_template_candidate(
    flux: np.ndarray,
    mask: np.ndarray,
    observed_pixels: np.ndarray,
    prominences: np.ndarray,
    template: StellarTemplate,
    options: WavelengthConfig,
    target_name: str,
    data_root: Path | None,
) -> _Candidate:
    """Find a linear solution when only two Balmer lines lie on the detector."""

    length = flux.size
    midpoint = 0.5 * (length - 1.0)
    sample_pixels = np.arange(0, length, 8, dtype=float)
    observed_signal = _absorption_signal(flux, mask, 80.0)[::8]
    observed_signal = _robust_standardize(observed_signal)

    template_step = float(np.median(np.diff(template.wavelength_angstrom)))
    template_sigma = max(1.0, 80.0 / max(template_step, 1e-6))
    template_signal = _absorption_signal(
        template.flux,
        np.zeros(template.flux.size, dtype=bool),
        template_sigma,
    )
    template_signal = gaussian_filter1d(template_signal, 1.0, mode="nearest")

    center_step = 10.0
    centers = np.arange(
        options.minimum_angstrom,
        options.maximum_angstrom + 0.5 * center_step,
        center_step,
    )
    dispersion_step = 0.01
    magnitudes = np.arange(
        options.minimum_abs_dispersion_angstrom_per_pixel,
        options.maximum_abs_dispersion_angstrom_per_pixel + 0.5 * dispersion_step,
        dispersion_step,
    )
    dispersions = np.concatenate((-magnitudes[::-1], magnitudes))
    coarse: list[tuple[float, float, float]] = []
    minimum_global_span = min(options.minimum_wavelength_span_angstrom, 1_200.0)

    normalized_observed = observed_signal - float(np.mean(observed_signal))
    observed_norm = float(np.linalg.norm(normalized_observed))
    if observed_norm <= 0:
        raise RuntimeError("The observed stellar spectrum has no usable line contrast.")

    for dispersion in dispersions:
        if abs(dispersion) * (length - 1.0) < minimum_global_span:
            continue
        query = centers[:, None] + dispersion * (sample_pixels[None, :] - midpoint)
        inside = (
            (query >= template.wavelength_angstrom[0])
            & (query <= template.wavelength_angstrom[-1])
        )
        overlap_fraction = np.mean(inside, axis=1)
        values = np.interp(
            query.ravel(),
            template.wavelength_angstrom,
            template_signal,
        ).reshape(query.shape)
        values -= np.mean(values, axis=1, keepdims=True)
        norms = np.linalg.norm(values, axis=1) * observed_norm
        correlations = np.divide(
            values @ normalized_observed,
            norms,
            out=np.full(centers.size, -np.inf),
            where=norms > 0,
        )
        correlations[overlap_fraction < 0.8] = -np.inf
        count = min(3, correlations.size)
        for index in np.argpartition(correlations, -count)[-count:]:
            if np.isfinite(correlations[index]):
                coarse.append(
                    (float(correlations[index]), float(centers[index]), float(dispersion))
                )
    if not coarse:
        raise RuntimeError("Global stellar-template search found no overlapping wavelength range.")

    named_template = _resolve_named_template(options, target_name, data_root)
    evaluated: list[_Candidate] = []
    seen: set[tuple[int, int]] = set()
    for coarse_correlation, center, dispersion in sorted(coarse, reverse=True)[:24]:
        key = (round(center), round(dispersion * 1_000))
        if key in seen:
            continue
        seen.add(key)
        initial_axis = center + dispersion * (np.arange(length, dtype=float) - midpoint)
        matched_pixels, matched_wavelengths = _match_balmer_to_axis(
            initial_axis,
            observed_pixels,
            prominences,
            options.stellar_feature_tolerance_pixels * 1.5,
        )
        if matched_pixels.size < 2:
            continue
        degree = min(1, matched_pixels.size - 1)
        coefficients = np.polyfit(matched_pixels, matched_wavelengths, degree)
        residuals = matched_wavelengths - np.polyval(coefficients, matched_pixels)
        rms = (
            float(np.sqrt(np.mean(residuals**2)))
            if matched_pixels.size > coefficients.size
            else float("nan")
        )
        axis = np.polyval(coefficients, np.arange(length, dtype=float))
        exact_correlation = (
            _partial_template_correlation(flux, mask, axis, named_template)
            if named_template is not None
            else None
        )
        evaluated.append(
            _Candidate(
                coefficients=coefficients,
                pixels=matched_pixels,
                wavelengths=matched_wavelengths,
                residuals=residuals,
                rms=rms,
                line_score=coarse_correlation,
                correlation=coarse_correlation,
                method="stellar-template-global-linear",
                exact_template_correlation=exact_correlation,
            )
        )
    if not evaluated:
        raise RuntimeError(
            "Global template search could not associate at least two Balmer lines with the data."
        )

    best = max(
        evaluated,
        key=lambda candidate: (
            candidate.correlation
            + 0.15 * max(candidate.exact_template_correlation or -1.0, -1.0),
            candidate.pixels.size,
        ),
    )
    stricter_correlation = max(options.minimum_template_correlation, 0.50)
    if best.correlation < stricter_correlation:
        raise RuntimeError(
            f"Two-line global template correlation {best.correlation:.3f} is below the "
            f"stricter {stricter_correlation:.3f} fallback limit."
        )
    if named_template is not None and (
        best.exact_template_correlation is None or best.exact_template_correlation < 0.65
    ):
        value = best.exact_template_correlation
        rendered = "unavailable" if value is None else f"{value:.3f}"
        raise RuntimeError(
            f"Named UVEX H-alpha template validation was {rendered}; at least 0.650 is required."
        )
    return best


def _match_balmer_to_axis(
    wavelength_axis: np.ndarray,
    observed_pixels: np.ndarray,
    prominences: np.ndarray,
    tolerance_pixels: float,
) -> tuple[np.ndarray, np.ndarray]:
    increasing = bool(wavelength_axis[-1] > wavelength_axis[0])
    interpolation_axis = wavelength_axis if increasing else wavelength_axis[::-1]
    interpolation_pixels = (
        np.arange(wavelength_axis.size, dtype=float)
        if increasing
        else np.arange(wavelength_axis.size - 1, -1, -1, dtype=float)
    )
    matched_pixels: list[float] = []
    matched_wavelengths: list[float] = []
    used: set[int] = set()
    for wavelength in BALMER_LINES:
        if not interpolation_axis[0] <= wavelength <= interpolation_axis[-1]:
            continue
        predicted = float(np.interp(wavelength, interpolation_axis, interpolation_pixels))
        distances = np.abs(observed_pixels - predicted)
        order = np.argsort(distances)
        selected = next((int(index) for index in order if int(index) not in used), None)
        if selected is None or distances[selected] > tolerance_pixels:
            continue
        used.add(selected)
        matched_pixels.append(float(observed_pixels[selected]))
        matched_wavelengths.append(float(wavelength))
    return np.asarray(matched_pixels, dtype=float), np.asarray(matched_wavelengths, dtype=float)


def _resolve_named_template(
    options: WavelengthConfig,
    target_name: str,
    data_root: Path | None,
) -> StellarTemplate | None:
    normalized = re.sub(r"[^a-z0-9]+", "", target_name.casefold())
    filename = NAMED_TEMPLATE_ALIASES.get(normalized)
    if filename is None:
        return None
    directories: list[Path] = []
    if options.template_directory is not None:
        directories.append(Path(options.template_directory).expanduser().resolve())
    if data_root is not None:
        root = Path(data_root).expanduser().resolve()
        directories.extend([root / "isis_6_1_1" / "isis_data3", root / "isis_data3"])
    for directory in dict.fromkeys(directories):
        path = directory / filename
        if path.is_file():
            return load_stellar_template(path)
    return None


def measure_stellar_template_correlation(
    flux: np.ndarray,
    mask: np.ndarray,
    wavelength_axis: np.ndarray,
    options: WavelengthConfig,
    target_name: str,
    data_root: Path | None = None,
) -> tuple[float, Path]:
    """Independently check a supplied wavelength axis against a stellar template.

    This is intentionally separate from the Balmer-line polynomial fit.  It lets
    a manually anchored/known-pairs solution retain an independent broad-template
    quality gate instead of treating an exactly determined polynomial's zero
    residual as proof that the line identifications were correct.
    """

    template = resolve_stellar_template(options, target_name, data_root)
    values = np.asarray(flux, dtype=float)
    invalid = np.asarray(mask, dtype=bool) | ~np.isfinite(values)
    axis = np.asarray(wavelength_axis, dtype=float)
    if values.ndim != 1 or invalid.shape != values.shape or axis.shape != values.shape:
        raise ValueError("Flux, mask, and wavelength axis must be matching 1D arrays.")
    correlation = _partial_template_correlation(values, invalid, axis, template)
    if not np.isfinite(correlation):
        raise RuntimeError("Too little template overlap to measure stellar correlation.")
    return float(correlation), template.path


def _partial_template_correlation(
    flux: np.ndarray,
    mask: np.ndarray,
    wavelength_axis: np.ndarray,
    template: StellarTemplate,
) -> float:
    spacing = float(np.median(np.diff(template.wavelength_angstrom)))
    smoothed_flux = gaussian_filter1d(
        template.flux,
        max(1.0, 2.0 / max(spacing, 1e-6)),
        mode="nearest",
    )
    sampled = np.interp(
        wavelength_axis,
        template.wavelength_angstrom,
        smoothed_flux,
        left=np.nan,
        right=np.nan,
    )
    overlap = np.isfinite(sampled) & ~mask & np.isfinite(flux)
    if overlap.sum() < 200:
        return float("nan")
    observed_signal = _absorption_signal(flux, mask, 80.0)
    filled = _interpolate_masked(sampled, ~np.isfinite(sampled))
    reference_signal = _absorption_signal(filled, ~np.isfinite(sampled), 80.0)
    observed = _robust_standardize(observed_signal[overlap])
    reference = _robust_standardize(reference_signal[overlap])
    return float(
        np.corrcoef(np.clip(observed, -4, 4), np.clip(reference, -4, 4))[0, 1]
    )


def _template_correlation(
    observed_signal: np.ndarray,
    mask: np.ndarray,
    wavelength_axis: np.ndarray,
    template: StellarTemplate,
) -> float:
    sampled = np.interp(
        wavelength_axis,
        template.wavelength_angstrom,
        template.flux,
        left=np.nan,
        right=np.nan,
    )
    overlap = np.isfinite(sampled) & ~mask & np.isfinite(observed_signal)
    if overlap.sum() < max(200, int(0.35 * wavelength_axis.size)):
        return float("nan")
    filled = _interpolate_masked(sampled, ~np.isfinite(sampled))
    template_signal = _absorption_signal(filled, ~np.isfinite(sampled), 80.0)
    observed = _robust_standardize(observed_signal[overlap])
    reference = _robust_standardize(template_signal[overlap])
    if np.std(observed) <= 0 or np.std(reference) <= 0:
        return float("nan")
    return float(np.corrcoef(np.clip(observed, -4, 4), np.clip(reference, -4, 4))[0, 1])


def _absorption_signal(flux: np.ndarray, mask: np.ndarray, sigma_pixels: float) -> np.ndarray:
    clean = _interpolate_masked(np.asarray(flux, dtype=float), mask | ~np.isfinite(flux))
    positive = clean[np.isfinite(clean) & (clean > 0)]
    if positive.size == 0:
        raise RuntimeError("The extracted spectrum contains no positive finite flux.")
    floor = max(float(np.percentile(positive, 1.0)) * 0.05, np.finfo(float).tiny)
    log_flux = np.log(np.clip(clean, floor, None))
    continuum = gaussian_filter1d(log_flux, sigma_pixels, mode="nearest")
    signal = continuum - log_flux
    signal -= float(np.median(signal[np.isfinite(signal)]))
    return signal


def _interpolate_masked(values: np.ndarray, mask: np.ndarray) -> np.ndarray:
    result = np.asarray(values, dtype=float).copy()
    invalid = np.asarray(mask, dtype=bool) | ~np.isfinite(result)
    valid_indices = np.flatnonzero(~invalid)
    if valid_indices.size < 2:
        raise RuntimeError("Too few valid spectral samples remain after masking.")
    invalid_indices = np.flatnonzero(invalid)
    result[invalid_indices] = np.interp(invalid_indices, valid_indices, result[valid_indices])
    return result


def _robust_standardize(values: np.ndarray) -> np.ndarray:
    values = np.asarray(values, dtype=float)
    median = float(np.median(values))
    mad = 1.4826 * float(np.median(np.abs(values - median)))
    scale = mad if mad > 1e-12 else float(np.std(values))
    return (values - median) / max(scale, 1e-12)


def _subpixel_peak(signal: np.ndarray, index: int) -> float:
    if index <= 0 or index >= signal.size - 1:
        return float(index)
    left, center, right = signal[index - 1 : index + 2]
    denominator = left - 2.0 * center + right
    if not np.isfinite(denominator) or abs(denominator) < 1e-12:
        return float(index)
    offset = 0.5 * (left - right) / denominator
    return float(index + np.clip(offset, -0.5, 0.5))
