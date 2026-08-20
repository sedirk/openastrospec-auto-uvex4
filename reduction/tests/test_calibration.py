import json
from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.calibration import (
    ReducedSpectrumData,
    apply_response_and_normalize,
    derive_relative_response,
    refine_emission_zero_point,
    write_calibration_products,
)


def _spectrum(
    path: Path,
    wavelength: np.ndarray,
    flux: np.ndarray,
    exposure: float = 10.0,
) -> ReducedSpectrumData:
    header = fits.Header()
    header["EXPTIME"] = exposure
    header["OBJECT"] = "Synthetic"
    return ReducedSpectrumData(
        path=path,
        header=header,
        pixel=np.arange(wavelength.size, dtype=float),
        wavelength_angstrom=wavelength,
        flux_adu=flux,
        uncertainty_adu=np.sqrt(np.maximum(flux, 1.0)),
        mask=np.zeros(wavelength.size, dtype=bool),
        exposure_s=exposure,
    )


def test_relative_response_recovers_smooth_shape(tmp_path: Path) -> None:
    wavelength = np.linspace(3800.0, 7400.0, 2400)
    template_flux = 1.5 + 0.3 * np.cos((wavelength - 5000.0) / 900.0)
    true_response = 0.55 + 0.45 * np.exp(-0.5 * ((wavelength - 5600.0) / 1300.0) ** 2)
    template_path = tmp_path / "template.dat"
    np.savetxt(template_path, np.column_stack([wavelength, template_flux]))
    standard = _spectrum(
        tmp_path / "standard.fits",
        wavelength,
        template_flux * true_response * 10_000.0,
        exposure=10.0,
    )

    response = derive_relative_response(standard, template_path, "Synthetic")

    band = (wavelength >= 5400.0) & (wavelength <= 5600.0)
    expected = true_response / np.median(true_response[band])
    np.testing.assert_allclose(response.response[100:-100], expected[100:-100], rtol=0.03)
    assert response.fractional_scatter < 0.02


def test_emission_line_zero_point_is_recovered(tmp_path: Path) -> None:
    wavelength = np.linspace(4000.0, 6800.0, 5601)
    measured_offset = -24.7
    references = np.asarray([4101.74, 4340.47, 4861.35, 4958.91, 5006.84, 6562.79])
    flux = np.full(wavelength.size, 100.0)
    for index, line in enumerate(references):
        center = line + measured_offset
        flux += (500.0 + 100.0 * index) * np.exp(-0.5 * ((wavelength - center) / 1.2) ** 2)
    science = _spectrum(tmp_path / "science.fits", wavelength, flux)

    correction = refine_emission_zero_point(
        science,
        references,
        maximum_offset_angstrom=40.0,
        match_tolerance_angstrom=2.0,
    )

    assert correction.measured_offset_angstrom == pytest.approx(measured_offset, abs=0.2)
    assert correction.applied_offset_angstrom == pytest.approx(-measured_offset, abs=0.2)
    assert correction.reference_wavelengths.size == references.size


def test_emission_lines_refine_small_dispersion_scale_error(tmp_path: Path) -> None:
    wavelength = np.linspace(4000.0, 7300.0, 6601)
    references = np.asarray(
        [4101.74, 4340.47, 4861.35, 4958.91, 5006.84, 6562.79, 7135.79]
    )
    pivot = 5200.0
    expected_scale = 1.003
    expected_offset = -42.0
    observed_lines = pivot + (references - pivot - expected_offset) / expected_scale
    flux = np.full(wavelength.size, 100.0)
    for index, line in enumerate(observed_lines):
        flux += (700.0 + 100.0 * index) * np.exp(
            -0.5 * ((wavelength - line) / 1.0) ** 2
        )
    science = _spectrum(tmp_path / "science-affine.fits", wavelength, flux)

    correction = refine_emission_zero_point(
        science,
        references,
        maximum_offset_angstrom=60.0,
        match_tolerance_angstrom=2.0,
    )

    assert correction.method == "bounded-affine"
    assert correction.scale == pytest.approx(expected_scale, abs=3e-4)
    corrected = correction.apply(observed_lines)
    np.testing.assert_allclose(corrected, references, atol=0.4)
    assert correction.rms_angstrom < 0.5


def test_response_application_writes_relative_and_normalized_products(tmp_path: Path) -> None:
    wavelength = np.linspace(4000.0, 7000.0, 1200)
    template_path = tmp_path / "template.dat"
    template_flux = 1.0 + 0.1 * (wavelength - 5500.0) / 1500.0
    np.savetxt(template_path, np.column_stack([wavelength, template_flux]))
    response_shape = 0.7 + 0.3 * np.exp(-0.5 * ((wavelength - 5500.0) / 1000.0) ** 2)
    standard = _spectrum(
        tmp_path / "standard.fits",
        wavelength,
        template_flux * response_shape * 50_000.0,
    )
    response = derive_relative_response(standard, template_path, "Synthetic")
    continuum = 200.0 * (1.0 + 0.08 * (wavelength - 5500.0) / 1500.0)
    line = 800.0 * np.exp(-0.5 * ((wavelength - 5006.84) / 2.0) ** 2)
    science = _spectrum(
        tmp_path / "science.fits",
        wavelength,
        (continuum + line) * response.response * 10.0,
    )

    calibrated = apply_response_and_normalize(science, response)
    products = write_calibration_products(response, calibrated, tmp_path / "out", "Target")

    outside_line = np.abs(wavelength - 5006.84) > 30.0
    assert np.nanmedian(calibrated.normalized_flux[outside_line]) == pytest.approx(1.0, abs=0.08)
    with fits.open(products["calibrated_fits"]) as hdul:
        hdul.verify("exception")
        assert hdul[0].header["FLUXCAL"] == "RELATIVE"
        assert hdul[0].header["ABSFLUX"] is False
        assert hdul[0].header["ORD2STRT"] == 6800.0
        assert hdul[0].header["ORD2STAT"] == "NOT_TESTED"
        assert hdul[0].header["ORD2MEAS"] is False
        assert "NORMALIZED_FLUX" in hdul["SPECTRUM"].columns.names
        order2 = np.asarray(hdul["SPECTRUM"].data["ORDER2_RISK"], dtype=bool)
        mask = np.asarray(hdul["SPECTRUM"].data["MASK"], dtype=bool)
        assert order2[wavelength >= 6800.0].all()
        assert not mask[wavelength >= 6800.0].all()
    manifest = json.loads(products["manifest"].read_text(encoding="utf-8"))
    assert manifest["scienceSourceProvenance"] == {
        "path": str((tmp_path / "science.fits").resolve()),
        "exists": False,
    }
