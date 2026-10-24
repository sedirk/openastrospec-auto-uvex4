"""Read-only manifest census; no camera, service, solver or control calls.

This counts run segments, not unique target opportunities or sky success rate.
Real means the locked manifest adapter says real; unlabelled runs stay separate.
Only manifests are hashed here, not every historical FITS on disk.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import datetime
import hashlib
import json
from pathlib import Path
import statistics


def timestamp(value):
    if not value:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        return parsed if parsed.tzinfo is not None else None
    except (ValueError, AttributeError):
        return None


def quantiles(values):
    values = sorted(v for v in values if v is not None and v >= 0)
    if not values:
        return {"n": 0}
    return {"n": len(values), "median": statistics.median(values),
            "p90": values[min(len(values)-1, int(.9*(len(values)-1)))], "maximum": max(values)}


def summarize(document, path, sha):
    labels = (document.get("lockedMetadata") or {}).get("labels") or {}
    if str(labels.get("adapter", "")).lower() != "real":
        return None
    snapshot = document.get("snapshot") or {}
    events = snapshot.get("recentEvents") or []
    starts, first_starts, first_ends = {}, {}, {}
    durations = defaultdict(list)
    for event in events:
        t = timestamp(event.get("timestampUtc"))
        stage = event.get("stage")
        if t and event.get("code") == "STAGE_STARTED":
            starts[stage] = t
            first_starts.setdefault(stage, t)
        if t and event.get("code") == "STAGE_COMPLETED":
            first_ends.setdefault(stage, t)
            start = starts.pop(stage, None)
            if start is not None and t >= start:
                durations[stage].append((t-start).total_seconds())
    # First completed attempt in the retained event window, not first start to
    # last end across separate retries. A truncated event tail is not inferred.
    elapsed = {s: values[0] for s, values in durations.items()}
    gates = document.get("gates") or {}
    qhy = first_ends.get("AcquireQhyWideField")
    placement = first_starts.get("PlaceTargetOnSlit")
    terminal = snapshot.get("currentStage")
    terminal_gate = gates.get(terminal) or {}
    ev = document.get("evidence") or []
    roles = Counter(str(e.get("kind")) for e in ev)
    fits = {str(e.get("absolutePath")) for e in ev
            if str(e.get("absolutePath", "")).lower().endswith((".fits", ".fit", ".fts"))}
    target = ((document.get("plan") or {}).get("target") or {}).get("name", "")
    counters = document.get("counters") or {}
    return {"run": document.get("observationRunId", path.parent.name), "target": target,
            "created": document.get("createdUtc"), "state": snapshot.get("state"),
            "terminal_state": document.get("terminalState"), "last_stage": terminal,
            "last_gate": terminal_gate.get("code"), "accepted_atr": counters.get("atrAcceptedFrames", 0),
            "attempted_atr": counters.get("atrAttemptedFrames", 0),
            "native_root": labels.get("nativeTargetRootRunId"),
            "g3_gate": (gates.get("AcquireG3SlitField") or {}).get("code"),
            "qhy_gate": (gates.get("AcquireQhyWideField") or {}).get("code"),
            "stage_completed_seconds": elapsed,
            "completed_attempts_in_retained_event_window": {s: len(v) for s, v in durations.items()},
            "qhy_completed_to_placement_started_seconds": (placement-qhy).total_seconds() if placement and qhy else None,
            "evidence_kind_counts": dict(roles), "fits_references": len(fits),
            "has_existing_fits_reference": any(Path(p).is_file() for p in fits),
            "manifest": str(path), "manifest_sha256": sha}


def census(root, output):
    root, output = root.resolve(), output.resolve()
    if output == root or root in output.parents or output.exists():
        raise ValueError("Output must be new and outside immutable observation tree")
    output.mkdir(parents=True)
    runs, rejected = [], Counter()
    total = 0
    for path in root.glob("UVEX-*/manifest.json"):
        total += 1
        try:
            raw = path.read_bytes()
            doc = json.loads(raw)
            sha = hashlib.sha256(raw).hexdigest()
            row = summarize(doc, path, sha)
            if row:
                runs.append(row)
            else:
                rejected[str(((doc.get("lockedMetadata") or {}).get("labels") or {}).get("adapter", "missing"))] += 1
        except (OSError, ValueError, TypeError) as error:
            rejected[type(error).__name__] += 1
    runs.sort(key=lambda r: r["created"] or "")
    changed = [r["run"] for r in runs if hashlib.sha256(Path(r["manifest"]).read_bytes()).hexdigest() != r["manifest_sha256"]]
    if changed:
        raise RuntimeError(f"Concurrent manifest changes; no stable census: {changed}")
    daily = defaultdict(lambda: {"segments": 0, "with_accepted_atr": 0, "accepted_atr": 0, "states": Counter(), "last_gates": Counter()})
    for r in runs:
        t = timestamp(r["created"])
        day = t.date().isoformat() if t else "unknown"
        d = daily[day]
        d["segments"] += 1
        d["with_accepted_atr"] += int(r["accepted_atr"] > 0)
        d["accepted_atr"] += r["accepted_atr"]
        d["states"][str(r["state"])] += 1
        d["last_gates"][str(r["last_gate"])] += 1
    stages = {s: quantiles([r["stage_completed_seconds"].get(s) for r in runs]) for s in
              ("AcquireQhyWideField", "AcquireG3SlitField", "PlaceTargetOnSlit", "SelectAtrExposure")}
    report = {"scope": "manifest-adapter-labelled-real run segments; not unique targets, not replayed success rate",
              "manifests_scanned": total, "excluded": dict(rejected), "real_segments": len(runs),
              "segments_with_accepted_atr": sum(r["accepted_atr"] > 0 for r in runs),
              "segments_with_existing_fits_reference": sum(r["has_existing_fits_reference"] for r in runs),
              "accepted_atr_frame_count_sum_not_deduplicated": sum(r["accepted_atr"] for r in runs),
              "terminal_states": dict(Counter(str(r["state"]) for r in runs)),
              "last_gates": dict(Counter(str(r["last_gate"]) for r in runs)),
              "completed_stage_seconds": stages,
              "stage_duration_semantics": "first completed attempt in retained snapshot events; not total target duration",
              "qhy_gap_semantics": "initial QHY stage end to first placement start; not age of all possible later QHY witnesses",
              "qhy_completed_to_placement_started_seconds": quantiles([r["qhy_completed_to_placement_started_seconds"] for r in runs]),
              "daily": dict(daily), "real_manifest_hashes_rechecked": len(runs), "raw_fits_not_modified": True,
              "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "runs": runs}
    (output/"corpus.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({k:v for k,v in report.items() if k not in ("runs", "daily", "last_gates")}, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observations", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    census(args.observations, args.output)
