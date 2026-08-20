from __future__ import annotations

from pathlib import Path

from astropy.io import fits
import numpy as np

from .config import WavelengthConfig
from .extraction import ExtractionProduct
from .models import WavelengthSolution


def calibrate_wavelength(
    extraction: ExtractionProduct,
    options: WavelengthConfig,
    target_name: str = "",
    data_root: Path | None = None,
) -> tuple[WavelengthSolution | None, list[str]]:
    mode = options.mode.strip().lower()
    if mode in {"", "none", "off", "disabled"}:
        return None, ["Wavelength calibration skipped by configuration; spectral axis remains in pixels."]
    if mode in {"known_pairs", "anchors", "manual"}:
        solution = _fit_known_pairs(extraction.flux.size, options)
        warnings = [
            "The wavelength solution uses explicitly supplied line anchors; verify every "
            "identification against the 2D spectrum and an independent reference."
        ]
        if solution.matched_pixels.size == solution.degree + 1:
            warnings.append(
                "The anchor polynomial is exactly determined and therefore has no residual "
                "degrees of freedom; its zero RMS is not an independent accuracy estimate."
            )
        if options.template_path is not None or options.template_directory is not None:
            from .stellar import measure_stellar_template_correlation

            try:
                correlation, template_path = measure_stellar_template_correlation(
                    extraction.flux,
                    extraction.mask,
                    solution.wavelength_angstrom,
                    options,
                    target_name,
                    data_root,
                )
            except Exception as error:
                warnings.append(
                    "Independent stellar-template validation of the supplied anchors failed: "
                    f"{type(error).__name__}: {error}"
                )
            else:
                solution.template_correlation = correlation
                solution.template_path = str(template_path)
                if correlation < options.minimum_template_correlation:
                    raise RuntimeError(
                        f"Known-pairs template correlation {correlation:.3f} is below the "
                        f"configured {options.minimum_template_correlation:.3f} quality limit."
                    )
        return solution, warnings
    if mode in {"solution", "solution_file", "reference", "transferred"}:
        if options.solution_path is None:
            return None, ["Transferred wavelength calibration requested, but solution_path is empty."]
        try:
            solution = _load_solution_file(options.solution_path, extraction.flux.size)
        except Exception as error:
            return None, [
                f"Transferred wavelength calibration failed: {type(error).__name__}: {error}"
            ]
        return solution, [
            "A standard-star wavelength solution was transferred to this target. The FITS files "
            "contain no grating/slit metadata, so unchanged grating angle, slit, binning, ROI, and "
            "camera mounting must be verified; use sky/telluric lines to check the zero point."
        ]
    if mode in {"stellar", "stellar_template", "standard_star", "template"}:
        from .stellar import calibrate_stellar_template

        try:
            solution = calibrate_stellar_template(
                extraction,
                options,
                target_name,
                data_root,
            )
        except Exception as error:
            return None, [
                f"Stellar-template wavelength calibration failed: "
                f"{type(error).__name__}: {error}"
            ]
        warnings = [
            "Stellar-template RMS is an internal line-fit residual, not arc-lamp absolute "
            "accuracy; radial velocity, slit illumination, blends, and broad-line centroids "
            "can shift the wavelength zero point."
        ]
        if solution.method == "stellar-template-global-linear":
            warnings.append(
                "Only two Balmer lines were available in this grating setting; a strictly "
                "quality-gated linear template solution was used instead of a quadratic fit."
            )
        elif solution.matched_pixels.size == solution.degree + 1:
            warnings.append(
                "The stellar polynomial has exactly the minimum number of matched lines; "
                "there are no residual degrees of freedom, so line-fit RMS is unavailable "
                "and the template-correlation gate carries the independent validation."
            )
        if solution.output_reversed:
            warnings.append(
                "Stellar lines proved that the configured dispersion direction was reversed; "
                "the final 2D/1D products were automatically reversed to increasing wavelength."
            )
        return solution, warnings
    if mode in {"aspired", "aspired_atlas", "atlas", "auto"}:
        if extraction.aspired_twodspec is None or extraction.arc_spectrum is None:
            return None, ["ASPIRED wavelength calibration requested, but no extracted arc spectrum is available."]
        if not options.atlas_elements:
            return None, ["ASPIRED wavelength calibration requested, but atlas_elements is empty."]
        try:
            return _fit_aspired(extraction, options), []
        except Exception as error:
            return None, [f"ASPIRED/RASCAL wavelength calibration failed: {type(error).__name__}: {error}"]
    raise ValueError(f"Unknown wavelength mode {options.mode!r}.")


