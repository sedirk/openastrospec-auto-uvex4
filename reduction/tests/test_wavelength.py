import numpy as np
import pytest

from uvex_reduce.config import WavelengthConfig
from uvex_reduce.wavelength import _fit_known_pairs


def test_known_pairs_make_strictly_increasing_axis() -> None:
    options = WavelengthConfig(
        mode="known_pairs",
        polynomial_degree=2,
        known_pixels=[100.0, 500.0, 900.0, 1300.0],
        known_angstroms=[4100.0, 4702.0, 5352.0, 6050.0],
        medium="air",
    )

    solution = _fit_known_pairs(1600, options)

    assert np.all(np.diff(solution.wavelength_angstrom) > 0)
    assert solution.coefficient_order == "descending"
    assert solution.medium == "air"


def test_reversed_anchor_solution_is_rejected() -> None:
    options = WavelengthConfig(
        mode="known_pairs",
        polynomial_degree=1,
        known_pixels=[100.0, 900.0, 1500.0, 1900.0],
        known_angstroms=[7000.0, 6000.0, 5000.0, 4000.0],
    )

    with pytest.raises(ValueError, match="strictly increasing"):
        _fit_known_pairs(2000, options)
