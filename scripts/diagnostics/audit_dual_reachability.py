"""Read-only causal reachability audit of immutable QHY/G3 candidates.

No hardware, network, SDK, raw edits, installation or production configuration
changes. Reports distinguish strict schema-2 production eligibility from a
counterfactual reconstruction of old schema-1 focus from readonly audit logs.
The mathematical model mirrors the .311 local fit for diagnosis; it is not a
release build or a declaration that reconstructed samples can be autoloaded.
"""

from __future__ import annotations

import argparse
import collections
import datetime as dt
import hashlib
import json
import math
import pathlib
import statistics
import time


def timestamp(value: str) -> dt.datetime:
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(dt.timezone.utc)


def sha(path: pathlib.Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def separation(a: dict, b: dict) -> float:
    ra1, dec1, ra2, dec2 = map(math.radians, (a["centerRightAscensionDegrees"], a["centerDeclinationDegrees"],
                                            b["centerRightAscensionDegrees"], b["centerDeclinationDegrees"]))
    h = math.sin((dec2 - dec1) / 2) ** 2 + math.cos(dec1) * math.cos(dec2) * math.sin((ra2 - ra1) / 2) ** 2
    return math.degrees(2 * math.asin(math.sqrt(min(1, max(0, h))))) * 3600


def geometry(frame: dict) -> tuple:
    names = ("cameraStableId", "frameWidthPixels", "frameHeightPixels", "binningX", "binningY", "roiOriginX", "roiOriginY",
             "roiWidthPixels", "roiHeightPixels", "flipped")
    return tuple(frame.get(k) for k in names)


def context(candidate: dict) -> tuple:
    names = ("commissioningPresetSha256", "nightSetupId", "nightSetupSha256", "installationEpochId", "telescopeDeviceId",
             "qhyCameraStableId", "g3CameraStableId", "qhyOpticalTrainId", "g3OpticalTrainId", "pierSide")
    return tuple(candidate.get(k) for k in names) + geometry(candidate["qhy"]) + geometry(candidate["g3"])


def signed_angle(value: float) -> float:
    return (value + 180) % 360 - 180


def tangent_project(ra: float, dec: float, east: float, north: float) -> dict:
    r, d = math.radians(ra), math.radians(dec)
    x, y = math.radians(east / 3600), math.radians(north / 3600)
    rho = math.hypot(x, y)
    if rho == 0:
        return {"centerRightAscensionDegrees": ra, "centerDeclinationDegrees": dec}
    c = math.atan(rho)
    out_dec = math.asin(math.cos(c) * math.sin(d) + y * math.sin(c) * math.cos(d) / rho)
    out_ra = r + math.atan2(x * math.sin(c), rho * math.cos(d) * math.cos(c) - y * math.sin(d) * math.sin(c))
    return {"centerRightAscensionDegrees": math.degrees(out_ra) % 360, "centerDeclinationDegrees": math.degrees(out_dec)}


def fit_diagnostic(current: dict, previous: list[dict], max_age_seconds: float = 1800) -> dict:
    c, now = current["candidate"], current["created"]
    sources = sorted(previous, key=lambda p: (p["created"], p["candidate"]["candidateSha256"]))
    result = {"sourceCount": len(sources), "sourceRunIds": [p["candidate"]["observationRunId"] for p in sources]}

    def reject(reason: str) -> dict:
        return result | {"accepted": False, "reason": reason}

    if not 3 <= len(sources) <= 24:
        return reject("insufficient_3_or_over_24_samples")
    s = [p["candidate"] for p in sources]
    if len({p["observationRunId"] for p in s}) < len(s):
        return reject("duplicate_run_sources")
    hashes = [p[role]["frameSha256"].upper() for p in s for role in ("qhy", "g3")]
    if len(set(hashes)) < len(hashes):
        return reject("duplicate_raw_source")
    if any((now - p["created"]).total_seconds() > max_age_seconds for p in sources):
        return reject("source_age")
    distances = [separation(p["qhy"], c["qhy"]) for p in s]
    if max(distances) > 5 * 3600:
        return reject("one_source_outside_5_degree_query_radius")
    pairs = [separation(a["qhy"], b["qhy"]) for i, a in enumerate(s) for b in s[i + 1:]]
    sky_span = max(pairs)
    if sky_span > 5 * 3600:
        return reject("fit_preflight_pairwise_sky_span_over_5_degrees")
    minimum_scale = min(p["g3"]["pixelScaleArcseconds"] for p in s)
    if min(pairs) < 10 * minimum_scale or sky_span < min(c["g3"]["frameWidthPixels"], c["g3"]["frameHeightPixels"]) * minimum_scale / 2:
        return reject("insufficient_distinct_sky_footprint")
    train, holdout = s[:-1], s[-1]
    east = statistics.median(p["model"]["g3MinusQhyEastArcseconds"] for p in train)
    north = statistics.median(p["model"]["g3MinusQhyNorthArcseconds"] for p in train)
    ratio = statistics.median(p["g3"]["pixelScaleArcseconds"] / p["qhy"]["pixelScaleArcseconds"] for p in train)
    origin = train[0]["model"]["g3MinusQhyPositionAngleDegrees"]
    angle = origin + statistics.median(signed_angle(p["model"]["g3MinusQhyPositionAngleDegrees"] - origin) for p in train)
    corner = math.hypot(c["g3"]["frameWidthPixels"], c["g3"]["frameHeightPixels"]) / 2

    def residual(p: dict) -> float:
        m = p["model"]
        translation = math.hypot(m["g3MinusQhyEastArcseconds"] - east, m["g3MinusQhyNorthArcseconds"] - north)
        shape = corner * (abs(ratio * p["qhy"]["pixelScaleArcseconds"] - p["g3"]["pixelScaleArcseconds"]) +
                          2 * p["g3"]["pixelScaleArcseconds"] * abs(math.sin(math.radians(signed_angle(m["g3MinusQhyPositionAngleDegrees"] - angle)) / 2)))
        return translation + shape

    maximum_residual = max(map(residual, s))
    source_uncertainty = max(p["model"]["predictionUncertaintyArcseconds"] for p in s)
    cap = 100 * minimum_scale  # Recorded operating preset for Oct 23; every other use is explicitly hypothetical.
    if maximum_residual + 2 * source_uncertainty >= cap:
        return reject("independent_fit_or_corner_error_over_100px")
    seconds_span = (sources[-1]["created"] - sources[0]["created"]).total_seconds()
    since_latest = (now - sources[-1]["created"]).total_seconds()
    nearest = min(distances)
    if seconds_span <= 0 or nearest > sky_span or since_latest > seconds_span:
        return reject("outside_empirical_time_or_spatial_extrapolation_envelope")
    uncertainty = maximum_residual * (1 + nearest / sky_span + since_latest / seconds_span) + 2 * source_uncertainty + c["qhy"]["pixelScaleArcseconds"]
    if uncertainty > cap:
        return reject("growth_uncertainty_over_100px")
    prediction = tangent_project(c["qhy"]["centerRightAscensionDegrees"], c["qhy"]["centerDeclinationDegrees"], east, north)
    actual_error = separation(prediction, c["g3"])
    return result | {"accepted": True, "reason": "causal_local_fit_counterfactual_only",
                     "predictionErrorArcseconds": actual_error, "predictionErrorG3Pixels": actual_error / c["g3"]["pixelScaleArcseconds"],
                     "uncertaintyArcseconds": uncertainty, "holdoutResidualArcseconds": residual(holdout),
                     "sourceSkySpanDegrees": sky_span / 3600, "secondsSinceLatestSource": since_latest}


def percentile(values: list[float], q: float) -> float | None:
    if not values:
        return None
    s = sorted(values)
    return s[round((len(s) - 1) * q)]


def summary(values: list[float]) -> dict:
    return {"count": len(values), "min": min(values) if values else None, "p50": percentile(values, .5),
            "p90": percentile(values, .9), "p99": percentile(values, .99), "max": max(values) if values else None}


def optimistic_two_new_witnesses(candidate: dict, model_uncertainty: float) -> dict:
    """Best-case zero true drift, exact G3 timing and zero extra CPU/IO/HTTP delay.

    Uses each actual record's exposure and solve durations twice. This is an
    optimistic timing/error-envelope prototype, not two newly observed frames.
    """
    q, g = candidate["qhy"], candidate["g3"]
    qe = (timestamp(q["exposureEndedUtc"]) - timestamp(q["exposureStartedUtc"])).total_seconds()
    qm = (timestamp(q["exposureMidpointUtc"]) - timestamp(q["exposureStartedUtc"])).total_seconds()
    qs = (timestamp(q["solveCompletedUtc"]) - timestamp(q["exposureEndedUtc"])).total_seconds()
    ge = (timestamp(g["exposureEndedUtc"]) - timestamp(g["exposureStartedUtc"])).total_seconds()
    gs = (timestamp(g["solveCompletedUtc"]) - timestamp(g["exposureEndedUtc"])).total_seconds()
    baseline = qe + qs
    first_mid = gs + qm
    second_mid = gs + baseline + qm
    now = gs + 2 * baseline
    g3_mid = -ge / 2
    qhy_half_support = max(qm, qe - qm)
    source_error = math.hypot(q["pixelScaleArcseconds"], candidate["collectionPolicy"]["maximumMountSpanArcseconds"])
    initial_rate_error = 2 * source_error / baseline
    integration_error = initial_rate_error * qhy_half_support
    rate_error = (2 * source_error + 2 * integration_error) / baseline
    error = source_error + integration_error + rate_error * (max(abs(g3_mid - second_mid), abs(now - second_mid)) + ge / 2)
    cap = 100 * g["pixelScaleArcseconds"]
    reason = "accept_optimistic_zero_drift"
    if now > 30 or now - second_mid > 15 or max(abs(g3_mid - first_mid), abs(g3_mid - second_mid)) > 20 or qs >= 10:
        reason = "time_or_processing_delay_window"
    elif error + model_uncertainty >= cap:
        reason = "error_envelope_even_at_zero_true_drift"
    return {"accepted": reason.startswith("accept"), "reason": reason, "addedUncertaintyArcseconds": error,
            "modelUncertaintyArcseconds": model_uncertainty, "capArcseconds": cap,
            "currentG3AgeAtDecisionSeconds": now + ge / 2, "QhySolveSeconds": qs,
            "G3ExposureSeconds": ge, "baselineSeconds": baseline}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-root", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--verify-source-hashes", action="store_true")
    parser.add_argument("--corpus", type=pathlib.Path, help="Optional readonly real-labelled manifest census from the root audit.")
    args = parser.parse_args()
    root, out = args.data_root.resolve(), args.output.resolve()
    if out.exists():
        raise SystemExit("Output must be a new directory.")
    if root == out or root in out.parents:
        raise SystemExit("Generated output cannot be inside observation data root.")
    started = time.perf_counter()
    archive = root / "calibration" / "qhy-g3"
    audit_records: list[tuple[dt.datetime, dict]] = []
    audit_files = sorted((root / "closed-loop").glob("*.jsonl"))
    for path in audit_files:
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            try:
                row = json.loads(line)
                p = row.get("result", {})
                if row.get("kind") == "night-setup-revalidated" and "c11RunStartPositionSteps" in p:
                    audit_records.append((timestamp(row["timestampUtc"]), p))
            except (json.JSONDecodeError, KeyError, ValueError):
                continue
    audit_records.sort(key=lambda p: p[0])
    records: list[dict] = []
    parse_errors = []
    digest_cache: dict[str, str | None] = {}
    for path in sorted(archive.glob("*/*candidate-*.json")):
        try:
            envelope = json.loads(path.read_text(encoding="utf-8-sig"))
            c = envelope["candidate"]
            created = timestamp(c["createdUtc"])
            focus = [a for when, a in audit_records if when <= created and (created - when).total_seconds() <= 1800 and
                     a.get("ActionConfigurationSha256", a.get("actionConfigurationSha256", "")).upper() == c["actionConfigurationSha256"].upper() and
                     a.get("nightSetupSha256", "").upper() == c["nightSetupSha256"].upper() and
                     a.get("commissioningPresetSha256", "").upper() == c["commissioningPresetSha256"].upper()]
            focus_key = (focus[-1].get("c11FocuserDeviceId"), focus[-1].get("c11RunStartPositionSteps")) if focus else None
            hashes = []
            for role in ("qhy", "g3"):
                for path_key, hash_key in (("framePath", "frameSha256"), ("solveEvidencePath", "solveEvidenceSha256")):
                    source = pathlib.Path(c[role][path_key])
                    actual = None
                    if args.verify_source_hashes:
                        key = str(source)
                        if key not in digest_cache:
                            digest_cache[key] = sha(source) if source.is_file() else None
                        actual = digest_cache[key]
                    hashes.append({"role": role + ":" + path_key, "path": str(source), "exists": source.is_file(),
                                   "matches": actual == c[role][hash_key].upper() if args.verify_source_hashes else None})
            manifest_path = root / "observations" / c["observationRunId"] / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig")) if manifest_path.is_file() else {}
            record = {"archivePath": str(path), "archiveSha256": sha(path), "schema": envelope.get("schemaVersion"),
                      "candidate": c, "created": created, "focusKey": focus_key, "sourceFiles": hashes,
                      "terminalState": manifest.get("terminalState"), "terminalReason": manifest.get("terminalReason"),
                      "acceptedScienceFrames": manifest.get("counters", {}).get("acceptedScienceFrames"),
                      "workerAdapter": manifest.get("lockedMetadata", {}).get("labels", {}).get("qhyServiceAdapter"),
                      "qhyConnectionRole": c["qhy"]["timingAuthority"]}
            records.append(record)
        except (KeyError, ValueError, OSError, TypeError) as error:
            parse_errors.append({"path": str(path), "error": str(error)})
    # Deduplicate repeated identical immutable records, retaining all raw files unchanged.
    records = list({r["candidate"]["candidateSha256"]: r for r in records}.values())
    records.sort(key=lambda r: r["created"])
    decisions = []
    stage_counts = collections.Counter()
    reason_counts = collections.Counter()
    day_counts = collections.Counter()
    for current in records:
        c = current["candidate"]
        query_started = timestamp(c["qhy"]["exposureStartedUtc"])
        past = [p for p in records if p["created"] < query_started]
        same_context = [p for p in past if context(p["candidate"]) == context(c)]
        age_limited = [p for p in same_context if (current["created"] - p["created"]).total_seconds() <= 1800]
        focus_limited = [p for p in age_limited if current["focusKey"] is not None and p["focusKey"] == current["focusKey"]]
        available = [p for p in focus_limited if all(s["exists"] and (s["matches"] is not False) for s in p["sourceFiles"])]
        stage_counts["queries"] += 1
        stage_counts["with_at_least_3_same_context_past_any_age"] += len(same_context) >= 3
        stage_counts["with_at_least_3_past_same_context_within_30m"] += len(age_limited) >= 3
        stage_counts["with_at_least_3_and_reconstructable_same_focus"] += len(focus_limited) >= 3
        stage_counts["with_at_least_3_original_sources_present_and_hash_ok"] += len(available) >= 3
        decision = fit_diagnostic(current, available)
        reason_counts[decision["reason"]] += 1
        day_counts[current["created"].astimezone(dt.timezone(dt.timedelta(hours=8))).date().isoformat()] += 1
        decisions.append({"runId": c["observationRunId"], "createdUtc": current["created"].isoformat(),
                          "focus": current["focusKey"], "schema2StrictProductionEligible": current["schema"] == 2,
                          "sameContextPastCount": len(same_context), "within30mCount": len(age_limited),
                          "reconstructedSameFocusCount": len(focus_limited), "diagnosticOnly": decision})
    source_issues = [s for r in records for s in r["sourceFiles"] if not s["exists"] or s["matches"] is False]
    durations = {"qhyShutterToSolveSeconds": [], "g3ShutterToSolveSeconds": [], "pairMidpointSeparationSeconds": [],
                 "pairWallClockSeconds": [], "qhyExposureSeconds": [], "g3ExposureSeconds": []}
    for r in records:
        c = r["candidate"]
        for role in ("qhy", "g3"):
            f = c[role]
            durations[role + "ShutterToSolveSeconds"].append((timestamp(f["solveCompletedUtc"]) - timestamp(f["exposureEndedUtc"])).total_seconds())
            durations[role + "ExposureSeconds"].append((timestamp(f["exposureEndedUtc"]) - timestamp(f["exposureStartedUtc"])).total_seconds())
        for field in ("pairMidpointSeparationSeconds", "pairWallClockSeconds"):
            durations[field].append(c[field])
    consecutive = []
    for a, b in zip(records, records[1:]):
        if context(a["candidate"]) == context(b["candidate"]):
            consecutive.append((b["created"] - a["created"]).total_seconds())
    corpus_rows = []
    corpus_stages = collections.Counter()
    if args.corpus:
        census = json.loads(args.corpus.read_text(encoding="utf-8-sig"))
        for entry in census["runs"]:
            manifest_path = pathlib.Path(entry["manifest"])
            manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
            locked = manifest.get("lockedMetadata") or {}
            labels = locked.get("labels") or {}
            if labels.get("adapter") != "real":
                continue
            corpus_stages["real_labelled_segments"] += 1
            raw_references = [e for e in manifest.get("evidence", []) if pathlib.Path(e.get("absolutePath", "")).suffix.lower() in (".fit", ".fits")]
            present_raw = [e for e in raw_references if pathlib.Path(e["absolutePath"]).is_file()]
            if not present_raw:
                continue
            corpus_stages["segments_with_existing_fits_reference"] += 1
            starts = [timestamp(j["timestampUtc"]) for j in manifest.get("journal", [])
                      if j.get("stage") == "AcquireG3SlitField" and j.get("code") == "STAGE_STARTED"]
            if not starts:
                continue
            query_time = min(starts)
            corpus_stages["segments_entered_g3_acquisition_with_fits"] += 1
            run_id = manifest["observationRunId"]
            qhy_solve = None
            for e in manifest.get("evidence", []):
                if e.get("kind") != "plate-solve-evidence" or timestamp(e["timestampUtc"]) > query_time:
                    continue
                p = pathlib.Path(e["absolutePath"])
                if not p.is_file():
                    continue
                d = json.loads(p.read_text(encoding="utf-8-sig"))
                payload = d.get("payload") or {}
                if payload.get("success") and "QHY" in payload.get("role", "") and payload.get("solved"):
                    qhy_solve = payload["solved"]
            templates = [r for r in records if r["candidate"]["commissioningPresetSha256"].upper() == locked.get("commissioningPresetSha256", "").upper() and
                         r["candidate"]["nightSetupSha256"].upper() == locked.get("nightSetupSha256", "").upper()]
            if not templates or not qhy_solve:
                corpus_rows.append({"runId": run_id, "reason": "no_matching_immutable_geometry_template_or_pre_g3_qhy_wcs"})
                continue
            corpus_stages["with_matching_configuration_template_and_pre_g3_qhy_wcs"] += 1
            template = templates[-1]["candidate"]
            action = (locked.get("additionalHashes") or {}).get("actionConfigurationSha256", "")
            focus = [a for when, a in audit_records if when <= query_time and (query_time - when).total_seconds() <= 1800 and
                     a.get("ActionConfigurationSha256", a.get("actionConfigurationSha256", "")).upper() == action.upper() and
                     a.get("nightSetupSha256", "").upper() == template["nightSetupSha256"].upper() and
                     a.get("commissioningPresetSha256", "").upper() == template["commissioningPresetSha256"].upper()]
            focus_key = (focus[-1].get("c11FocuserDeviceId"), focus[-1].get("c11RunStartPositionSteps")) if focus else None
            prior = [r for r in records if r["candidate"]["observationRunId"] != run_id and r["created"] < query_time and
                     context(r["candidate"]) == context(template) and (query_time - r["created"]).total_seconds() <= 1800 and
                     focus_key is not None and r["focusKey"] == focus_key]
            corpus_stages["with_at_least_3_previous_30m_same_cfg_focus"] += len(prior) >= 3
            q = template["qhy"] | {"centerRightAscensionDegrees": qhy_solve["raDegrees"],
                                   "centerDeclinationDegrees": qhy_solve["decDegrees"],
                                   "pixelScaleArcseconds": qhy_solve["pixelScaleArcseconds"]}
            query = {"candidate": template | {"qhy": q}, "created": query_time}
            decision = fit_diagnostic(query, prior)
            corpus_stages["counterfactual_fit_supported_at_first_g3_entry"] += decision["accepted"]
            corpus_rows.append({"runId": run_id, "target": entry["target"], "queryUtc": query_time.isoformat(),
                                "lastG3Gate": (manifest.get("gates", {}).get("AcquireG3SlitField") or {}).get("code"),
                                "pastSameConfigurationFocus30mCount": len(prior), "diagnosticOnly": decision})
    report = {
        "purpose": "Read-only causal reachability audit, not activation or production alteration",
        "scriptSha256": sha(pathlib.Path(__file__)), "sourceRoot": str(root), "archiveRecordCount": len(records),
        "schemaCounts": dict(collections.Counter(r["schema"] for r in records)), "archiveDays": dict(sorted(day_counts.items())),
        "strictProductionSchema2Count": sum(r["schema"] == 2 for r in records),
        "reconstructedFocusCount": sum(r["focusKey"] is not None for r in records),
        "counterfactualCausalStages": dict(stage_counts), "counterfactualDiagnosticReasons": dict(reason_counts),
        "counterfactualFitsAccepted": [d for d in decisions if d["diagnosticOnly"]["accepted"]],
        "latencies": {k: summary(v) for k, v in durations.items()},
        "consecutiveSameContextPairSpacingSeconds": summary(consecutive),
        "workerAdapters": dict(collections.Counter(str(r["workerAdapter"]) for r in records)),
        "pairSourceCounts": dict(collections.Counter(r["candidate"]["pairSource"] for r in records)),
        "timingAuthorities": dict(collections.Counter(r["qhyConnectionRole"] for r in records)),
        "sourceHashVerificationRequested": args.verify_source_hashes, "uniqueSourceFilesHashed": len(digest_cache),
        "optimisticTwoFreshQhyZeroDriftPrototypes": {
            str(model_error): {
                "reasons": dict(collections.Counter(optimistic_two_new_witnesses(r["candidate"], model_error)["reason"] for r in records)),
                "addedUncertaintyArcseconds": summary([optimistic_two_new_witnesses(r["candidate"], model_error)["addedUncertaintyArcseconds"] for r in records]),
            } for model_error in (0, 5, 10, 20, 21.810878796657416)
        },
        "sourceFileIssues": source_issues, "parseErrors": parse_errors, "decisions": decisions,
        "firstG3EntryCensus": {"source": str(args.corpus) if args.corpus else None,
                               "stages": dict(corpus_stages), "rows": corpus_rows},
        "limitations": ["Historical schema1 focus reconstruction is diagnostic only and never production-autoloadable.",
                        "Denominator contains paired formal G3 successes, not every attempted target; an unpaired first-target G3 failure cannot bootstrap the model.",
                        "100px is the recorded Oct23 operating recognition radius; other-day fit conclusions are explicitly counterfactual, not their saved-policy acceptance.",
                        "Fit diagnostic mirrors key .311 model gates, not the actual production assembly or the additional fresh dual-QHY drift witness.",
                        "No future sample enters any causal fit; query is each later pair's accepted QHY exposure start.",
                        "No source files were rewritten, renamed, moved or deleted."],
        "elapsedSeconds": time.perf_counter() - started,
    }
    out.mkdir(parents=True)
    (out / "reachability.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("archiveRecordCount", "schemaCounts", "counterfactualCausalStages", "counterfactualDiagnosticReasons",
                                           "strictProductionSchema2Count", "sourceFileIssues", "elapsedSeconds")}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
