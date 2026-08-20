from pathlib import Path

from astropy.io import fits
import numpy as np

from uvex_reduce.inspector import inspect_file
from uvex_reduce.models import FrameType


def _write(path: Path, *, image_type: str, object_name: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    header = fits.Header()
    header["IMAGETYP"] = image_type
    header["OBJECT"] = object_name
    header["EXPTIME"] = 10.0
    header["GAIN"] = 100
    fits.PrimaryHDU(np.zeros((12, 20), dtype=np.uint16), header).writeto(path)


def test_led_filename_overrides_stale_light_header(tmp_path: Path) -> None:
    path = tmp_path / "26.2.18" / "LED-4.fit"
    _write(path, image_type="Light Frame", object_name="Regulus")

    record = inspect_file(path, tmp_path)

    assert record.frame_type is FrameType.FLAT
    assert record.confidence == 0.82
    assert "continuum/flat" in record.classification_reason


def test_filename_object_conflict_is_reported(tmp_path: Path) -> None:
    path = tmp_path / "26.2.17" / "castor-1.fit"
    _write(path, image_type="Light Frame", object_name="Jupiter")

    record = inspect_file(path, tmp_path)

    assert record.frame_type is FrameType.LIGHT
    assert record.inferred_object == "castor"
    assert any("conflicts" in warning for warning in record.warnings)


def test_saved_fits_gain_takes_precedence_over_true_adc_gain(tmp_path: Path) -> None:
    path = tmp_path / "SharpCap" / "Vega_00001.fits"
    _write(path, image_type="Light", object_name="Vega")
    with fits.open(path, mode="update") as hdul:
        hdul[0].header["EGAIN"] = 1.0098
        hdul[0].header["EGAINSAV"] = 0.06311

    record = inspect_file(path, tmp_path)

    assert record.gain == 0.06311
