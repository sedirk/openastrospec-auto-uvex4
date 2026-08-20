from __future__ import annotations

import numpy as np
import pytest

from uvex_reduce.image_viewer import make_neutral_fits_preview


def test_fits_preview_is_neutral_grayscale() -> None:
    data = np.arange(240 * 320, dtype=np.float32).reshape(240, 320)
    data[12, 14] = np.nan
    data[18, 20] = np.inf

    preview = make_neutral_fits_preview(data)

    rgb = np.asarray(preview.image)
    assert preview.original_shape == (240, 320)
    assert preview.display_shape == (240, 320)
    assert rgb.shape == (240, 320, 3)
    np.testing.assert_array_equal(rgb[..., 0], rgb[..., 1])
    np.testing.assert_array_equal(rgb[..., 1], rgb[..., 2])
    assert preview.low < preview.high


def test_fits_preview_downsamples_only_for_display() -> None:
    data = np.linspace(0, 1, 800 * 400, dtype=np.float32).reshape(400, 800)

    preview = make_neutral_fits_preview(data, maximum_dimension=256)

    assert preview.original_shape == (400, 800)
    assert preview.display_shape == (128, 256)


@pytest.mark.parametrize(
    "data",
    [
        np.ones((10, 10, 2), dtype=np.float32),
        np.ones((20, 20), dtype=np.float32),
    ],
)
def test_fits_preview_rejects_unsupported_or_flat_data(data: np.ndarray) -> None:
    with pytest.raises(ValueError):
        make_neutral_fits_preview(data)
