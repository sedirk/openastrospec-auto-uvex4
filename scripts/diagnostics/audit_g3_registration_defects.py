"""Replay saved G3 mosaic registrations against immutable original pixels.

This is an offline measurement audit, not a solver or a device-control entry
point. Run with the repository's pinned SEP environment. Output must be outside
the observations tree. No input file or saved evidence is modified.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

import numpy as np
from astropy.io import fits

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "star-detection"))
from sep_detector import detect  # noqa: E402


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def local_raw(root, saved_path):
    # Saved evidence may refer to a different machine's absolute path.
    parts = saved_path.replace("\\", "/").split("/")
    index = next(i for i, part in enumerate(parts) if part.startswith("UVEX-20"))
    path = root.joinpath(*parts[index:]).resolve(strict=True)
    if not path.is_relative_to(root):
        raise ValueError("Source escaped observations root")
    return path


def inspect_registration(root, receipt):
    payload = json.loads(receipt.read_text(encoding="utf-8-sig"))["payload"]
    result = payload["registration"]
    if not result["accepted"]:
        return None
    sources = []
    for key, sha_key in [("reference", "referenceSha256"), ("current", "currentSha256")]:
        path = local_raw(root, payload[key])
        before = digest(path)
        if before.lower() != payload[sha_key].lower():
            raise ValueError(f"Source hash differs: {path}")
        pixels = fits.getdata(path).astype(np.float32)
        measured = detect(pixels)
        points = []
        for pair in result["fitPairs"] + result["holdoutPairs"]:
            position = pair[key]
            component = min(measured["parent_components"], key=lambda c: math.hypot(
                c["x"] - position["x"], c["y"] - position["y"]))
            distance = math.hypot(component["x"] - position["x"], component["y"] - position["y"])
            if distance > 0.1:
                raise ValueError("Saved star not reproduced by current pinned extractor")
            minor_fwhm = 2.354820045 * component["b"]
            points.append({"savedPixel": position, "measured": component,
                           "minorAxisFwhmPixels": minor_fwhm,
                           "usableMeasuredShape": minor_fwhm >= 1.8})
        if digest(path) != before:
            raise ValueError("Input changed during replay")
        sources.append({"role": key, "path": str(path.relative_to(root)),
                        "sha256": before, "points": points,
                        "totalSavedWitnesses": len(points),
                        "resolvedSavedWitnesses": sum(p["usableMeasuredShape"] for p in points)})
    return {"receipt": str(receipt.relative_to(root)), "savedRegistration": result,
            "sources": sources, "targetIdentityGranted": False,
            "newSolveClaimed": False, "rawUnchanged": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observations", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--run", action="append", required=True,
                        help="Run directory ID; repeat for unrelated fields / controls.")
    args = parser.parse_args()
    root = args.observations.resolve(strict=True)
    output = args.output.resolve()
    if output == root or output.is_relative_to(root):
        parser.error("Output must be outside immutable observation roots")
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        parser.error("Choose a new output file; existing evidence is never overwritten")
    reports = []
    for run in args.run:
        directory = (root / run / "evidence").resolve(strict=True)
        if not directory.is_relative_to(root):
            parser.error("Run escaped observations root")
        for receipt in sorted(directory.glob("*g3-overlap-registration.json")):
            report = inspect_registration(root, receipt)
            if report is not None:
                reports.append(report)
    output.write_text(json.dumps({"algorithm": "sep-parent-moment-registration-audit-v1",
                                  "registrations": reports}, indent=2), encoding="utf-8")
    print(f"Read-only replay: {len(reports)} accepted saved registrations; {output}")
    for report in reports:
        print(report["receipt"], [(s["resolvedSavedWitnesses"], s["totalSavedWitnesses"])
                                   for s in report["sources"]])


if __name__ == "__main__":
    main()
