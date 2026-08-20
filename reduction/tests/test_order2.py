from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.calibration import ReducedSpectrumData, derive_relative_response
from uvex_reduce.order2 import (
    assess_second_order,
    load_second_order_assessment,
    write_second_order_products,
)


def _response(
    tmp_path: Path,
    name: str,
    template_flux: np.ndarray,
    instrument_response: np.ndarray,
    wavelength: np.ndarray,
    blue_to_red_ratio: float,
):
    template_path = tmp_path / f"{name}.dat"
    order = np.argsort(np.concatenate([wavelength / 2.0, wavelength]))
    combined_wave = np.concatenate([wavelength / 2.0, wavelength])[order]
    combined_flux = np.concatenate(
        [template_flux * blue_to_red_ratio, template_flux]
    )[order]
    np.savetxt(template_path, np.column_stack([combined_wave, combined_flux]))

    spectrum = ReducedSpectrumData(
        path=tmp_path / f"{name}.fits",
        header=fits.Header({"EXPTIME": 10.0}),
        pixel=np.arange(wavelength.size, dtype=float),
        wavelength_angstrom=wavelength,
        flux_adu=template_flux * instrument_response * 10.0,
        uncertainty_adu=np.full(wavelength.size, 1.0),
        mask=np.zeros(wavelength.size, dtype=bool),
        exposure_s=10.0,
    )
    return derive_relative_response(
        spectrum,
        template_path,
        name,
        smoothing_angstrom=20.0,
    )


def test_second_order_positive_hot_excess_is_detected(tmp_path: Path) -> None:
    wavelength = np.linspace(5000.0, 7600.0, 2601)
    base = 0.9 + 0.1 * np.exp(-0.5 * ((wavelength - 5900.0) / 1200.0) ** 2)
    ramp = np.clip((wavelength - 6900.0) / 250.0, 0.0, 1.0)
    hot = _response(
        tmp_path,
        "Hot",
        np.full(wavelength.size, 2.0),
        base * (1 + 0.12 * ramp),
        wavelength,
        blue_to_red_ratio=2.0,
    )
    cool = _response(
        tmp_path,
        "Cool",
        np.full(wavelength.size, 1.0),
        base,
        wavelength,
        blue_to_red_ratio=0.5,
    )

    study = assess_second_order(
        {"Hot": hot, "Cool": cool},
        ["Hot"],
        "Cool",
        minimum_fractional_excess=0.02,
    )

    assert study.assessment.status == "detected"
    assert study.assessment.empirical_onset_angstrom == pytest.approx(6950.0, abs=120.0)
    assert study.assessment.formal_change_sign == "positive"


def test_wrong_sign_change_is_rejected_and_products_round_trip(tmp_path: Path) -> None:
    wavelength = np.linspace(5000.0, 7600.0, 2601)
    base = np.ones(wavelength.size)
    ramp = np.clip((wavelength - 6700.0) / 250.0, 0.0, 1.0)
    hot = _response(
        tmp_path,
        "Hot",
        np.full(wavelength.size, 2.0),
        base * (1 - 0.10 * ramp),
        wavelength,
        blue_to_red_ratio=2.0,
    )
    cool = _response(
        tmp_path,
        "Cool",
        np.full(wavelength.size, 1.0),
        base,
        wavelength,
        blue_to_red_ratio=0.5,
    )

    study = assess_second_order({"Hot": hot, "Cool": cool}, ["Hot"], "Cool")
    products = write_second_order_products(study, tmp_path / "diagnostic")
    restored = load_second_order_assessment(products["json"])

    assert restored.status == "undetermined"
    assert restored.empirical_onset_angstrom is None
    assert restored.formal_change_sign == "negative"
    assert products["csv"].is_file()
    assert products["png"].is_file()
