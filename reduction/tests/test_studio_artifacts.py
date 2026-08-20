from pathlib import Path

from uvex_reduce.studio import artifact_is_quarantined


def test_invalid_legacy_artifacts_are_hidden() -> None:
    root = Path("output")
    assert artifact_is_quarantined(
        root / "final-invalid-legacy-do-not-use" / "old.fits",
        root,
    )
    assert artifact_is_quarantined(
        root / "segments" / "first-only-invalid-auto-detect-do-not-use" / "old.png",
        root,
    )
    assert not artifact_is_quarantined(
        root / "same-session-ngc6543" / "final" / "current.fits",
        root,
    )
