from pathlib import Path

import numpy as np

from uvex_reduce.config import WavelengthConfig
from uvex_reduce.extraction import ExtractionProduct
from uvex_reduce.models import TraceResult
from uvex_reduce.stellar import BALMER_LINES, calibrate_stellar_template


def test_stellar_template_finds_reversed_balmer_solution(tmp_path: Path) -> None:
    template_wavelength = np.arange(3500.0, 8000.1, 5.0)
    template_flux = np.ones_like(template_wavelength)
    for wavelength in BALMER_LINES:
        template_flux -= 0.35 * np.exp(
            -0.5 * ((template_wavelength - wavelength) / 8.0) ** 2
        )
    template_path = tmp_path / "synthetic.dat"
    np.savetxt(template_path, np.column_stack((template_wavelength, template_flux)))

    length = 2000
    detector_pixel = np.arange(length, dtype=float)
    detector_wavelength = 7400.0 - 1.75 * detector_pixel
    flux = np.interp(detector_wavelength, template_wavelength, template_flux)
    flux *= 1.0 + 0.08 * np.sin(detector_pixel / 500.0)
    uncertainty = np.full(length, 0.01)
    mask = np.zeros(length, dtype=bool)
    trace = TraceResult(
        centers=np.full(length, 100.0),
        sigma_pixels=np.full(length, 4.0),
        valid_bins=10,
        snr=100.0,
        method="synthetic",
    )
    extraction = ExtractionProduct(flux, uncertainty, mask, trace, "synthetic", [])
    options = WavelengthConfig(
        mode="stellar_template",
        polynomial_degree=2,
        template_path=template_path,
        template_star="Regulus",
        minimum_angstrom=3500.0,
        maximum_angstrom=7600.0,
        minimum_matched_lines=5,
        maximum_rms_angstrom=3.0,
        minimum_pixel_span_fraction=0.5,
        minimum_template_correlation=0.4,
        stellar_feature_prominence=0.02,
        minimum_wavelength_span_angstrom=2500.0,
        minimum_abs_dispersion_angstrom_per_pixel=0.5,
        maximum_abs_dispersion_angstrom_per_pixel=2.0,
    )

    solution = calibrate_stellar_template(extraction, options, "Regulus")

    assert solution.output_reversed
    assert np.all(np.diff(solution.wavelength_angstrom) > 0)
    assert solution.matched_pixels.size >= 5
    assert solution.template_correlation is not None
    assert solution.template_correlation > 0.8
    np.testing.assert_allclose(
        solution.wavelength_angstrom[0], detector_wavelength[-1], atol=3.0
    )
