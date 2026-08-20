from __future__ import annotations

from collections import Counter
import csv
import json
from pathlib import Path
import re
from typing import Iterable

from astropy.io import fits

from .models import FrameRecord, FrameType


FITS_SUFFIXES = {".fit", ".fits", ".fts"}
IGNORED_DIRECTORY_NAMES = {"isis_6_1_1", "__macosx"}
INVALID_OBJECT_NAMES = {"", "na", "n/a", "none", "unknown", "unnamed", "object"}


def discover_fits(root: str | Path) -> list[Path]:
    root_path = Path(root).expanduser().resolve()
    if not root_path.is_dir():
        raise NotADirectoryError(root_path)
    return sorted(
        (
            path
            for path in root_path.rglob("*")
            if path.is_file()
            and path.suffix.lower() in FITS_SUFFIXES
            and not any(part.lower() in IGNORED_DIRECTORY_NAMES for part in path.parts)
        ),
        key=lambda path: str(path).lower(),
    )


def inspect_directory(root: str | Path) -> list[FrameRecord]:
    root_path = Path(root).expanduser().resolve()
    return [inspect_file(path, root_path) for path in discover_fits(root_path)]


def inspect_file(path: str | Path, root: str | Path | None = None) -> FrameRecord:
    file_path = Path(path).expanduser().resolve()
    relative = _relative_display(file_path, Path(root).resolve() if root else file_path.parent)
    try:
        header = fits.getheader(file_path, ext=0, memmap=True)
    except Exception as error:
        return FrameRecord(
            path=file_path,
            relative_path=relative,
            frame_type=FrameType.UNKNOWN,
            confidence=0.0,
            classification_reason=f"FITS header unreadable: {error}",
            warnings=["The file was not opened as a valid primary-HDU FITS image."],
        )

    frame_type, confidence, reason = classify_frame(file_path, header)
    object_name = _clean_text(_first(header, "OBJECT", "OBJNAME", "TARGET"))
    inferred_object = infer_object_from_filename(file_path)
    warnings: list[str] = []
    if object_name and object_name.lower() in INVALID_OBJECT_NAMES:
        object_name = None
    if object_name and inferred_object and _normalise_name(object_name) != _normalise_name(inferred_object):
        warnings.append(f"Header OBJECT={object_name!r} conflicts with filename target {inferred_object!r}.")
    if frame_type in {FrameType.LIGHT, FrameType.FLAT, FrameType.DARK, FrameType.ARC} and _as_float(
        _first(header, "EXPTIME", "EXPOSURE", "EXP_TIME")
    ) is None:
        warnings.append("Exposure time is missing.")
    bitpix = _as_int(header.get("BITPIX"))
    if bitpix not in {16, -32, -64}:
        warnings.append(f"Unusual BITPIX={bitpix}; treat this as a preview/master until verified.")

    stem = file_path.stem.lower()
    is_master = stem in {"dark", "offset", "bias", "flat", "masterdark", "masterbias", "masterflat"}
    return FrameRecord(
        path=file_path,
        relative_path=relative,
        frame_type=frame_type,
        confidence=confidence,
        classification_reason=reason,
        object_name=object_name,
        inferred_object=inferred_object,
        exposure_s=_as_float(_first(header, "EXPTIME", "EXPOSURE", "EXP_TIME")),
        # SharpCap can save a lower-bit-depth camera ADC in a 16-bit FITS
        # container.  In that case EGAIN describes a *true ADC* count while
        # EGAINSAV describes the pixel values actually stored in this file.
        # Prefer the saved-value scale so Inspector output can be copied into
        # DetectorConfig without overstating Poisson noise by the bit shift.
        gain=_as_float(_first(header, "EGAINSAV", "EGAIN", "GAIN", "CCDGAIN")),
        date_obs=_clean_text(_first(header, "DATE-OBS", "DATEOBS")),
        instrument=_clean_text(_first(header, "INSTRUME", "CAMERA")),
        temperature_c=_as_float(_first(header, "CCD-TEMP", "CCD_TEMP", "SENSOR_TEMP")),
        width=_as_int(header.get("NAXIS1")),
        height=_as_int(header.get("NAXIS2")),
        bitpix=bitpix,
        is_master=is_master,
        warnings=warnings,
    )


