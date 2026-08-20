from uvex_reduce.pipeline import (
    _cosmic_ray_replacements_from_warnings,
    _sdk_wrap_repairs_from_warnings,
)


def test_sdk_wrap_history_is_structured_for_configured_and_automatic_repairs() -> None:
    repairs = _sdk_wrap_repairs_from_warnings(
        [
            "first.fit: applied documented ATR585M SDK wrap repair "
            "(cyclic x shift -64 px, direction=left); original FITS was not modified.",
            "second.fit: automatically detected ATR585M SDK x-wrap in the science group "
            "(seam score 42.0 sigma) and applied cyclic x shift -64 px in memory; "
            "original FITS was not modified.",
            "Unrelated warning.",
        ]
    )

    assert repairs == [
        {
            "file": "first.fit",
            "appliedShiftPixels": -64,
            "direction": "left",
            "detection": "configured",
            "sourceFitsModified": False,
        },
        {
            "file": "second.fit",
            "appliedShiftPixels": -64,
            "direction": "left",
            "detection": "automatic",
            "sourceFitsModified": False,
        },
    ]


def test_cosmic_ray_replacement_history_is_structured() -> None:
    replacements = _cosmic_ray_replacements_from_warnings(
        [
            "first.fit: replaced 30 cosmic-ray candidate pixels.",
            "Unrelated warning.",
            "second.fit: replaced 799 cosmic-ray candidate pixels.",
        ]
    )

    assert replacements == [
        {"file": "first.fit", "replacedPixelCount": 30},
        {"file": "second.fit", "replacedPixelCount": 799},
    ]
