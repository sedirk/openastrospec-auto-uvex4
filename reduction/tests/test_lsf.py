import numpy as np
import pytest

from uvex_reduce.lsf import (
    ReplicaKernelAnchor,
    add_left_shifted_replica,
    remove_left_shifted_replica,
    remove_wavelength_dependent_left_replica,
)


def test_shifted_replica_round_trip_recovers_isolated_lines() -> None:
    pixel = np.arange(600, dtype=float)
    truth = 20.0 + 80.0 * np.exp(-0.5 * ((pixel - 220.0) / 4.0) ** 2)
    truth += 35.0 * np.exp(-0.5 * ((pixel - 410.0) / 7.0) ** 2)
    observed = add_left_shifted_replica(truth, 10.4, 0.42)

    restored, _ = remove_left_shifted_replica(observed, 10.4, 0.42)

    np.testing.assert_allclose(restored[30:-80], truth[30:-80], rtol=2e-5, atol=2e-3)
    assert np.trapz(restored[30:-80]) == pytest.approx(
        np.trapz(truth[30:-80]),
        rel=2e-5,
    )


def test_uncertainty_is_propagated_and_increases() -> None:
    observed = np.ones(200)
    uncertainty = np.full(200, 2.0)

    _, corrected_uncertainty = remove_left_shifted_replica(
        observed,
        8.0,
        0.35,
        uncertainty,
    )

    assert corrected_uncertainty is not None
    assert np.nanmedian(corrected_uncertainty[20:-80]) > 2.0


def test_broadened_replica_round_trip() -> None:
    pixel = np.arange(500, dtype=float)
    truth = 10.0 + 50.0 * np.exp(-0.5 * ((pixel - 240.0) / 4.0) ** 2)
    observed = add_left_shifted_replica(
        truth,
        9.7,
        0.4,
        secondary_blur_sigma_pixels=2.5,
    )

    restored, _ = remove_left_shifted_replica(
        observed,
        9.7,
        0.4,
        secondary_blur_sigma_pixels=2.5,
    )

    np.testing.assert_allclose(restored[40:-100], truth[40:-100], rtol=3e-5, atol=3e-3)


def test_wavelength_dependent_kernel_blends_without_a_seam() -> None:
    observed = np.linspace(2.0, 3.0, 300)
    anchors = [
        ReplicaKernelAnchor(50.0, 11.0, 0.45),
        ReplicaKernelAnchor(250.0, 6.0, 0.10),
    ]

    result = remove_wavelength_dependent_left_replica(observed, anchors)

    assert result.corrected.shape == observed.shape
    assert result.offset_pixels[150] == pytest.approx(8.5, abs=0.03)
    assert result.secondary_to_primary[150] == pytest.approx(0.275, abs=0.003)
    assert np.all(np.isfinite(result.corrected))
    assert result.corrected[50] != 0.0
    assert result.corrected[250] != 0.0
    assert abs(np.diff(result.corrected)[149] - np.diff(result.corrected)[150]) < 0.02
