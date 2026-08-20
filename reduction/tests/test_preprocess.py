import numpy as np
import pytest
from astropy.io import fits
from scipy.ndimage import shift

from uvex_reduce.config import DetectorConfig, OrientationConfig, PreprocessConfig
from uvex_reduce.preprocess import (
    _auto_repair_sdk_wrap_group,
    _read_frame_with_sdk_fix,
    _normalised_gaussian_filter,
    _shift_masked_image,
    estimate_profile_shift,
    estimate_trace_peak_shift,
    flip_horizontal,
    preprocess_science,
    read_frame,
)


def test_sdk_wrap_heuristic_is_disabled_by_default() -> None:
    assert PreprocessConfig().auto_detect_sdk_wrap is False


def test_horizontal_flip_keeps_image_variance_and_mask_registered() -> None:
    image = np.arange(12).reshape(3, 4)
    variance = image + 100
    mask = image % 3 == 0

    flipped_image, flipped_variance, flipped_mask = flip_horizontal(image, variance, mask)

    np.testing.assert_array_equal(flipped_image, image[:, ::-1])
    np.testing.assert_array_equal(flipped_variance, variance[:, ::-1])
    np.testing.assert_array_equal(flipped_mask, mask[:, ::-1])


def test_subpixel_profile_shift_has_correct_alignment_sign() -> None:
    pixels = np.arange(200, dtype=float)
    reference = np.exp(-0.5 * ((pixels - 80.0) / 5.0) ** 2)
    target = shift(reference, 2.4, order=3, mode="constant", cval=0.0)

    applied_shift, confidence = estimate_profile_shift(reference, target, 10.0)

    assert applied_shift == pytest.approx(-2.4, abs=0.15)
    assert confidence > 0.9


def test_significant_trace_peak_wins_over_fixed_spatial_pattern() -> None:
    pixels = np.arange(300, dtype=float)
    fixed = 3.0 * np.sin(pixels / 9.0) + 2.0 * np.cos(pixels / 17.0)
    reference = fixed + 40.0 * np.exp(-0.5 * ((pixels - 220.0) / 3.0) ** 2)
    target = fixed + 40.0 * np.exp(-0.5 * ((pixels - 70.0) / 3.0) ** 2)

    applied_shift, confidence = estimate_trace_peak_shift(reference, target, 200.0)

    assert applied_shift == pytest.approx(150.0, abs=0.2)
    assert confidence >= 0.5


def test_nan_aware_flat_smoothing_does_not_spread_single_nan() -> None:
    image = np.ones((80, 120), dtype=float)
    image[40, 60] = np.nan

    smooth = _normalised_gaussian_filter(image, sigma=(8.0, 12.0))

    assert np.isfinite(smooth).all()
    np.testing.assert_allclose(smooth, 1.0, atol=1e-6)


def test_masked_shift_marks_interpolation_footprint() -> None:
    image = np.ones((30, 40), dtype=float)
    image[15, 20] = 1_000_000.0
    mask = np.zeros_like(image, dtype=bool)
    mask[15, 20] = True

    shifted, shifted_mask = _shift_masked_image(image, mask, 0.25, -0.4)

    assert np.nanmax(shifted) < 2.0
    assert shifted_mask.sum() > 1


def test_sdk_wrap_fix_is_explicit_and_does_not_modify_source(tmp_path) -> None:
    path = tmp_path / "affected.fit"
    original = np.arange(5 * 100, dtype=np.uint16).reshape(5, 100)
    fits.PrimaryHDU(original).writeto(path)
    options = PreprocessConfig(
        sdk_wrap_fix_files=["affected.fit"],
        sdk_wrap_shift_pixels=64,
    )
    warnings: list[str] = []

    repaired = _read_frame_with_sdk_fix(path, options, warnings)

    np.testing.assert_array_equal(repaired.data, np.roll(original, -64, axis=1))
    np.testing.assert_array_equal(fits.getdata(path), original)
    assert any("original FITS was not modified" in warning for warning in warnings)


