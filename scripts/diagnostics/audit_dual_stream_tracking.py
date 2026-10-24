"""Offline SEP relative tracking of existing QHY science-photometry FITS only.

The experiment measures relative image repeatability during guided science, not
polar-drift truth, blind acquisition, or absolute G3 target/slit coordinates.
"""

from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import platform
import time

from astropy.io import fits
import astropy
import numpy as np
import scipy
from scipy.spatial import cKDTree
import sep


def validate_output_and_range(output: Path, frames: int, skip: int) -> Path:
    if frames < 2 or skip < 0:
        raise ValueError("frames must be >= 2 and skip must be >= 0")
    resolved = output.resolve()
    repository_tmp = (Path(__file__).resolve().parents[2] / "tmp").resolve()
    if resolved == repository_tmp or repository_tmp not in resolved.parents or resolved.exists():
        raise ValueError("Output must be a new directory strictly inside this repository's resolved tmp root")
    return resolved


def select_records(records: list[dict], frames: int, skip: int) -> list[dict]:
    if frames < 2 or skip < 0:
        raise ValueError("frames must be >= 2 and skip must be >= 0")
    selected = records[skip:skip + frames]
    if len(selected) != frames:
        raise ValueError(f"Requested {frames} consecutive frames after skip {skip}, but only {len(selected)} exist")
    return selected