def classify_frame(path: Path, header: fits.Header) -> tuple[FrameType, float, str]:
    tokens = _path_tokens(path)
    image_type = (_clean_text(_first(header, "IMAGETYP", "IMAGETYPE", "FRAME", "OBSTYPE")) or "").lower()

    if tokens & {"bias", "offset", "zero"}:
        return FrameType.BIAS, 0.99, "directory/filename contains bias or offset"
    if tokens & {"dark"}:
        return FrameType.DARK, 0.99, "directory/filename contains dark"
    if tokens & {"flat", "tungsten", "halogen", "continuum", "led"}:
        confidence = 0.82 if "led" in tokens else 0.97
        return FrameType.FLAT, confidence, "directory/filename identifies continuum/flat illumination"
    if tokens & {"arc", "neon", "argon", "thar", "relco", "hg", "calarc"}:
        return FrameType.ARC, 0.97, "directory/filename identifies an arc lamp"

    if any(token in image_type for token in ("bias", "offset", "zero")):
        return FrameType.BIAS, 0.95, f"IMAGETYP={image_type!r}"
    if "dark" in image_type:
        return FrameType.DARK, 0.95, f"IMAGETYP={image_type!r}"
    if "flat" in image_type:
        return FrameType.FLAT, 0.95, f"IMAGETYP={image_type!r}"
    if any(token in image_type for token in ("arc", "lamp", "comparison")):
        return FrameType.ARC, 0.9, f"IMAGETYP={image_type!r}"
    if any(token in image_type for token in ("light", "science", "object")):
        return FrameType.LIGHT, 0.8, f"IMAGETYP={image_type!r}"
    return FrameType.UNKNOWN, 0.2, "no reliable path or FITS-header classification signal"


def infer_object_from_filename(path: Path) -> str | None:
    stem = path.stem
    if re.fullmatch(r"\d{12,14}", stem):
        parent = path.parent.name
        return parent if not re.fullmatch(r"\d+(?:\.\d+)+", parent) else None
    prefix = re.sub(r"[-_ ]?\d+$", "", stem).strip("-_ ")
    if not prefix or _normalise_name(prefix) in {
        "led",
        "flat",
        "dark",
        "offset",
        "bias",
        "arc",
    }:
        return None
    return prefix


def write_manifest(records: Iterable[FrameRecord], output_prefix: str | Path) -> tuple[Path, Path]:
    prefix = Path(output_prefix).expanduser().resolve()
    prefix.parent.mkdir(parents=True, exist_ok=True)
    json_path = prefix.with_suffix(".json")
    csv_path = prefix.with_suffix(".csv")
    rows = [record.to_dict() for record in records]
    json_path.write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
    fieldnames = list(rows[0]) if rows else list(FrameRecord.__dataclass_fields__)
    with csv_path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            csv_row = dict(row)
            csv_row["warnings"] = " | ".join(csv_row["warnings"])
            writer.writerow(csv_row)
    return json_path, csv_path


def print_report(records: list[FrameRecord]) -> None:
    counts = Counter(record.frame_type.value for record in records)
    print(f"Scanned {len(records)} FITS file(s): " + ", ".join(f"{key}={value}" for key, value in sorted(counts.items())))
    print("TYPE     CONF  EXPTIME  GAIN  OBJECT(file/header)            FILE")
    print("-" * 110)
    for record in records:
        target = record.inferred_object or record.object_name or "-"
        if record.object_name and record.inferred_object and _normalise_name(record.object_name) != _normalise_name(record.inferred_object):
            target = f"{record.inferred_object}/{record.object_name}"
        exposure = "-" if record.exposure_s is None else f"{record.exposure_s:g}"
        gain = "-" if record.gain is None else f"{record.gain:g}"
        print(
            f"{record.frame_type.value:<8} {record.confidence:>4.2f}  {exposure:>7}  {gain:>4}  "
            f"{target[:28]:<28}  {record.relative_path}"
        )
        for warning in record.warnings:
            print(f"           ! {warning}")


def _first(header: fits.Header, *keys: str):
    for key in keys:
        value = header.get(key)
        if value is not None:
            return value
    return None


def _path_tokens(path: Path) -> set[str]:
    return {
        token
        for part in path.parts[-3:]
        for token in re.findall(r"[a-zA-Z]+", part.lower())
    }


def _clean_text(value) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _as_float(value) -> float | None:
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _as_int(value) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _normalise_name(value: str) -> str:
    return re.sub(r"[^a-z0-9]", "", value.lower())


def _relative_display(path: Path, root: Path) -> str:
    try:
        return str(path.relative_to(root))
    except ValueError:
        return str(path)
