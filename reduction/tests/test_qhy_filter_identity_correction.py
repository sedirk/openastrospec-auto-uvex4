from __future__ import annotations

import importlib.util
from pathlib import Path

import numpy as np
from astropy.io import fits


TOOL_PATH = (
    Path(__file__).parents[1] / "tools" / "qhy_filter_identity_correction.py"
)
SPEC = importlib.util.spec_from_file_location("qhy_filter_identity_correction", TOOL_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def write_frame(path: Path, filter_name: str, camera_id: str = "QHYminiCam8M-test") -> None:
    header = fits.Header()
    header["INSTRUME"] = "QHYminiCam8M"
    header["CAMERAID"] = camera_id
    header["FILTER"] = filter_name
    header["EXPTIME"] = 3.0
    header["IMAGETYP"] = "LIGHT"
    fits.writeto(path, np.zeros((4, 6), dtype=np.uint16), header, overwrite=False)


def test_mapping_covers_named_and_legacy_slot_labels() -> None:
    assert MODULE.corrected_filter_label("S") == "U"
    assert MODULE.corrected_filter_label("H") == "O"
    assert MODULE.corrected_filter_label("O") == "H"
    assert MODULE.corrected_filter_label("U") == "S"
    assert MODULE.corrected_filter_label("G") == "Z"
    assert MODULE.corrected_filter_label("R") == "I"
    assert MODULE.corrected_filter_label("I") == "R"
    assert MODULE.corrected_filter_label("Z") == "G"
    assert MODULE.corrected_filter_label("Slot 5") == "I"
    assert MODULE.corrected_filter_label("Slot 0") == "U"
    assert MODULE.corrected_filter_label("Slot 1") == "O"
    assert MODULE.corrected_filter_label("Slot 2") == "H"
    assert MODULE.corrected_filter_label("Slot 3") == "S"


def test_manifest_is_sidecar_only_and_deduplicates_by_hash(tmp_path: Path) -> None:
    first = tmp_path / "first.fits"
    duplicate = tmp_path / "duplicate.fits"
    narrowband = tmp_path / "h.fits"
    write_frame(first, "R")
    duplicate.write_bytes(first.read_bytes())
    write_frame(narrowband, "H")
    before = first.read_bytes()

    manifest = MODULE.build_manifest([tmp_path], stable_id="QHYminiCam8M-test")

    assert manifest["policy"]["rawInputsImmutable"] is True
    assert manifest["summary"]["affectedPathEntries"] == 3
    assert manifest["summary"]["uniqueAffectedFramesBySha256"] == 2
    assert {entry["physicalFilter"] for entry in manifest["entries"]} == {"I", "O"}
    assert first.read_bytes() == before