def timestamp(value: str) -> float:
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def digest(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest().upper()


def percentile(values, q: float):
    values = list(values)
    return float(np.percentile(values, q)) if values else None


def extract(record: dict) -> dict:
    begin = time.perf_counter()
    path = Path(record["fitsPath"])
    actual_hash = digest(path)
    if actual_hash != record["sha256"].upper():
        raise ValueError(f"Immutable source hash changed: {path}")
    after_hash = time.perf_counter()
    with fits.open(path, memmap=False) as hdus:
        image = np.array(hdus[0].data, dtype=np.float32, order="C")
    height, width = image.shape
    background = sep.Background(image, bw=128, bh=128)
    image -= background.back()
    objects = sep.extract(image, 4.0, err=background.rms(), minarea=5,
                          deblend_nthresh=32, deblend_cont=0.005)
    snr_proxy = objects["flux"] / np.maximum(
        background.globalrms * np.sqrt(objects["npix"]), 1.0)
    selected = (
        (objects["x"] > 32) & (objects["x"] < width - 32) &
        (objects["y"] > 32) & (objects["y"] < height - 32) &
        (objects["a"] > 0.7) & (objects["a"] < 12) &
        (objects["b"] > 0.6) & (objects["b"] / objects["a"] > 0.4) &
        (objects["peak"] < 60000) & (objects["flag"] == 0) & (snr_proxy > 15)
    )
    objects = objects[selected]
    objects = objects[np.argsort(objects["flux"])[::-1]][:500]
    xy = np.column_stack((objects["x"], objects["y"]))
    return {"xy": xy, "shape": (height, width), "record": record,
            "detected": int(len(objects)), "sourceSha256": actual_hash,
            "hashSeconds": after_hash - begin,
            "readBackgroundSepSeconds": time.perf_counter() - after_hash}


def fit(source: np.ndarray, target: np.ndarray) -> np.ndarray:
    # [x',y'] = [a*x-b*y+tx, b*x+a*y+ty]; centred detector coordinates.
    matrix = np.zeros((2 * len(source), 4))
    matrix[0::2, 0] = source[:, 0]
    matrix[0::2, 1] = -source[:, 1]
    matrix[0::2, 2] = 1
    matrix[1::2, 0] = source[:, 1]
    matrix[1::2, 1] = source[:, 0]
    matrix[1::2, 3] = 1
    return np.linalg.lstsq(matrix, target.ravel(), rcond=None)[0]


def transform(points: np.ndarray, model: np.ndarray) -> np.ndarray:
    a, b, tx, ty = model
    return points @ np.array([[a, b], [-b, a]]) + (tx, ty)


def register(reference: dict, current: dict) -> dict:
    begin = time.perf_counter()
    height, width = reference["shape"]
    centre = np.array([width / 2, height / 2])
    ref, cur = reference["xy"] - centre, current["xy"] - centre
    # Fixed reference, no fit to held-out stars. Radius is a declared guided-data
    # experiment limit, not a blind-field matching algorithm.
    if len(ref) < 12 or len(cur) < 12:
        return {"accepted": False, "reason": "insufficient_detected_stars"}
    distances, forward = cKDTree(cur).query(ref, distance_upper_bound=15)
    _, backward = cKDTree(ref).query(cur, distance_upper_bound=15)
    pairs = [(i, int(j)) for i, j in enumerate(forward)
             if np.isfinite(distances[i]) and j < len(cur) and backward[j] == i]
    train = [(i, j) for i, j in pairs if i % 3 != 0]
    heldout = [(i, j) for i, j in pairs if i % 3 == 0]
    if len(train) < 8 or len(heldout) < 4:
        return {"accepted": False, "reason": "insufficient_mutual_matches",
                "candidateTrain": len(train), "candidateHeldout": len(heldout)}
    source = ref[[i for i, _ in train]]
    target = cur[[j for _, j in train]]
    rng = np.random.default_rng(20261024)
    best = np.zeros(len(train), dtype=bool)
    for _ in range(256):
        indices = rng.choice(len(train), 3, replace=False)
        model = fit(source[indices], target[indices])
        inliers = np.linalg.norm(transform(source, model) - target, axis=1) <= 1.0
        if inliers.sum() > best.sum():
            best = inliers
    if best.sum() < 8:
        return {"accepted": False, "reason": "no_consistent_training_transform",
                "candidateTrain": len(train), "candidateHeldout": len(heldout)}
    model = fit(source[best], target[best])
    train_residual = np.linalg.norm(transform(source, model) - target, axis=1)
    check_source = ref[[i for i, _ in heldout]]
    check_target = cur[[j for _, j in heldout]]
    check_residual = np.linalg.norm(transform(check_source, model) - check_target, axis=1)
    # Report ALL matched held-out stars; no favourable residual clipping.
    check_fraction = float(np.mean(check_residual <= 1.0))
    train_fraction = float(np.mean(train_residual <= 1.0))
    heldout_recovery = len(heldout) / sum(i % 3 == 0 for i in range(len(ref)))
    check_p95 = percentile(check_residual, 95)
    scale = float(np.hypot(model[0], model[1]))
    accepted = (train_fraction >= 0.8 and check_fraction >= 0.8 and heldout_recovery >= 0.8 and
                check_p95 <= 1.0 and abs(scale - 1) <= 0.002)
    return {"accepted": bool(accepted), "reason": "relative_heldout_validated" if accepted else "independent_validation_failed",
            "candidateTrain": len(train), "candidateHeldout": len(heldout),
            "referenceDetectedStars": len(ref), "heldoutReferenceRecoveryFraction": heldout_recovery,
            "referenceHeldoutStars": sum(i % 3 == 0 for i in range(len(ref))),
            "unmatchedReferenceHeldoutStars": sum(i % 3 == 0 for i in range(len(ref))) - len(heldout),
            "unmatchedReferenceStars": len(ref) - len(pairs),
            "trainInlierFraction": train_fraction, "heldoutInlierFraction": check_fraction,
            "trainMedianPixels": percentile(train_residual, 50),
            "heldoutMedianPixels": percentile(check_residual, 50),
            "heldoutP95Pixels": check_p95,
            "heldoutRmsPixels": float(np.sqrt(np.mean(check_residual ** 2))),
            "heldoutSpanPixels": np.ptp(check_source, axis=0).tolist(),
            "centreTranslationXPixels": float(model[2]), "centreTranslationYPixels": float(model[3]),
            "rotationDegrees": float(np.degrees(np.arctan2(model[1], model[0]))),
            "scale": scale, "registrationSeconds": time.perf_counter() - begin,
            "heldoutResidualsPixels": check_residual.tolist()}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--frames", type=int, default=20)
    parser.add_argument("--skip", type=int, default=4)
    args = parser.parse_args()
    output = validate_output_and_range(args.output, args.frames, args.skip)
    inventory = json.loads(args.inventory.read_text(encoding="utf-8"))
    run_ids = ["UVEX-20261021T155003Z-90031e12848644f", "UVEX-20261021T161417Z-b4d1814cc23441c"]
    segments, loaded = [], []
    started = time.perf_counter()
    for run_id in run_ids:
        job = next(row for row in inventory["jobs"] if row["runId"] == run_id)
        index = Path(job["frameIndex"])
        records = [json.loads(line) for line in index.read_text(encoding="utf-8").splitlines() if line.strip()]
        selected = select_records(records, args.frames, args.skip)
        frames = [extract(record) for record in selected]
        reference = frames[0]
        rows = []
        for ordinal, frame in enumerate(frames):
            result = register(reference, frame)
            result.update({"sequenceNumber": frame["record"]["sequenceNumber"],
                           "exposureMidpointUtc": frame["record"]["exposureMidpointUtc"],
                           "elapsedFromReferenceSeconds": timestamp(frame["record"]["exposureMidpointUtc"]) - timestamp(reference["record"]["exposureMidpointUtc"]),
                           "sourcePath": frame["record"]["fitsPath"], "sourceSha256": frame["sourceSha256"],
                           "detectedStars": frame["detected"], "hashSeconds": frame["hashSeconds"],
                           "readBackgroundSepSeconds": frame["readBackgroundSepSeconds"],
                           "selfReferenceNotValidation": ordinal == 0})
            rows.append(result)
        evaluated = rows[1:]
        accepted = [row for row in evaluated if row["accepted"]]
        gap = [timestamp(b["exposureMidpointUtc"]) - timestamp(a["exposureMidpointUtc"]) for a, b in zip(rows, rows[1:])]
        segment = {"runId": run_id, "target": job["target"], "frameIndex": str(index),
                   "indexSha256": digest(index), "frames": rows,
                   "detectorSettings": selected[0]["settings"], "imageShapeYX": reference["shape"],
                   "pixelScaleArcseconds": None, "pixelScaleAuthority": "Not present in frame index; angular pixel scale not assumed",
                   "distinctDetectorSettings": len({json.dumps(record["settings"], sort_keys=True) for record in selected}),
                   "summary": {
                       "evaluatedNonSelfFrames": len(evaluated), "acceptedNonSelfFrames": len(accepted),
                       "cadenceMedianSeconds": percentile(gap, 50), "cadenceMaximumSeconds": max(gap),
                       "holdoutMedianOfFrameMediansPixels": percentile((row["heldoutMedianPixels"] for row in accepted), 50),
                       "holdoutMaximumP95Pixels": max((row["heldoutP95Pixels"] for row in accepted), default=None),
                       "holdoutMinimumReferenceRecoveryFraction": min((row["heldoutReferenceRecoveryFraction"] for row in evaluated), default=None),
                       "unmatchedHeldoutMinimum": min((row["unmatchedReferenceHeldoutStars"] for row in evaluated), default=None),
                       "unmatchedHeldoutMaximum": max((row["unmatchedReferenceHeldoutStars"] for row in evaluated), default=None),
                       "unmatchedAllReferenceMinimum": min((row["unmatchedReferenceStars"] for row in evaluated), default=None),
                       "unmatchedAllReferenceMaximum": max((row["unmatchedReferenceStars"] for row in evaluated), default=None),
                       "centroidProcessingMedianSeconds": percentile((row["readBackgroundSepSeconds"] for row in rows), 50),
                       "registrationMedianSeconds": percentile((row.get("registrationSeconds", 0) for row in evaluated), 50),
                       "hashMedianSeconds": percentile((row["hashSeconds"] for row in rows), 50),
                       "lastAccepted": {key: accepted[-1][key] for key in ["elapsedFromReferenceSeconds", "centreTranslationXPixels", "centreTranslationYPixels", "rotationDegrees", "scale"]} if accepted else None}}
        segments.append(segment)
        loaded.append(frames)
    controls = []
    for source, target in [(loaded[0][0], loaded[1][0]), (loaded[1][0], loaded[0][0])]:
        result = register(source, target)
        result.update({"referencePath": source["record"]["fitsPath"], "wrongFieldPath": target["record"]["fitsPath"]})
        controls.append(result)
    report = {"purpose": __doc__, "scriptSha256": digest(Path(__file__)),
              "inventorySha256": digest(args.inventory), "elapsedSeconds": time.perf_counter() - started,
              "packages": {"python": platform.python_version(), "numpy": np.__version__, "scipy": scipy.__version__, "sep": sep.__version__, "astropy": astropy.__version__},
              "limits": {"initialMutualMatchingRadiusPixels": 15, "ransacTrainingThresholdPixels": 1,
                         "minimumTrain": 8, "minimumHeldout": 4, "minimumInlierFraction": 0.8,
                         "minimumHeldoutReferenceRecoveryFraction": 0.8,
                         "maximumHeldoutP95Pixels": 1, "maximumScaleDifference": 0.002,
                         "independence": "Fixed reference star IDs divisible by 3 withheld from all fitting; self reference excluded from validation"},
              "limitations": ["Only two guided science photometry segments, not blind acquisition or cloudy-field coverage.",
                              "Relative native-pixel repeatability, not polar-alignment drift truth or absolute G3/slit position.",
                              "Reference centroids are common to all comparisons; errors are temporally correlated.",
                              "P95 is per-frame over every matched heldout star without residual clipping; maximum P95 summarizes evaluated frames.",
                              "Unmatched stars remain counted in recovery fractions; the 500-source brightness cap can also cause non-recovery.",
                              "Wall-clock timing on a non-isolated workstation, potentially with concurrent builds/apps; not a real-time production throughput guarantee.",
                              "Timing uses immutable service midpoint metadata; no claim of calibrated native shutter timing."],
              "segments": segments, "wrongFieldControls": controls}
    output.mkdir(parents=True)
    report_path = output / "tracking.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    (output / "tracking.json.sha256").write_text(digest(report_path) + "  tracking.json\n", encoding="utf-8")
    print(json.dumps({"elapsedSeconds": report["elapsedSeconds"], "segments": [{"target": row["target"], **row["summary"]} for row in segments], "wrongFieldControls": controls}, indent=2))


if __name__ == "__main__":
    main()