def _fit_known_pairs(length: int, options: WavelengthConfig) -> WavelengthSolution:
    pixels = np.asarray(options.known_pixels, dtype=float)
    wavelengths = np.asarray(options.known_angstroms, dtype=float)
    if pixels.size != wavelengths.size:
        raise ValueError("known_pixels and known_angstroms must have the same length.")
    if pixels.size < options.polynomial_degree + 1:
        raise ValueError(
            f"At least {options.polynomial_degree + 1} known pixel/wavelength pairs are required."
        )
    if not np.isfinite(pixels).all() or not np.isfinite(wavelengths).all():
        raise ValueError("Known pixel/wavelength pairs must be finite.")
    if np.unique(pixels).size < options.polynomial_degree + 1:
        raise ValueError("Known pixel coordinates must contain enough distinct values for the fit.")
    if np.any((pixels < 0) | (pixels >= length)):
        raise ValueError("Known pixel coordinates lie outside the extracted spectrum.")

    keep = np.ones(pixels.size, dtype=bool)
    degree = min(options.polynomial_degree, pixels.size - 1)
    for _ in range(5):
        coefficients = np.polyfit(pixels[keep], wavelengths[keep], degree)
        residuals = wavelengths - np.polyval(coefficients, pixels)
        median = np.median(residuals[keep])
        scatter = 1.4826 * np.median(np.abs(residuals[keep] - median))
        if scatter <= 0:
            break
        next_keep = np.abs(residuals - median) <= max(0.25, 4.0 * scatter)
        if next_keep.sum() < degree + 1 or np.array_equal(next_keep, keep):
            break
        keep = next_keep

    coefficients = np.polyfit(pixels[keep], wavelengths[keep], degree)
    matched_residuals = wavelengths[keep] - np.polyval(coefficients, pixels[keep])
    axis = np.polyval(coefficients, np.arange(length, dtype=float))
    if not np.all(np.diff(axis) > 0):
        raise ValueError(
            "The wavelength solution is not strictly increasing from left to right. UVEX raw data "
            "must be horizontally flipped first, and anchor pixels must use post-flip coordinates."
        )
    return WavelengthSolution(
        wavelength_angstrom=axis,
        coefficients=coefficients,
        matched_pixels=pixels[keep],
        matched_wavelengths=wavelengths[keep],
        residuals_angstrom=matched_residuals,
        rms_angstrom=float(np.sqrt(np.mean(matched_residuals**2))),
        degree=degree,
        method="robust-known-pairs-polynomial",
        medium=options.medium.lower(),
        coefficient_order="descending",
    )


def _fit_aspired(extraction: ExtractionProduct, options: WavelengthConfig) -> WavelengthSolution:
    from aspired import spectral_reduction

    oned = spectral_reduction.OneDSpec(verbose=False)
    oned.from_twodspec(extraction.aspired_twodspec, stype="science")
    oned.find_arc_lines(
        prominence=options.peak_prominence,
        distance=options.peak_distance_pixels,
        refine=True,
        display=False,
        stype="science",
    )
    oned.initialise_calibrator(stype="science")
    oned.set_hough_properties(
        min_wavelength=options.minimum_angstrom,
        max_wavelength=options.maximum_angstrom,
        range_tolerance=300.0,
        linearity_tolerance=100.0,
        stype="science",
    )
    oned.add_atlas(
        options.atlas_elements,
        min_atlas_wavelength=options.minimum_angstrom,
        max_atlas_wavelength=options.maximum_angstrom,
        min_intensity=5.0,
        min_distance=5.0,
        candidate_tolerance=10.0,
        vacuum=options.medium.lower() == "vacuum",
        stype="science",
    )
    oned.do_hough_transform(stype="science")
    oned.fit(
        fit_deg=options.polynomial_degree,
        max_tries=5_000,
        fit_tolerance=10.0,
        candidate_tolerance=3.0,
        progress=False,
        return_solution=True,
        display=False,
        stype="science",
    )
    oned.apply_wavelength_calibration(stype="science")
    spectrum = oned.science_spectrum_list[0]
    wavelength = np.asarray(spectrum.wave, dtype=float)
    matched_pixels = _array_or_empty(getattr(spectrum, "matched_peaks", None))
    matched_wavelengths = _array_or_empty(getattr(spectrum, "matched_atlas", None))
    coefficients = _array_or_empty(getattr(spectrum, "fit_coeff", None))
    if wavelength.size != extraction.flux.size or not np.isfinite(wavelength).all():
        raise RuntimeError("ASPIRED returned an invalid wavelength axis.")
    if not np.all(np.diff(wavelength) > 0):
        raise RuntimeError(
            "ASPIRED returned a non-monotonic/reversed wavelength axis after UVEX orientation correction."
        )
    if matched_pixels.size and matched_wavelengths.size == matched_pixels.size:
        residuals = matched_wavelengths - np.interp(matched_pixels, np.arange(wavelength.size), wavelength)
    else:
        residuals = _array_or_empty(getattr(spectrum, "residual", None))
    raw_rms = getattr(spectrum, "rms", None)
    rms = (
        float(raw_rms)
        if raw_rms is not None
        else float(np.sqrt(np.mean(residuals**2))) if residuals.size else float("nan")
    )
    if matched_pixels.size < options.minimum_matched_lines:
        raise RuntimeError(
            f"Only {matched_pixels.size} arc lines were matched; "
            f"at least {options.minimum_matched_lines} are required."
        )
    pixel_span = float(np.ptp(matched_pixels)) / max(1.0, wavelength.size - 1.0)
    if pixel_span < options.minimum_pixel_span_fraction:
        raise RuntimeError(
            f"Matched arc lines span only {pixel_span:.1%} of the detector; "
            f"at least {options.minimum_pixel_span_fraction:.1%} is required."
        )
    if not np.isfinite(rms) or rms > options.maximum_rms_angstrom:
        raise RuntimeError(
            f"Wavelength RMS {rms:.3f} Angstrom exceeds the configured "
            f"{options.maximum_rms_angstrom:.3f} Angstrom limit."
        )
    return WavelengthSolution(
        wavelength_angstrom=wavelength,
        coefficients=coefficients,
        matched_pixels=matched_pixels,
        matched_wavelengths=matched_wavelengths,
        residuals_angstrom=residuals,
        rms_angstrom=rms,
        degree=options.polynomial_degree,
        method="aspired-rascal-atlas",
        medium=options.medium.lower(),
        coefficient_order="ascending",
    )