def test_sdk_wrap_fix_ignores_unlisted_frame(tmp_path) -> None:
    path = tmp_path / "normal.fit"
    original = np.arange(4 * 80, dtype=np.uint16).reshape(4, 80)
    fits.PrimaryHDU(original).writeto(path)

    loaded = _read_frame_with_sdk_fix(
        path,
        PreprocessConfig(sdk_wrap_fix_files=["different.fit"]),
        [],
    )

    np.testing.assert_array_equal(loaded.data, original)


def test_sdk_wrap_explicit_right_direction_is_supported(tmp_path) -> None:
    path = tmp_path / "opposite-direction.fit"
    original = np.arange(5 * 100, dtype=np.uint16).reshape(5, 100)
    fits.PrimaryHDU(original).writeto(path)
    warnings: list[str] = []

    repaired = _read_frame_with_sdk_fix(
        path,
        PreprocessConfig(
            sdk_wrap_fix_files=[path.name],
            sdk_wrap_shift_pixels=64,
            sdk_wrap_fix_direction="right",
        ),
        warnings,
    )

    np.testing.assert_array_equal(repaired.data, np.roll(original, 64, axis=1))
    assert any("direction=right" in warning for warning in warnings)


def test_sdk_wrap_is_auto_detected_from_column_64_seam_with_group_anchor(tmp_path) -> None:
    width, height = 256, 80
    x = np.arange(width, dtype=float)
    spectrum = 500.0 + 2.0 * x + 300.0 * np.exp(-0.5 * ((x - 150.0) / 12.0) ** 2)
    spatial = np.exp(-0.5 * ((np.arange(height) - 37.0) / 3.0) ** 2)
    original = 200.0 + 20.0 * spatial[:, None] * spectrum[None, :]
    wrapped = np.roll(original, 64, axis=1).astype(np.uint16)
    anchor_path = tmp_path / "anchor.fit"
    path = tmp_path / "wrapped.fit"
    header = fits.Header({"INSTRUME": "ATR585M", "EXPTIME": 10.0, "GAIN": 100})
    fits.PrimaryHDU(original.astype(np.uint16), header=header).writeto(anchor_path)
    fits.PrimaryHDU(wrapped, header=header).writeto(path)
    anchor = read_frame(anchor_path)
    frame = read_frame(path)
    warnings: list[str] = []

    _auto_repair_sdk_wrap_group(
        [anchor, frame],
        PreprocessConfig(auto_detect_sdk_wrap=True, sdk_wrap_seam_sigma=4.0),
        warnings,
        "science",
    )

    np.testing.assert_array_equal(anchor.data, original.astype(np.uint16))
    np.testing.assert_array_equal(frame.data, original.astype(np.uint16))
    assert frame.header["SDKWRAP"] is True
    assert any("automatically detected" in warning for warning in warnings)


def test_sdk_wrap_single_frame_requires_explicit_selection(tmp_path) -> None:
    width, height = 256, 80
    x = np.arange(width, dtype=float)
    spectrum = 500.0 + 2.0 * x + 300.0 * np.exp(-0.5 * ((x - 150.0) / 12.0) ** 2)
    spatial = np.exp(-0.5 * ((np.arange(height) - 37.0) / 3.0) ** 2)
    original = 200.0 + 20.0 * spatial[:, None] * spectrum[None, :]
    wrapped = np.roll(original, 64, axis=1).astype(np.uint16)
    path = tmp_path / "single.fit"
    header = fits.Header({"INSTRUME": "ATR585M", "EXPTIME": 10.0, "GAIN": 100})
    fits.PrimaryHDU(wrapped, header=header).writeto(path)
    frame = read_frame(path)
    warnings: list[str] = []

    _auto_repair_sdk_wrap_group(
        [frame],
        PreprocessConfig(auto_detect_sdk_wrap=True, sdk_wrap_seam_sigma=4.0),
        warnings,
        "science",
    )

    np.testing.assert_array_equal(frame.data, wrapped)
    assert "SDKWRAP" not in frame.header
    assert any("single-frame" in warning for warning in warnings)


