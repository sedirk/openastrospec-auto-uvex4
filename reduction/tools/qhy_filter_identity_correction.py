from __future__ import annotations

import argparse
import csv
import hashlib
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from astropy.io import fits


PHYSICAL_FILTER_BY_RECORDED_LABEL = {
    "S": "U",
    "H": "O",
    "O": "H",
    "U": "S",
    "G": "Z",
    "R": "I",
    "I": "R",
    "Z": "G",
    "SLOT 0": "U",
    "SLOT 1": "O",
    "SLOT 2": "H",
    "SLOT 3": "S",
    "SLOT 4": "Z",
    "SLOT 5": "I",
    "SLOT 6": "R",
    "SLOT 7": "G",
}


def normalize_filter_label(value: object) -> str:
    return " ".join(str(value or "").strip().upper().replace("_", " ").split())


def corrected_filter_label(recorded_label: object) -> str | None:
    return PHYSICAL_FILTER_BY_RECORDED_LABEL.get(normalize_filter_label(recorded_label))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def iter_fits_files(roots: Iterable[Path]) -> Iterable[Path]:
    seen: set[Path] = set()
    for root in roots:
        if not root.exists():
            continue
        candidates = [root] if root.is_file() else root.rglob("*")
        for path in candidates:
            if not path.is_file() or path.suffix.lower() not in {".fit", ".fits", ".fts"}:
                continue
            resolved = path.resolve()
            if resolved not in seen:
                seen.add(resolved)
                yield resolved


def is_qhy_minicam8m(header: fits.Header, stable_id: str | None) -> bool:
    model = str(header.get("INSTRUME", "")).strip()
    camera_id = str(header.get("CAMERAID", "")).strip()
    if stable_id and camera_id.casefold() != stable_id.casefold():
        return False
    return model.casefold() == "qhyminicam8m" or camera_id.casefold().startswith(
        "qhyminicam8m-"
    )


def build_manifest(
    roots: list[Path],
    *,
    stable_id: str | None = None,
    evidence_path: Path | None = None,
) -> dict[str, Any]:
    scanned = 0
    qhy_frames = 0
    unreadable: list[dict[str, str]] = []
    entries: list[dict[str, Any]] = []

    for path in iter_fits_files(roots):
        scanned += 1
        try:
            header = fits.getheader(path, 0)
        except Exception as exc:  # pragma: no cover - exercised only by damaged field data
            unreadable.append({"path": str(path), "error": str(exc)})
            continue
        if not is_qhy_minicam8m(header, stable_id):
            continue
        qhy_frames += 1
        recorded = normalize_filter_label(header.get("FILTER"))
        corrected = corrected_filter_label(recorded)
        if corrected is None:
            continue
        entries.append(
            {
                "path": str(path),
                "sha256": sha256_file(path),
                "sizeBytes": path.stat().st_size,
                "dateObs": str(header.get("DATE-OBS", "")),
                "imageType": str(header.get("IMAGETYP", "")),
                "recordedFilter": recorded,
                "physicalFilter": corrected,
                "cameraId": str(header.get("CAMERAID", "")),
                "instrument": str(header.get("INSTRUME", "")),
                "exposureSeconds": float(header.get("EXPTIME", 0.0)),
                "width": int(header.get("NAXIS1", 0)),
                "height": int(header.get("NAXIS2", 0)),
            }
        )

    entries.sort(key=lambda item: (item["dateObs"], item["path"]))
    unique_hashes = {entry["sha256"] for entry in entries}
    recorded_counts = Counter(entry["recordedFilter"] for entry in entries)
    corrected_counts = Counter(entry["physicalFilter"] for entry in entries)
    evidence: dict[str, Any] | None = None
    if evidence_path is not None:
        evidence = {
            "path": str(evidence_path.resolve()),
            "sha256": sha256_file(evidence_path),
        }

    return {
        "schemaVersion": 1,
        "correctionId": "QHYMINICAM8M-FILTER-IDENTITY-20260826",
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "policy": {
            "rawInputsImmutable": True,
            "application": "Resolve physical filter from this sidecar; never rewrite raw FITS.",
        },
        "scope": {
            "model": "QHYminiCam8M",
            "stableId": stable_id,
            "roots": [str(root.resolve()) for root in roots],
        },
        "mapping": dict(PHYSICAL_FILTER_BY_RECORDED_LABEL),
        "evidence": evidence,
        "summary": {
            "fitsPathsExamined": scanned,
            "qhyFramePathsExamined": qhy_frames,
            "affectedPathEntries": len(entries),
            "uniqueAffectedFramesBySha256": len(unique_hashes),
            "recordedFilterCounts": dict(sorted(recorded_counts.items())),
            "physicalFilterCounts": dict(sorted(corrected_counts.items())),
            "unreadableFitsPaths": len(unreadable),
        },
        "unreadable": unreadable,
        "entries": entries,
    }


def write_outputs(manifest: dict[str, Any], json_path: Path, csv_path: Path) -> None:
    json_path.parent.mkdir(parents=True, exist_ok=True)
    csv_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    columns = [
        "dateObs",
        "recordedFilter",
        "physicalFilter",
        "sha256",
        "sizeBytes",
        "exposureSeconds",
        "imageType",
        "cameraId",
        "path",
    ]
    with csv_path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=columns, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(manifest["entries"])


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build an immutable sidecar correcting the commissioned QHY filter identity."
    )
    parser.add_argument("--root", action="append", required=True, type=Path)
    parser.add_argument("--stable-id")
    parser.add_argument("--evidence", type=Path)
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--csv", required=True, type=Path)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    manifest = build_manifest(
        args.root,
        stable_id=args.stable_id,
        evidence_path=args.evidence,
    )
    write_outputs(manifest, args.json, args.csv)
    print(json.dumps(manifest["summary"], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