def _array_or_empty(value) -> np.ndarray:
    if value is None:
        return np.asarray([], dtype=float)
    array = np.asarray(value, dtype=float)
    if array.ndim == 0:
        return np.asarray([], dtype=float) if not np.isfinite(array) else array.reshape(1)
    return array.ravel()


def _load_solution_file(path: str | Path, expected_length: int) -> WavelengthSolution:
    file_path = Path(path).expanduser().resolve()
    if not file_path.is_file():
        raise FileNotFoundError(file_path)
    with fits.open(file_path, memmap=False) as hdul:
        if "SPECTRUM" not in hdul or "WAVECAL" not in hdul:
            raise ValueError("Reference FITS must contain SPECTRUM and WAVECAL extensions.")
        spectrum_data = hdul["SPECTRUM"].data
        if "WAVELENGTH" not in spectrum_data.names:
            raise ValueError("Reference FITS has no calibrated WAVELENGTH column.")
        wavelength = np.asarray(spectrum_data["WAVELENGTH"], dtype=float)
        if wavelength.size != expected_length:
            raise ValueError(
                f"Reference spectrum has {wavelength.size} pixels, but target has "
                f"{expected_length}; ROI/binning differs."
            )
        if not np.isfinite(wavelength).all() or not np.all(np.diff(wavelength) > 0):
            raise ValueError("Reference wavelength axis is not finite and strictly increasing.")
        wave_hdu = hdul["WAVECAL"]
        wave_data = wave_hdu.data
        matched_pixels = np.asarray(wave_data["PIXEL"], dtype=float)
        matched_wavelengths = np.asarray(wave_data["WAVELENGTH"], dtype=float)
        residuals = np.asarray(wave_data["RESIDUAL"], dtype=float)
        coefficient_keys = sorted(
            (key for key in wave_hdu.header if key.startswith("COEF") and key[4:].isdigit()),
            key=lambda key: int(key[4:]),
        )
        coefficients = np.asarray([wave_hdu.header[key] for key in coefficient_keys], dtype=float)
        degree = int(wave_hdu.header.get("WAVEDEG", max(0, coefficients.size - 1)))
        original_method = str(wave_hdu.header.get("WAVEMETH", "unknown"))
        rms = float(wave_hdu.header.get("WAVERMS", float("nan")))
        medium = str(wave_hdu.header.get("AIRORVAC", "unknown")).lower()
        correlation = wave_hdu.header.get("TMPLCORR")
        output_reversed = bool(wave_hdu.header.get("AUTOREV", False))
        coefficient_order = str(wave_hdu.header.get("COEWORD", "descending")).lower()
    if coefficients.size != degree + 1:
        coefficients = np.polyfit(
            np.arange(expected_length, dtype=float),
            wavelength,
            degree,
        )
    return WavelengthSolution(
        wavelength_angstrom=wavelength,
        coefficients=coefficients,
        matched_pixels=matched_pixels,
        matched_wavelengths=matched_wavelengths,
        residuals_angstrom=residuals,
        rms_angstrom=rms,
        degree=degree,
        method=f"transferred-{original_method}",
        medium=medium,
        coefficient_order=coefficient_order,
        template_path=str(file_path),
        template_correlation=float(correlation) if correlation is not None else None,
        output_reversed=output_reversed,
    )
