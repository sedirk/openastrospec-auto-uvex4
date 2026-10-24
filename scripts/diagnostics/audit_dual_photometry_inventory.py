"""Read-only QHY job/index census; does not open FITS, SDK, service or hardware.

Inventories historical photometry cadence for future offline registration tests.
An existing FITS reference is not a verified sky frame or a solved frame.
"""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
import hashlib
import json
from pathlib import Path
import statistics


def instant(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def distribution(values: list[float]) -> dict:
    ordered = sorted(values)
    if not ordered:
        return {"count": 0}
    return {
        "count": len(ordered),
        "minimum": ordered[0],
        "median": statistics.median(ordered),
        "p90": ordered[min(len(ordered) - 1, int(len(ordered) * 0.90))],
        "maximum": ordered[-1],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root, output = args.data_root.resolve(), args.output.resolve()
    if output == root or root in output.parents or output.exists():
        raise ValueError("Output must be new and outside immutable observational data")
    kinds: Counter = Counter()
    photometry: list[dict] = []
    errors: list[dict] = []
    for path in sorted((root / "runs").rglob("manifest.json")):
        try:
            job = json.loads(path.read_text(encoding="utf-8-sig"))
            kinds[str(job.get("kind"))] += 1
            if str(job.get("kind")).casefold() != "photometry":
                continue
            index = path.parent / "frames.jsonl"
            source = "frames.jsonl" if index.exists() else "retained manifest frames"
            frames = (
                [json.loads(line) for line in index.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
                if index.exists()
                else job.get("frames", [])
            )
            frames = sorted(
                {str(frame["frameId"]): frame for frame in frames}.values(),
                key=lambda frame: frame["exposureMidpointUtc"],
            )
            gaps = [
                (instant(b["exposureMidpointUtc"]) - instant(a["exposureMidpointUtc"])).total_seconds()
                for a, b in zip(frames, frames[1:])
            ]
            exposures = [float(frame["settings"]["exposureSeconds"]) for frame in frames]
            filters = Counter(str(frame["settings"].get("filterName")) for frame in frames)
            exists = sum(Path(frame["fitsPath"]).is_file() for frame in frames)
            geometry_keys = {
                json.dumps(
                    {key: value for key, value in frame["settings"].items() if key != "exposureSeconds"},
                    sort_keys=True,
                )
                for frame in frames
            }
            photometry.append({
                "manifest": str(path), "frameIndex": str(index), "frameSource": source,
                "jobId": job.get("id"), "runId": job.get("observationRunId"),
                "target": job.get("requestedTarget"), "camera": job.get("expectedCameraStableId"),
                "state": job.get("state"), "indexedFrames": len(frames), "existingFitsPaths": exists,
                "startUtc": frames[0]["exposureStartedUtc"] if frames else None,
                "endUtc": frames[-1]["exposureEndedUtc"] if frames else None,
                "spanSeconds": (
                    instant(frames[-1]["exposureEndedUtc"]) - instant(frames[0]["exposureStartedUtc"])
                ).total_seconds() if frames else 0,
                "midpointGapSeconds": distribution(gaps), "exposureSeconds": distribution(exposures),
                "filters": dict(filters), "distinctSettingsExcludingExposure": len(geometry_keys),
                "roles": dict(Counter(str(frame.get("role")) for frame in frames)),
                "timingSources": dict(Counter(str(frame.get("timingSource", "absent")) for frame in frames)),
                "gapsLe5s": sum(0 < gap <= 5 for gap in gaps),
                "gapsLe10s": sum(0 < gap <= 10 for gap in gaps),
                "gapsLe30s": sum(0 < gap <= 30 for gap in gaps),
                "gapsLe60s": sum(0 < gap <= 60 for gap in gaps),
                "midpointGaps": gaps,
            })
        except (OSError, ValueError, KeyError, TypeError) as error:
            errors.append({"manifest": str(path), "error": str(error)})
    all_gaps = [gap for row in photometry for gap in row["midpointGaps"]]
    report = {
        "purpose": "Read-only metadata inventory, not registration/sky validation or production activation",
        "scriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest().upper(),
        "dataRoot": str(root), "jobKinds": dict(kinds), "errors": errors,
        "photometryJobs": len(photometry),
        "indexedPhotometryFrames": sum(row["indexedFrames"] for row in photometry),
        "existingPhotometryFitsPaths": sum(row["existingFitsPaths"] for row in photometry),
        "photometryJobsWithAtLeast2Frames": sum(row["indexedFrames"] >= 2 for row in photometry),
        "photometryJobsWithAtLeast10Frames": sum(row["indexedFrames"] >= 10 for row in photometry),
        "photometryJobsWithAtLeast100Frames": sum(row["indexedFrames"] >= 100 for row in photometry),
        "allPhotometryMidpointGapSeconds": distribution(all_gaps),
        "photometryGapsLe5s": sum(0 < gap <= 5 for gap in all_gaps),
        "photometryGapsLe10s": sum(0 < gap <= 10 for gap in all_gaps),
        "photometryGapsLe30s": sum(0 < gap <= 30 for gap in all_gaps),
        "photometryGapsLe60s": sum(0 < gap <= 60 for gap in all_gaps),
        "jobs": photometry,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key not in ("jobs", "errors")}, indent=2))


if __name__ == "__main__":
    main()