def test_mixed_exposures_are_rate_normalised_before_stack(tmp_path) -> None:
    paths = []
    rate = np.full((32, 48), 20.0)
    for index, exposure in enumerate((10.0, 20.0)):
        path = tmp_path / f"science-{index}.fit"
        header = fits.Header(
            {
                "INSTRUME": "ATR585M",
                "EXPTIME": exposure,
                "GAIN": 100,
                "XBINNING": 1,
                "YBINNING": 1,
            }
        )
        fits.PrimaryHDU((rate * exposure).astype(np.uint16), header=header).writeto(path)
        paths.append(path)

    stack = preprocess_science(
        paths,
        [],
        [],
        [],
        DetectorConfig(),
        PreprocessConfig(
            align_frames=False,
            use_bias=False,
            use_dark=False,
            use_flat=False,
        ),
        OrientationConfig(red_left_blue_right=False, horizontal_flip=False),
    )

    np.testing.assert_allclose(stack.image, rate * 15.0)
    assert stack.header["EXPTIME"] == 15.0
    assert stack.header["TOTEXP"] == 30.0
    assert stack.header["EXPNORM"] is True


def test_sigma_clipped_mean_is_available_after_per_frame_cleaning(tmp_path) -> None:
    paths = []
    for index, value in enumerate((0, 0, 9)):
        path = tmp_path / f"science-{index}.fit"
        header = fits.Header(
            {
                "INSTRUME": "ATR585M",
                "EXPTIME": 10.0,
                "GAIN": 100,
            }
        )
        fits.PrimaryHDU(
            np.full((20, 30), value, dtype=np.uint16),
            header=header,
        ).writeto(path)
        paths.append(path)

    stack = preprocess_science(
        paths,
        [],
        [],
        [],
        DetectorConfig(),
        PreprocessConfig(
            align_frames=False,
            sigma_clip=100.0,
            combine_method="mean",
            use_bias=False,
            use_dark=False,
            use_flat=False,
        ),
        OrientationConfig(red_left_blue_right=False, horizontal_flip=False),
    )

    np.testing.assert_allclose(stack.image, 3.0)
    assert stack.header["COMBMETH"] == "MEAN"
    assert stack.header["CRREJECT"] is False


def test_temporal_sigma_clip_rejects_a_large_transient(tmp_path) -> None:
    paths = []
    for index, value in enumerate((98, 100, 102, 99, 101)):
        path = tmp_path / f"transient-{index}.fit"
        data = np.full((20, 30), value, dtype=np.uint16)
        if index == 4:
            data[10, 15] = 10_000
        fits.PrimaryHDU(
            data,
            header=fits.Header({"INSTRUME": "ATR585M", "EXPTIME": 10.0, "GAIN": 100}),
        ).writeto(path)
        paths.append(path)

    stack = preprocess_science(
        paths,
        [],
        [],
        [],
        DetectorConfig(),
        PreprocessConfig(
            align_frames=False,
            sigma_clip=4.5,
            combine_method="mean",
            use_bias=False,
            use_dark=False,
            use_flat=False,
        ),
        OrientationConfig(red_left_blue_right=False, horizontal_flip=False),
    )

    assert stack.image[10, 15] == pytest.approx(99.75)
    assert stack.header["TCRSAMP"] >= 1


def test_mixed_camera_gain_is_rejected(tmp_path) -> None:
    paths = []
    for index, gain in enumerate((100, 300)):
        path = tmp_path / f"gain-{gain}.fit"
        header = fits.Header({"INSTRUME": "ATR585M", "EXPTIME": 10.0, "GAIN": gain})
        fits.PrimaryHDU(np.ones((20, 30), dtype=np.uint16), header=header).writeto(path)
        paths.append(path)

    with pytest.raises(ValueError, match="mixed camera GAIN"):
        preprocess_science(
            paths,
            [],
            [],
            [],
            DetectorConfig(),
            PreprocessConfig(use_bias=False, use_dark=False, use_flat=False),
            OrientationConfig(red_left_blue_right=False, horizontal_flip=False),
        )


