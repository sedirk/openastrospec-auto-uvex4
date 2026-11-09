"""Replay the production measured-source worker on an explicit immutable cohort.

No hardware/network calls; input paths/hashes/hints come from a local JSON list.
Write only to a new analysis directory, outside the raw observation directories.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "astap_recovery", Path(__file__).resolve().parents[1]/"star-detection"/"astap_recovery.py")
WORKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(WORKER)


def replay(samples, runtime, output):
    output.mkdir(parents=True, exist_ok=False)
    results = []
    for index, sample in enumerate(samples):
        sample = sample.get("sample", sample)
        folder = output/f"{index:04d}"
        folder.mkdir()
        request = dict(runtime, sourcePath=sample["path"], sourceSha256=sample["sha256"],
                       ra=sample["ra"], dec=sample["dec"], scale=sample["scale"], radiusDegrees=2.)
        result = WORKER.recover(request, folder)
        (folder/"result.json").write_text(json.dumps(result, ensure_ascii=False, allow_nan=False), encoding="utf-8")
        assert WORKER.sha(sample["path"]).lower() == sample["sha256"].lower()
        row = dict(label=sample["label"], sourceSha256=sample["sha256"], accepted=result["accepted"],
                   reason=result["reason"], matches=len(result.get("matches", [])),
                   selection=result.get("solverSelection"), elapsedSeconds=result["elapsedSeconds"])
        results.append(row)
        (output/"summary.json").write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")
        print(json.dumps(row, ensure_ascii=True), flush=True)
    return results


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--samples", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    replay(json.loads(args.samples.read_text(encoding="utf-8-sig")),
           json.loads(args.runtime.read_text(encoding="utf-8-sig")), args.output.resolve())
