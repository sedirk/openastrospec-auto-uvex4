from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.calibration import CalibratedSpectrum, ReducedSpectrumData
from uvex_reduce.line_analysis import measure_nebular_lines, write_nebular_line_products


def _synthetic_calibrated(tmp_path: Path) -> CalibratedSpectrum:
    wavelength = np.linspace(4200.0, 7200.0, 6001)
    continuum = np.full_like(wavelength, 100.0)
    flux = continuum.copy()
    sigma = 2.5
    amplitudes = {
        4340.47: 50.0,
        4861.35: 100.0,
        4958.91: 150.0,
        5006.84: 450.0,
        5875.62: 25.0,
        6548.05: 15.0,
        6562.79: 286.0,
        6583.45: 44.4,
        6716.44: 20.0,
        6730.82: 25.0,
        7065.19: 15.0,
        7135.79: 30.0,
    }
    for center, amplitude in amplitudes.items():
        flux += amplitude * np.exp(-0.5 * ((wavelength - center) / sigma) ** 2)
    flux += np.random.default_rng(42).normal(0.0, 0.2, wavelength.size)
    uncertainty = np.ones_like(wavelength)
    source = ReducedSpectrumData(
        path=tmp_path / "science.fits",
        header=fits.Header({"EXPTIME": 10.0}),
        pixel=np.arange(wavelength.size, dtype=float),
        wavelength_angstrom=wavelength,
        flux_adu=flux,
        uncertainty_adu=uncertainty,
        mask=np.zeros(wavelength.size, dtype=bool),
        exposure_s=10.0,
    )
    return CalibratedSpectrum(
        source=source,
        wavelength_angstrom=wavelength,
        relative_flux=flux,
        relative_uncertainty=uncertainty,
        continuum=continuum,
        normalized_flux=flux / continuum,
        normalized_uncertainty=uncertainty / continuum,
        response=np.ones_like(wavelength),
        mask=np.zeros(wavelength.size, dtype=bool),
        response_standard="Synthetic",
        response_template=tmp_path / "template.dat",
        zero_point=None,
        second_order_start_angstrom=6800.0,
        second_order_status="not_tested",
        second_order_empirical_onset_angstrom=None,
        second_order_diagnostic_marker_angstrom=7292.0,
        second_order_assessment_path=None,
    )


def test_nebular_line_analysis_recovers_diagnostic_ratios(tmp_path: Path) -> None:
    calibrated = _synthetic_calibrated(tmp_path)

    analysis = measure_nebular_lines(calibrated)

    assert analysis.oiii_5007_to_4959 == pytest.approx(3.0, rel=0.02)
    assert analysis.halpha_to_hbeta == pytest.approx(2.86, rel=0.03)
    assert analysis.sii_6716_to_6731 == pytest.approx(0.8, rel=0.03)
    assert analysis.summary()["detectedLineCount"] >= 10
    he7065 = next(line for line in analysis.measurements if line.label == "He I 7065")
    assert he7065.second_order_risk is True


def test_nebular_line_products_are_written(tmp_path: Path) -> None:
    calibrated = _synthetic_calibrated(tmp_path)
    analysis = measure_nebular_lines(calibrated)

    products = write_nebular_line_products(calibrated, analysis, tmp_path, "Target")

    assert all(path.is_file() and path.stat().st_size > 0 for path in products.values())