def test_gain_mismatched_flat_is_rejected_and_recorded(tmp_path) -> None:
    science_path = tmp_path / "science.fit"
    flat_path = tmp_path / "flat.fit"
    science_header = fits.Header(
        {
            "INSTRUME": "ATR585M",
            "EXPTIME": 10.0,
            "GAIN": 100,
            "XBINNING": 1,
            "YBINNING": 1,
        }
    )
    flat_header = science_header.copy()
    flat_header["GAIN"] = 10_000
    fits.PrimaryHDU(
        np.full((40, 60), 500, dtype=np.uint16),
        header=science_header,
    ).writeto(science_path)
    fits.PrimaryHDU(
        np.full((40, 60), 20_000, dtype=np.uint16),
        header=flat_header,
    ).writeto(flat_path)

    stack = preprocess_science(
        [science_path],
        [],
        [],
        [flat_path],
        DetectorConfig(),
        PreprocessConfig(
            align_frames=False,
            use_bias=False,
            use_dark=False,
            use_flat=True,
        ),
        OrientationConfig(red_left_blue_right=False, horizontal_flip=False),
    )

    assert stack.header["FLATCOR"] is False
    assert any("calibration-incompatible flat" in warning for warning in stack.warnings)


def test_cross_night_dark_is_accepted_when_detector_settings_match(tmp_path) -> None:
    science_path = tmp_path / "science-20260504.fit"
    dark_path = tmp_path / "dark-20260401.fit"
    common = {
        "INSTRUME": "ATR585M",
        "EXPTIME": 600.0,
        "GAIN": 100,
        "CCD-TEMP": -10.0,
        "XBINNING": 1,
        "YBINNING": 1,
    }
    fits.PrimaryHDU(
        np.full((30, 40), 100, dtype=np.uint16),
        header=fits.Header(common),
    ).writeto(science_path)
    fits.PrimaryHDU(
        np.full((30, 40), 10, dtype=np.uint16),
        header=fits.Header({**common, "DATE-OBS": "2026-04-01T00:00:00"}),
    ).writeto(dark_path)

    stack = preprocess_science(
        [science_path],
        [],
        [dark_path],
        [],
        DetectorConfig(),
        PreprocessConfig(
            align_frames=False,
            use_bias=False,
            use_dark=True,
            use_flat=False,
        ),
        OrientationConfig(red_left_blue_right=False, horizontal_flip=False),
    )

    assert stack.header["DARKCOR"] is True
    np.testing.assert_allclose(stack.image, 90.0)


def test_gain_mismatched_dark_is_rejected_and_recorded(tmp_path) -> None:
    science_path = tmp_path / "science.fit"
    dark_path = tmp_path / "dark.fit"
    science_header = fits.Header(
        {
            "INSTRUME": "ATR585M",
            "EXPTIME": 600.0,
            "GAIN": 100,
            "CCD-TEMP": -10.0,
            "XBINNING": 1,
            "YBINNING": 1,
        }
    )
    dark_header = science_header.copy()
    dark_header["GAIN"] = 300
    fits.PrimaryHDU(
        np.full((30, 40), 100, dtype=np.uint16),
        header=science_header,
    ).writeto(science_path)
    fits.PrimaryHDU(
        np.full((30, 40), 10, dtype=np.uint16),
        header=dark_header,
    ).writeto(dark_path)

    stack = preprocess_science(
        [science_path],
        [],
        [dark_path],
        [],
        DetectorConfig(),
        PreprocessConfig(
            align_frames=False,
            use_bias=False,
            use_dark=True,
            use_flat=False,
        ),
        OrientationConfig(red_left_blue_right=False, horizontal_flip=False),
    )

    assert stack.header["DARKCOR"] is False
    assert any("calibration-incompatible dark" in warning for warning in stack.warnings)
