import numpy as np
import pytest

from uvex_reduce.config import ExtractionConfig
from uvex_reduce.extraction import (
    TraceDetectionError,
    _extraction_quality_issue,
    trace_spectrum,
)


def _synthetic_trace(height: int = 140, width: int = 320):
    rng = np.random.default_rng(14)
    x = np.arange(width, dtype=float)
    y = np.arange(height, dtype=float)[:, None]
    truth = 54.0 + 0.012 * x + 0.00002 * (x - width / 2.0) ** 2
    continuum = 800.0 + 250.0 * np.sin(x / 90.0)
    image = 120.0 + continuum * np.exp(-0.5 * ((y - truth) / 7.5) ** 2)
    image += rng.normal(0.0, 8.0, image.shape)
    return image.astype(np.float32), truth


def test_broad_off_center_trace_is_recovered() -> None:
    image, truth = _synthetic_trace()
    options = ExtractionConfig(
        backend="native",
        trace_bins=20,
        trace_half_width=35,
        minimum_trace_snr=2.0,
        aperture_half_width=18,
    )

    trace = trace_spectrum(image, np.zeros_like(image, dtype=bool), options)

    assert trace.valid_bins >= 15
    assert np.sqrt(np.mean((trace.centers - truth) ** 2)) < 1.5
    assert 4.0 < np.median(trace.sigma_pixels) < 15.0


def test_featureless_frame_stops_instead_of_inventing_trace() -> None:
    image = np.zeros((100, 200), dtype=float)
    options = ExtractionConfig(backend="native", minimum_trace_snr=3.0)

    with pytest.raises(TraceDetectionError):
        trace_spectrum(image, np.zeros_like(image, dtype=bool), options)


def test_noisy_optimal_result_is_rejected_against_boxcar_check() -> None:
    rng = np.random.default_rng(5)
    x = np.arange(800, dtype=float)
    reference = 1_000.0 + 300.0 * np.sin(x / 200.0)
    noisy = reference + rng.normal(0.0, 100.0, x.size)
    options = ExtractionConfig(backend="aspired")

    issue = _extraction_quality_issue(
        noisy,
        np.full(x.size, 10.0),
        np.zeros(x.size, dtype=bool),
        reference,
        np.zeros(x.size, dtype=bool),
        options,
    )

    assert issue is not None
    assert "high-frequency noise" in issue
