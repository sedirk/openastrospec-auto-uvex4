from __future__ import annotations

from pathlib import Path

import numpy as np

from uvex_reduce.config import load_config


CONFIG_DIRECTORY = Path(__file__).resolve().parents[1] / "configs"


def test_all_canonical_albireo_configs_preserve_correct_detector_orientation() -> None:
    expected = {
        "20260504-albireo-a-final.toml": ([708.7, 2508.6], [4861.35, 6562.79]),
        "20260504-albireo-b-final.toml": (
            [168.5, 712.2, 2514.2],
            [4340.47, 4861.35, 6562.79],
        ),
        "20260504-albireo-a-clean.toml": ([708.7, 2508.6], [4861.35, 6562.79]),
        "20260504-albireo-b-clean.toml": (
            [168.5, 712.2, 2514.2],
            [4340.47, 4861.35, 6562.79],
        ),
    }

    assert {path.name for path in CONFIG_DIRECTORY.glob("20260504-albireo-*.toml")} == set(
        expected
    )
    for filename, (pixels, wavelengths) in expected.items():
        config = load_config(CONFIG_DIRECTORY / filename)
        assert config.orientation.red_left_blue_right is True
        assert config.orientation.horizontal_flip is True
        assert config.wavelength.mode == "known_pairs"
        assert config.wavelength.known_pixels == pixels
        assert config.wavelength.known_angstroms == wavelengths
        assert np.all(np.diff(config.wavelength.known_pixels) > 0)
        assert np.all(np.diff(config.wavelength.known_angstroms) > 0)


def test_albireo_raw_and_flipped_anchor_pixels_are_consistent() -> None:
    detector_last_pixel = 3839.0
    expected_raw = {
        "20260504-albireo-a-final.toml": [3130.3, 1330.4],
        "20260504-albireo-b-final.toml": [3670.5, 3126.8, 1324.8],
    }

    for filename, raw_pixels in expected_raw.items():
        config = load_config(CONFIG_DIRECTORY / filename)
        recovered_raw = [
            detector_last_pixel - pixel for pixel in config.wavelength.known_pixels
        ]
        assert np.allclose(recovered_raw, raw_pixels, atol=1e-9)
