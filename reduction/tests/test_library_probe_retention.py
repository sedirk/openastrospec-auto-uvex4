"""All physical deletions below use tiny synthetic pytest files only."""
import hashlib
import json
import os
from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.library_catalog import LibraryCatalog, PARSER_VERSION
from uvex_reduce.library_cleanup_execute import CleanupExecutor, confirmation_phrase, latest_file_receipt
from uvex_reduce.library_cleanup_plan import CleanupPlanner
from uvex_reduce.library_probe_retention import (
    OwnerSpaceReclaimRetentionPlanner,
    ProbeRetentionPlanner,
    TargetSuccessRetentionPlanner,
    TerminalTargetRetentionPlanner,
)
from uvex_reduce.library_probe_retention_watch import ProbeMetadataWatch, unrelated_metadata


def make_run(root, *, state="Completed", science=True, code="TARGET_SKY_CONTRAST_LOW",
             accepted="False", run_id="one"):
    folder = root / run_id
    folder.mkdir(parents=True)
    evidence = []
    paths = {}
    for name, role, quality in ([("bad", "PROBE", accepted), ("good", "PROBE", "True")]
                               + ([("science", "SCIENCE", "True")] if science else [])):
        path = folder / f"{name}.fits"
        hdu = fits.PrimaryHDU(np.ones((8, 8), dtype=np.uint16))
        hdu.header.update(OBSRUNID=run_id, UVEXCID=name, UVEXSTG=role)
        hdu.writeto(path)
        paths[name] = path
        evidence.append({"kind": "atr-probe-fits" if role == "PROBE" else "atr-science-fits",
                         "absolutePath": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                         "metadata": {"observationRunId": run_id, "captureId": name,
                                      "stageRole": role, "qualityAccepted": quality,
                                      "qualityCode": code if name == "bad" else "ATR_PROBE_TIER_VALIDATED",
                                      "fitsProvenanceVerified": "True"}})
    record = {"observationRunId": run_id, "simulator": False,
              "plan": {"target": {"name": "Star"}}, "snapshot": {"state": state},
              "terminalState": state, "evidence": evidence}
    manifest = folder / "manifest.json"
    manifest.write_text(json.dumps(record), encoding="utf-8")
    return paths, manifest, record


def make_target_success_run(root, *, state="Completed", science=True,
                            planned_start="2026-11-06T12:00:00+00:00"):
    run_id = "success"
    folder = root / run_id
    evidence_dir = folder / "evidence"
    evidence_dir.mkdir(parents=True)
    paths = {}
    evidence = []

    def add_frame(name, kind, *, stage=None, accepted=None, metadata=None):
        path = evidence_dir / name
        hdu = fits.PrimaryHDU(np.ones((8, 8), dtype=np.uint16))
        hdu.header["OBSRUNID"] = run_id
        hdu.header["UVEXCID"] = (metadata or {}).get("captureId", name)
        if stage:
            hdu.header["UVEXSTG"] = stage
        hdu.writeto(path)
        paths[path.stem] = path
        evidence.append({
            "kind": kind,
            "absolutePath": str(path),
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "metadata": metadata,
        })
        return path

    add_frame("bad-probe.fits", "atr-probe-fits", stage="PROBE", metadata={
        "observationRunId": run_id, "captureId": "bad", "stageRole": "PROBE",
        "qualityAccepted": "False", "qualityCode": "TARGET_SKY_CONTRAST_LOW",
        "fitsProvenanceVerified": "True",
    })
    add_frame("unselected-probe.fits", "atr-probe-fits", stage="PROBE", metadata={
        "observationRunId": run_id, "captureId": "unselected", "stageRole": "PROBE",
        "qualityAccepted": "False", "qualityCode": "EXPOSURE_TIER_SELECTED",
        "fitsProvenanceVerified": "True",
    })
    add_frame("accepted-uncredited-probe.fits", "atr-probe-fits", stage="PROBE", metadata={
        "observationRunId": run_id, "captureId": "accepted-uncredited",
        "stageRole": "PROBE", "qualityAccepted": "True",
        "qualityCode": "ATR_PROBE_TIER_VALIDATED", "fitsProvenanceVerified": "True",
    })
    if science:
        credited = add_frame("credited-trial.fits", "atr-probe-fits", stage="PROBE", metadata={
            "observationRunId": run_id, "captureId": "credited", "stageRole": "PROBE",
            "qualityAccepted": "True", "qualityCode": "ATR_PROBE_TIER_VALIDATED",
            "fitsProvenanceVerified": "True",
        })
        evidence.append({
            "kind": "atr-science-fits", "absolutePath": str(credited),
            "sha256": hashlib.sha256(credited.read_bytes()).hexdigest(),
            "metadata": {
                "observationRunId": run_id, "captureId": "credited",
                "effectiveStageRole": "SCIENCE", "qualityAccepted": "True",
                "qualityCode": "ATR_FRAME_QUALITY_VALID", "fitsProvenanceVerified": "True",
            },
        })
        add_frame("science.fits", "atr-science-fits", stage="SCIENCE", metadata={
            "observationRunId": run_id, "captureId": "science",
            "stageRole": "SCIENCE", "qualityAccepted": "True",
            "qualityCode": "ATR_FRAME_QUALITY_VALID",
            "fitsProvenanceVerified": "True",
        })
    add_frame("g3-solve.fit", "g3-plate-solve-probe-fits")
    add_frame("g3-residual.fit", "g3-phd2-lock-residual")
    add_frame("qhy-acquisition.fits", "qhy-acquisition-fits")
    add_frame("qhy-photometry-r.fits", "qhy-photometry-fits")

    record = {
        "observationRunId": run_id,
        "simulator": False,
        "plan": {"target": {"name": "Star"}, "plannedStartUtc": planned_start},
        "snapshot": {"state": state},
        "terminalState": state,
        "evidence": evidence,
    }
    manifest = folder / "manifest.json"
    manifest.write_text(json.dumps(record), encoding="utf-8")
    return paths, manifest, record


def add_qhy_photometry_job(root, *, run_id="success", job_state="Completed",
                           leaf_role="PHOTOMETRY-R", write_frame_index=True):
    job = root / "qhy" / "data" / "runs" / run_id / "qhy-photometry-job"
    raw_dir, preview_dir = job / "raw", job / "preview"
    raw_dir.mkdir(parents=True)
    preview_dir.mkdir(parents=True)
    frame_id = "photo-frame"
    raw = raw_dir / f"000001_{leaf_role}_{run_id}.fits"
    preview = preview_dir / f"000001_{leaf_role}_{run_id}.png"
    fits.PrimaryHDU(np.ones((8, 8), dtype=np.uint16)).writeto(raw)
    preview.write_bytes(bytes.fromhex(
        "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4"
        "890000000d49444154789c6360000000020001e221bc330000000049454e44ae"
        "426082"
    ))
    record = {
        "id": "qhy-photometry-job", "observationRunId": run_id,
        "state": job_state, "completedUtc": "2026-11-06T12:30:00+00:00",
        "startedUtc": "2026-11-06T12:01:00+00:00",
    }
    (job / "manifest.json").write_text(json.dumps(record), encoding="utf-8")
    frame = {
        "frameId": frame_id, "sequenceNumber": 1, "role": leaf_role,
        "fitsPath": str(raw), "previewPath": str(preview),
        "sha256": hashlib.sha256(raw.read_bytes()).hexdigest(),
    }
    (job / "frames.jsonl").write_text(
        (json.dumps(frame) + "\n") if write_frame_index else "", encoding="utf-8")
    return raw, preview, record


def planner(tmp_path, **kwargs):
    catalog = LibraryCatalog(tmp_path / "catalog.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    return ProbeRetentionPlanner(catalog, authorized=True, **kwargs)


def target_success_planner(tmp_path):
    catalog = LibraryCatalog(tmp_path / "catalog.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    return TargetSuccessRetentionPlanner(catalog, authorized=True)


def terminal_target_planner(tmp_path, **kwargs):
    catalog = LibraryCatalog(tmp_path / "catalog.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    return TerminalTargetRetentionPlanner(catalog, authorized=True, **kwargs)


def owner_reclaim_planner(tmp_path):
    catalog = LibraryCatalog(tmp_path / "catalog.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    return OwnerSpaceReclaimRetentionPlanner(catalog, authorized=True)


def test_explicit_policy_only_rejects_and_preserves_valid_probes(tmp_path):
    paths, _, _ = make_run(tmp_path / "data")
    p = planner(tmp_path)
    assert ProbeRetentionPlanner(p.catalog).preview_probes() is None
    ordinary = CleanupPlanner(p.catalog).preview(["run:one"], "permanent")
    assert not any(i["selected"] for i in ordinary["items"])
    plan = p.preview_probes()
    selected = [i for i in plan["items"] if i["selected"]]
    assert [Path(i["path"]).name for i in selected] == ["bad.fits"]
    assert plan["estimated_release_bytes"] == paths["bad"].stat().st_size
    assert p.revalidate(plan)["valid"]
    p.catalog.close()


def test_target_success_deletes_parse_and_uncredited_trials_but_keeps_science_photometry_and_credited_trial(tmp_path):
    paths, manifest, _ = make_target_success_run(tmp_path / "data")
    p = target_success_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name for i in plan["items"] if i["selected"]}
    assert selected == {
        "bad-probe.fits", "unselected-probe.fits", "accepted-uncredited-probe.fits",
        "g3-solve.fit", "g3-residual.fit", "qhy-acquisition.fits",
    }
    assert paths["credited-trial"].exists()
    assert paths["science"].exists()
    assert paths["qhy-photometry-r"].exists()
    assert manifest.exists()
    p.catalog.close()


@pytest.mark.parametrize("state", ["Faulted", "Failed", "Cancelled"])
def test_terminal_target_cleans_disposable_frames_after_failed_or_cancelled_target(tmp_path, state):
    paths, manifest, _ = make_target_success_run(
        tmp_path / "data", state=state, science=False)
    p = terminal_target_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name for i in plan["items"] if i["selected"]}
    assert selected == {
        "bad-probe.fits", "unselected-probe.fits", "accepted-uncredited-probe.fits",
        "g3-solve.fit", "g3-residual.fit", "qhy-acquisition.fits",
    }
    assert paths["qhy-photometry-r"].exists()
    assert manifest.exists()
    p.catalog.close()


def test_terminal_target_keeps_science_and_credited_trial_after_failure(tmp_path):
    paths, manifest, _ = make_target_success_run(
        tmp_path / "data", state="Faulted", science=True)
    p = terminal_target_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name for i in plan["items"] if i["selected"]}
    assert "credited-trial.fits" not in selected
    assert "science.fits" not in selected
    assert "qhy-photometry-r.fits" not in selected
    assert "bad-probe.fits" in selected
    assert paths["credited-trial"].exists()
    assert paths["science"].exists()
    assert manifest.exists()
    p.catalog.close()


def test_terminal_target_removes_paired_qhy_acquisition_preview_not_photometry(tmp_path):
    _, manifest, record = make_target_success_run(
        tmp_path / "data", state="Faulted", science=False)
    acquisition, preview, _ = add_qhy_photometry_job(
        tmp_path / "data", leaf_role="ACQUISITION-WIDE-STREAM")
    record["evidence"].append({
        "kind": "qhy-acquisition-fits", "absolutePath": str(acquisition),
        "sha256": hashlib.sha256(acquisition.read_bytes()).hexdigest(),
        "metadata": {"observationRunId": "success"},
    })
    manifest.write_text(json.dumps(record), encoding="utf-8")
    p = terminal_target_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name.casefold() for i in plan["items"] if i["selected"]}
    assert acquisition.name.casefold() in selected
    assert preview.name.casefold() in selected
    assert not any("qhy-photometry-r" in name for name in selected)
    p.catalog.close()


@pytest.mark.parametrize("state", ["RunningAuto", "PausedNeedsAttention", "", None])
def test_terminal_target_never_cleans_active_or_unknown_target(tmp_path, state):
    make_target_success_run(tmp_path / "data", state=state, science=False)
    p = terminal_target_planner(tmp_path)
    assert p.preview_candidates() is None
    p.catalog.close()


def test_terminal_target_uses_forward_only_watermark(tmp_path):
    make_target_success_run(tmp_path / "data", state="Faulted", science=False,
                            planned_start="2026-11-06T12:00:00+00:00")
    assert terminal_target_planner(tmp_path).preview_candidates()

    catalog = LibraryCatalog(tmp_path / "future.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    future = TerminalTargetRetentionPlanner(
        catalog, authorized=True,
        not_before_utc="2026-11-06T13:00:00+00:00")
    assert future.preview_candidates() is None
    catalog.close()


@pytest.mark.skipif(os.name != "nt", reason="Windows handle deletion")
def test_terminal_producer_evidence_path_not_model_and_denied_item_isolated(tmp_path, monkeypatch):
    from uvex_reduce.library_probe_retention_cli import cycle
    from uvex_reduce.library_cleanup_execute import WindowsCleanupBackend
    paths, manifest, record = make_target_success_run(tmp_path / "data", state="Faulted")
    for e in record["evidence"]:
        e["evidencePath"] = e["absolutePath"]
    manifest.write_text(json.dumps(record), encoding="utf-8")
    (manifest.parent / "fit-model.json").write_text(json.dumps({
        "observationRunId": "success", "evidencePath": str(paths["g3-residual"]),
    }), encoding="utf-8")
    p = terminal_target_planner(tmp_path)
    monkeypatch.setattr(WindowsCleanupBackend, "delete_access",
                        staticmethod(lambda path: Path(path).name != "bad-probe.fits"))
    result = cycle(p.catalog, execute=True, policy="terminal-target")
    assert result["status"] == "completed" and result["access_denied_count"] == 1, result
    assert result["deleted_count"] > 0 and not paths["unselected-probe"].exists()
    for name in ("bad-probe", "g3-residual", "credited-trial", "science", "qhy-photometry-r"):
        assert paths[name].is_file()
    p.catalog.close()


def test_real_stream_alias_requires_job_index_header_and_digest(tmp_path):
    from uvex_reduce.library_cleanup_plan import _facts
    from uvex_reduce.library_catalog import _canonical
    job = "12345678-1234-1234-1234-123456789abc"
    frame = "87654321-1234-1234-1234-123456789abc"
    d = tmp_path / "one" / ("qhy-" + job.replace("-", ""))
    (d / "raw").mkdir(parents=True)
    path = d / "raw/test.fits"
    role = "ACQUISITION-WIDE-STREAM"
    label = "Star / continuous acquisition witness"
    hdu = fits.PrimaryHDU(np.ones((8, 8), dtype=np.uint16))
    for k,v in {"OBJECT":label,"FRAMROLE":role,"OBS-RUN":"one","JOBID":job,"FRAMEID":frame}.items():
        hdu.header[k] = v
    hdu.writeto(path)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    (d / "manifest.json").write_text(json.dumps({"id": job,"observationRunId":"one",
        "state":"Cancelled", "completedUtc":"2026-11-20T00:00:00Z"}))
    entry = {"frameId":frame,"fitsPath":str(path),"role":role,"sha256":digest}
    (d / "frames.jsonl").write_text(json.dumps(entry))
    snap = {"by_path":{},"metadata_facts":{}}
    for i,p in enumerate((d/"manifest.json", d/"frames.jsonl")):
        f = _facts(str(p))
        snap["by_path"][_canonical(p)] = {**f,"id":i,"warning":"","missing":False}
        snap["metadata_facts"][i] = f
    warn = f"Identity conflict (target): FITS={label!r}; linked metadata='Star'; FITS identity retained"
    row = {"target":label,"warning":warn,"path":_canonical(path),"role":role,"capture_id":frame}
    proof = {"qhy_identity":("Star",role,job,frame),"run_id":"one","sha256":digest,"source_ids":[0,1]}
    valid = TerminalTargetRetentionPlanner._verified_witness_alias
    assert valid(row,proof,snap)
    assert not valid({**row,"warning":warn+"; unrelated warning"},proof,snap)
    assert not valid({**row,"role":"PHOTOMETRY"},proof,snap)
    assert not valid(row,{**proof,"run_id":"other"},snap)
    assert not valid(row,{**proof,"sha256":"0"*64},snap)
    path.write_bytes(path.read_bytes()+b"x")
    assert not valid(row,proof,snap)


def test_archived_control_requires_matching_retirement_and_guards_archive(tmp_path):
    root = tmp_path / "data"
    make_target_success_run(root)
    control = root / "control"
    control.mkdir()
    old = control / "native-meridian-flip.json"
    entry = dict(RunId="ended", Phase="OwnersIdle", BeforeSide="pierWest", AfterSide="",
                 UpdatedUtc="2026-11-19T16:07:18Z", Segments=["ended"], Reason="ObservedSideChange")
    old.write_text(json.dumps(entry),encoding="utf-8")
    p = terminal_target_planner(tmp_path)
    archive = old.with_name(old.name + ".pre-dispatch-" + "a"*32 + ".archive")
    old.rename(archive)
    receipt = archive.with_name(archive.name + ".audit.json")
    audit = dict(action="EndedPreDispatchMeridianIntentRetired",motionSent=False,Phase="OwnersIdle",
                 oldRunId="ended",segments=["ended"],originalPath=str(old), archivePath=str(archive),
                 originalSha256=hashlib.sha256(archive.read_bytes()).hexdigest())
    p.catalog.scan()
    assert p.preview_candidates() is None
    receipt.write_text(json.dumps(audit),encoding="utf-8")
    p.catalog.scan()
    plan = p.preview_candidates()
    assert plan
    assert {os.path.normcase(str(f)) for f in (old, archive, receipt)} <= set(plan["shared_guard_paths"])
    archive.write_bytes(archive.read_bytes()+b" ")
    assert not p.revalidate(plan)["valid"]
    p.catalog.close()


def test_terminal_guide_archive_protects_original_inputs_not_entire_target(tmp_path):
    paths, manifest, _ = make_target_success_run(tmp_path / "data")
    control = manifest.parent / "control"
    control.mkdir()
    old = control / "g3-acquisition-motion.json"
    old.write_text(json.dumps({"state":{"observationRunId":"success",
                                        "framePath":str(paths["g3-solve"])}}))
    p = terminal_target_planner(tmp_path)
    archive = old.with_name(old.name + ".terminal-" + "b"*32 + ".archive")
    updated = json.loads(old.read_text())
    updated["state"]["lastReason"] = (
        "Monotonic G3 lineage aggregate adopted from 1 trustworthy durable copy/copies without counter or clock rollback.")
    old.write_text(json.dumps(updated))
    old.rename(archive)
    receipt = archive.with_name(archive.name + ".audit.json")
    receipt.write_text(json.dumps(dict(action="TerminalTargetGuideStateRetired",motionSent=False,
        runId="success",originalPath=str(old),archivePath=str(archive),
        originalSha256=hashlib.sha256(archive.read_bytes()).hexdigest())))
    p.catalog.scan()
    # Production obtains a container-run association for these transient files.
    p.catalog.connection.execute("UPDATE files SET run_id='success' WHERE path=?",
                                 (os.path.normcase(str(old)),))
    p.catalog.connection.commit()
    plan = p.preview_candidates()
    assert plan
    chosen = {Path(i["path"]).name for i in plan["items"] if i["selected"]}
    assert "bad-probe.fits" in chosen and "g3-solve.fit" not in chosen
    assert os.path.normcase(str(archive)) in plan["shared_guard_paths"]
    p.catalog.close()


def test_owner_reclaim_adds_qhy_photometry_pairs_to_successful_history(tmp_path):
    paths, manifest, record = make_target_success_run(tmp_path / "data")
    photometry, preview, _ = add_qhy_photometry_job(tmp_path / "data")
    record["evidence"].append({
        "kind": "qhy-photometry-fits", "absolutePath": str(photometry),
        "evidencePath": str(photometry),
        "sha256": hashlib.sha256(photometry.read_bytes()).hexdigest(),
        "metadata": {"observationRunId": "success"},
    })
    manifest.write_text(json.dumps(record), encoding="utf-8")
    (tmp_path / "data" / "success" / "evidence" / "plate-solve.json").write_text(
        json.dumps({"observationRunId": "success", "framePath": str(photometry)}),
        encoding="utf-8",
    )
    p = owner_reclaim_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name.casefold() for i in plan["items"] if i["selected"]}
    assert photometry.name.casefold() in selected
    assert preview.name.casefold() in selected
    assert {
        "bad-probe.fits", "unselected-probe.fits", "accepted-uncredited-probe.fits",
        "g3-solve.fit", "g3-residual.fit", "qhy-acquisition.fits",
    } <= selected
    for name in ("credited-trial", "science", "qhy-photometry-r"):
        assert paths[name].exists()
    assert manifest.exists()
    p.catalog.close()


def test_owner_reclaim_removes_terminal_photometry_from_failed_run_without_probe_cleanup(tmp_path):
    paths, manifest, record = make_target_success_run(tmp_path / "data", state="Faulted", science=False)
    record["snapshot"]["state"] = "Faulted"
    record["terminalState"] = "Faulted"
    manifest.write_text(json.dumps(record), encoding="utf-8")
    photometry, preview, _ = add_qhy_photometry_job(
        tmp_path / "data", job_state="Faulted")
    p = owner_reclaim_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name.casefold() for i in plan["items"] if i["selected"]}
    assert selected == {photometry.name.casefold(), preview.name.casefold()}
    assert paths["bad-probe"].exists()
    p.catalog.close()


def test_owner_reclaim_covers_unlisted_successful_g3_led_preshots(tmp_path):
    _, _, _ = make_target_success_run(tmp_path / "data")
    frame = tmp_path / "data" / "success" / "evidence" / "00099-g3-slit-led-off-before-01.fit"
    fits.PrimaryHDU(np.ones((8, 8), dtype=np.uint16)).writeto(frame)
    catalog = LibraryCatalog(tmp_path / "catalog.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    catalog.connection.execute(
        "UPDATE files SET channel='slit',role='led-off-before-01',run_id='success' WHERE path=?",
        (os.path.normcase(str(frame)),))
    catalog.connection.commit()
    p = OwnerSpaceReclaimRetentionPlanner(catalog, authorized=True)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name for i in plan["items"] if i["selected"]}
    assert frame.name in selected
    p.catalog.close()


def test_owner_reclaim_tolerates_target_name_warning_for_exact_qhy_pair(tmp_path):
    _, _, record = make_target_success_run(tmp_path / "data")
    photometry, preview, _ = add_qhy_photometry_job(tmp_path / "data")
    record["evidence"].append({
        "kind": "qhy-photometry-fits", "absolutePath": str(photometry),
        "evidencePath": str(photometry),
        "sha256": hashlib.sha256(photometry.read_bytes()).hexdigest(),
        "metadata": {"observationRunId": "success"},
    })
    catalog = LibraryCatalog(tmp_path / "catalog.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    catalog.connection.execute(
        "UPDATE files SET warning='Identity conflict (target)' WHERE path=?",
        (os.path.normcase(str(photometry)),))
    catalog.connection.commit()
    p = OwnerSpaceReclaimRetentionPlanner(catalog, authorized=True)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name.casefold() for i in plan["items"] if i["selected"]}
    assert photometry.name.casefold() in selected
    assert preview.name.casefold() in selected
    p.catalog.close()


def test_owner_reclaim_covers_terminal_final_frame_missing_from_frame_index(tmp_path):
    _, _, _ = make_target_success_run(tmp_path / "data")
    photometry, preview, _ = add_qhy_photometry_job(
        tmp_path / "data", leaf_role="ACQUISITION-WIDE-STREAM",
        write_frame_index=False)
    catalog = LibraryCatalog(tmp_path / "catalog.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    catalog.connection.execute(
        "UPDATE files SET channel='photometry',role='ACQUISITION-WIDE-STREAM',"
        "run_id='success' WHERE path IN (?,?)",
        (os.path.normcase(str(photometry)), os.path.normcase(str(preview))),
    )
    catalog.connection.commit()
    p = OwnerSpaceReclaimRetentionPlanner(catalog, authorized=True)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name.casefold() for i in plan["items"] if i["selected"]}
    assert photometry.name.casefold() in selected
    assert preview.name.casefold() in selected
    p.catalog.close()


@pytest.mark.parametrize("run_state,job_state", [
    ("RunningAuto", "Running"),
    ("Completed", "Running"),
    ("PausedNeedsAttention", "PausedNeedsAttention"),
])
def test_owner_reclaim_never_deletes_active_or_unknown_photometry(
        tmp_path, run_state, job_state):
    make_target_success_run(tmp_path / "data", state=run_state)
    add_qhy_photometry_job(tmp_path / "data", job_state=job_state)
    p = owner_reclaim_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name.casefold()
                for i in (plan or {}).get("items", ()) if i["selected"]}
    assert not any("photometry-r" in name for name in selected)
    p.catalog.close()


def test_owner_reclaim_preserves_reduction_dependency(tmp_path):
    _, _, _ = make_target_success_run(tmp_path / "data")
    photometry, preview, _ = add_qhy_photometry_job(tmp_path / "data")
    (tmp_path / "data" / "saved.astroproj").write_text(
        json.dumps({"inputs": {"photometry": [str(photometry)]}}), encoding="utf-8")
    p = owner_reclaim_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name.casefold() for i in plan["items"] if i["selected"]}
    assert photometry.name.casefold() not in selected
    assert preview.name.casefold() in selected
    p.catalog.close()


@pytest.mark.parametrize("state", ["Faulted", "Cancelled", "RunningAuto", "PausedNeedsAttention"])
def test_target_success_never_cleans_unsuccessful_or_active_target(tmp_path, state):
    make_target_success_run(tmp_path / "data", state=state)
    p = target_success_planner(tmp_path)
    assert p.preview_candidates() is None
    p.catalog.close()


def test_target_success_requires_accepted_science(tmp_path):
    make_target_success_run(tmp_path / "data", science=False)
    p = target_success_planner(tmp_path)
    assert p.preview_candidates() is None
    p.catalog.close()


def test_target_success_never_reaches_back_before_enable_watermark(tmp_path):
    make_target_success_run(tmp_path / "data", planned_start="2026-11-06T12:00:00+00:00")
    p = target_success_planner(tmp_path)
    assert p.preview_candidates()
    p.catalog.close()

    catalog = LibraryCatalog(tmp_path / "future.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    future = TargetSuccessRetentionPlanner(
        catalog, authorized=True, not_before_utc="2026-11-06T13:00:00+00:00")
    assert future.preview_candidates() is None
    catalog.close()


def test_target_success_dependency_protects_only_referenced_parse_frame(tmp_path):
    paths, _, _ = make_target_success_run(tmp_path / "data")
    (tmp_path / "data" / "saved.astroproj").write_text(
        json.dumps({"inputs": {"g3": [str(paths["g3-solve"])]}}), encoding="utf-8")
    p = target_success_planner(tmp_path)
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name for i in plan["items"] if i["selected"]}
    assert "g3-solve.fit" not in selected
    assert "g3-residual.fit" in selected
    assert paths["g3-solve"].exists()
    p.catalog.close()


def test_target_success_changed_parse_hash_is_protected(tmp_path):
    paths, _, _ = make_target_success_run(tmp_path / "data")
    p = target_success_planner(tmp_path)
    paths["qhy-acquisition"].write_bytes(paths["qhy-acquisition"].read_bytes() + b"changed")
    plan = p.preview_candidates()
    selected = {Path(i["path"]).name for i in plan["items"] if i["selected"]}
    assert "qhy-acquisition.fits" not in selected
    p.catalog.close()


@pytest.mark.skipif(os.name != "nt", reason="Real Windows locked-handle backend")
def test_target_success_physical_batch_keeps_only_science_credited_trial_and_photometry(tmp_path):
    paths, manifest, _ = make_target_success_run(tmp_path / "data")
    p = target_success_planner(tmp_path)
    plan = p.preview_candidates()
    receipt = CleanupExecutor(p.catalog, p).execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "completed", receipt
    assert sum(row["status"] == "deleted" for row in receipt["results"]) == 6
    for name in ("bad-probe", "unselected-probe", "accepted-uncredited-probe",
                 "g3-solve", "g3-residual", "qhy-acquisition"):
        assert not paths[name].exists()
    for name in ("credited-trial", "science", "qhy-photometry-r"):
        assert paths[name].exists()
    assert manifest.exists()
    audit = Path(receipt["audit_path"])
    assert audit.exists()
    assert json.loads(audit.read_text(encoding="utf-8").splitlines()[-1])["receipt"]["status"] == "completed"
    p.catalog.close()


@pytest.mark.parametrize("state", ["RunningAuto", "Paused", "", None])
def test_active_and_unknown_never_deleted(tmp_path, state):
    make_run(tmp_path / "data", state=state)
    p = planner(tmp_path, require_science=False)
    assert p.preview_probes() is None
    p.catalog.close()


def test_failed_history_opt_in_is_not_automatic_success_policy(tmp_path):
    make_run(tmp_path / "data", state="Faulted", science=False)
    p = planner(tmp_path)
    assert p.preview_probes() is None
    assert ProbeRetentionPlanner(p.catalog, authorized=True, require_science=False).preview_probes()
    p.catalog.close()


@pytest.mark.parametrize(("code", "accepted"), [
    ("EXPOSURE_TIER_SELECTED", "False"), ("UNKNOWN", "False"),
    ("TARGET_SKY_CONTRAST_LOW", "True"), ("TARGET_SKY_CONTRAST_LOW", None),
])
def test_unselected_but_valid_or_unknown_probe_protected(tmp_path, code, accepted):
    make_run(tmp_path / "data", code=code, accepted=accepted)
    p = planner(tmp_path)
    assert p.preview_probes() is None
    p.catalog.close()


@pytest.mark.parametrize("mode", ["reuse", "project", "model", "operator_pick", "hash", "header"])
def test_reuse_dependencies_and_identity_protect(tmp_path, mode):
    paths, manifest, record = make_run(tmp_path / "data")
    if mode == "reuse":
        record["evidence"][0]["metadata"]["effectiveStageRole"] = "SCIENCE"
    elif mode in {"project", "model"}:
        (tmp_path / "data" / ("saved.astroproj" if mode == "project" else "model.json")).write_text(
            json.dumps({"inputs": {"science": [str(paths["bad"])]}}), encoding="utf-8")
    elif mode == "hash":
        record["evidence"][0]["sha256"] = "0" * 64
    elif mode == "header":
        with fits.open(paths["bad"], mode="update") as hdus:
            hdus[0].header["UVEXSTG"] = "SCIENCE"
        record["evidence"][0]["sha256"] = hashlib.sha256(paths["bad"].read_bytes()).hexdigest()
    manifest.write_text(json.dumps(record), encoding="utf-8")
    p = planner(tmp_path)
    if mode == "operator_pick":
        identifier = p.connection.execute("SELECT id FROM files WHERE capture_id='bad'").fetchone()[0]
        p.catalog.set_review(identifier, "pick")
    plan = p.preview_probes()
    assert plan is None or all(i["protected"] for i in plan["items"] if i["selected"])
    p.catalog.close()


def test_preview_invalidated_by_later_science_reuse(tmp_path):
    _, manifest, record = make_run(tmp_path / "data")
    p = planner(tmp_path)
    plan = p.preview_probes()
    record["evidence"][0]["metadata"]["effectiveStageRole"] = "SCIENCE"
    manifest.write_text(json.dumps(record), encoding="utf-8")
    assert not p.revalidate(plan)["valid"]
    p.catalog.close()


@pytest.mark.skipif(os.name != "nt", reason="Real Windows locked-handle backend")
def test_actual_deletion_keeps_science_good_probe_logs_and_active_next_target(tmp_path):
    root = tmp_path / "data"
    paths, manifest, _ = make_run(root)
    active, _, _ = make_run(root, run_id="next", state="RunningAuto")
    keep = [paths["good"], paths["science"], manifest, *active.values()]
    before = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in keep}
    p = planner(tmp_path)
    plan = p.preview_probes()
    receipt = CleanupExecutor(p.catalog, p).execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "completed", receipt
    assert receipt["results"][0]["status"] == "deleted"
    assert not paths["bad"].exists()
    assert {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in keep} == before
    assert latest_file_receipt(p.catalog, paths["bad"])["status"] == "deleted"
    assert p.preview_probes() is None
    p.catalog.close()


@pytest.mark.parametrize("reference", ["bad.fits", "*.fits", ".", "nested/../bad.fits"])
def test_new_dependency_absolute_relative_glob_directory_blocks(tmp_path, reference):
    probe = tmp_path / "bad.fits"
    record = tmp_path / "new.astroproj"
    record.write_text(json.dumps({"dataRoot": reference}), encoding="utf-8")
    assert not unrelated_metadata(record, {os.path.normcase(str(probe))})
    watcher = ProbeMetadataWatch([tmp_path], candidates=[probe])
    with pytest.raises(OSError):
        watcher.changed(tmp_path, [record.name])


def test_unrelated_new_acquisition_metadata_allowed_but_partial_and_guards_defer(tmp_path):
    path = tmp_path / "new.json"
    path.write_text(json.dumps({"fitsPath": str(tmp_path / "new.fits")}), encoding="utf-8")
    watcher = ProbeMetadataWatch([tmp_path], candidates=[tmp_path / "old.fits"])
    watcher.changed(tmp_path, [path.name])
    path.write_text('{"partial":', encoding="utf-8")
    with pytest.raises(OSError):
        watcher.changed(tmp_path, [path.name])
    guard = tmp_path / "manifest.json"
    guard.write_text("{}", encoding="utf-8")
    watcher = ProbeMetadataWatch([tmp_path], candidates=[], guard_paths=[guard])
    with pytest.raises(OSError):
        watcher.changed(tmp_path, [guard.name])


def test_selected_file_removal_is_expected_but_live_change_still_stops(tmp_path):
    candidate = tmp_path / "selected.fits"
    candidate.write_bytes(b"selected")
    watcher = ProbeMetadataWatch([tmp_path], candidates=[candidate])
    with pytest.raises(OSError):
        watcher.changed(tmp_path, [candidate.name])
    candidate.unlink()
    watcher.changed(tmp_path, [candidate.name])


def test_new_unrelated_live_run_does_not_hide_new_old_probe_reference(tmp_path):
    paths, _, _ = make_run(tmp_path / "data")
    p = planner(tmp_path)
    live = tmp_path / "data" / "live.json"
    live.write_text(json.dumps({"observationRunId": "new", "path": str(tmp_path / "data" / "new.fits")}), encoding="utf-8")
    assert p.preview_probes()
    live.write_text(json.dumps({"input": str(paths["bad"])}), encoding="utf-8")
    assert p.preview_probes() is None
    p.catalog.close()


def test_changed_indexed_unrelated_metadata_does_not_block_other_run(tmp_path):
    paths, _, _ = make_run(tmp_path / "data")
    live = tmp_path / "data" / "night.json"
    live.write_text('{"value": 1}', encoding="utf-8")
    p = planner(tmp_path)
    live.write_text('{"value": 22}', encoding="utf-8")
    assert p.preview_probes()
    live.write_text(json.dumps({"path": str(paths["bad"])}), encoding="utf-8")
    assert p.preview_probes() is None
    p.catalog.close()


def test_deleted_qhy_records_do_not_make_atr_disposable_or_mask_missing_atr_manifest(tmp_path):
    paths, manifest, _ = make_run(tmp_path / "data")
    qhy = tmp_path / "data" / "qhy" / "data" / "runs" / "UVEX-one" / "qhy-job" / "manifest.json"
    qhy.parent.mkdir(parents=True)
    qhy.write_text(json.dumps({"observationRunId": "one", "id": "job", "state": "Completed"}), encoding="utf-8")
    p = planner(tmp_path)
    qhy.unlink()
    p.catalog.scan()
    assert p.preview_probes()
    manifest.unlink()
    p.catalog.scan()
    assert p.preview_probes() is None
    assert paths["bad"].exists()
    p.catalog.close()


@pytest.mark.parametrize("run_id", ["UVEX-one", "0123456789abcdef0123456789abcdef"])
def test_removed_qhy_record_accepts_current_and_legacy_run_ids(tmp_path, run_id):
    path = tmp_path / "qhy" / "data" / "runs" / run_id / "qhy-job" / "manifest.json"
    row = {
        "path": os.path.normcase(str(path)), "missing": 1, "warning": "",
        "parser_version": PARSER_VERSION,
    }
    assert ProbeRetentionPlanner._removed_qhy_record(row)


@pytest.mark.skipif(os.name != "nt", reason="Windows change notifications and locked deletion")
@pytest.mark.parametrize("introduce_dependency", [False, True])
def test_physical_batch_allows_other_run_writes_but_stops_for_new_dependency(tmp_path, introduce_dependency):
    root = tmp_path / "data"
    make_run(root, run_id="one")
    make_run(root, run_id="two")
    p = planner(tmp_path)
    plan = p.preview_probes()
    selected = [i for i in plan["items"] if i["selected"]]
    assert len(selected) == 2
    def watch(roots, **kwargs):
        return ProbeMetadataWatch(roots, candidates=[i["path"] for i in selected],
            guard_paths=[g["path"] for g in plan["protection_documents"]], **kwargs)
    def progress(done, _total, _result):
        if done == 1:
            target = selected[1]["path"] if introduce_dependency else str(root / "new-exposure.fits")
            (root / "new-live.json").write_text(json.dumps({"input": target}), encoding="utf-8")
    receipt = CleanupExecutor(p.catalog, p, _watcher=watch).execute(plan, confirmation_phrase(plan), progress=progress)
    assert receipt["status"] == ("partial" if introduce_dependency else "completed"), receipt
    assert not Path(selected[0]["path"]).exists()
    assert Path(selected[1]["path"]).exists() == introduce_dependency
    p.catalog.close()


def test_automatic_cli_cannot_apply_failed_history_authorization():
    from uvex_reduce.library_probe_retention_cli import main
    with pytest.raises(SystemExit) as result:
        main(["--watch", "--include-failed-history", "--execute-permanent-rejected-probes"])
    assert result.value.code == 2


@pytest.mark.parametrize("arguments", [
    ["--watch", "--policy", "target-success"],
    ["--watch", "--policy", "target-success", "--execute-permanent-rejected-probes"],
    ["--watch", "--policy", "target-success", "--include-failed-history",
     "--execute-permanent-target-success"],
    ["--policy", "target-success", "--execute-permanent-target-success"],
    ["--policy", "terminal-target", "--execute-permanent-target-success"],
    ["--policy", "terminal-target", "--execute-permanent-terminal-target"],
    ["--watch", "--policy", "owner-space-reclaim",
     "--execute-permanent-owner-space-reclaim"],
    ["--policy", "owner-space-reclaim", "--execute-permanent-target-success"],
])
def test_target_success_watch_requires_its_own_explicit_authorization(arguments):
    from uvex_reduce.library_probe_retention_cli import main
    with pytest.raises(SystemExit) as result:
        main(arguments)
    assert result.value.code == 2


def test_target_success_enable_flag_requires_valid_utc_watermark(tmp_path):
    from uvex_reduce.library_probe_retention_cli import _enabled_time
    flag = tmp_path / "target-success-retention.enabled"
    flag.write_text(json.dumps({"enabledUtc": "not-a-time"}), encoding="utf-8")
    assert _enabled_time(flag) is None
    flag.write_text(json.dumps({"enabledUtc": "2026-11-06T13:00:00+00:00"}), encoding="utf-8")
    assert _enabled_time(flag).isoformat() == "2026-11-06T13:00:00+00:00"


def test_windows_usb_identity_is_not_a_question_mark_input_glob(tmp_path):
    from uvex_reduce.library_probe_retention_watch import mentions_inputs
    candidates = {os.path.normcase(str(tmp_path / "bad.fits"))}
    assert not mentions_inputs({"cameraIdentity": r"\\?\usb#vid_0000&pid_0000#<synthetic>"}, tmp_path, candidates)
    if os.name == "nt":
        assert mentions_inputs({"input": "\\\\?\\" + str(tmp_path / "bad.fits")}, tmp_path, candidates)


def test_file_uri_is_a_local_dependency_not_an_ignored_web_url(tmp_path):
    from uvex_reduce.library_probe_retention_watch import mentions_inputs
    path = tmp_path / "bad probe.fits"
    candidates = {os.path.normcase(str(path))}
    assert mentions_inputs({"input": path.as_uri()}, tmp_path, candidates)
    assert not mentions_inputs({"help": "https://example.test/manual"}, tmp_path, candidates)


@pytest.mark.skipif(os.name != "nt", reason="Windows notification pump")
@pytest.mark.parametrize("final", ["unrelated", "dependency", "partial"])
def test_in_progress_metadata_must_resolve_before_mutation(tmp_path, final):
    import time
    protected = tmp_path / "protected.fits"
    record = tmp_path / "new.json"
    with ProbeMetadataWatch([tmp_path], candidates=[protected]) as watch:
        record.write_text('{"input":', encoding="utf-8")
        deadline = time.monotonic() + 3
        while not watch._pending_metadata and time.monotonic() < deadline:
            time.sleep(0.02)
        assert watch._pending_metadata
        if final != "partial":
            record.write_text(json.dumps({"input": str(protected if final == "dependency"
                                                        else tmp_path / "new.fits")}), encoding="utf-8")
        if final == "unrelated":
            watch.check()
            assert not watch._pending_metadata
        else:
            with pytest.raises(OSError):
                watch.check()


@pytest.mark.skipif(os.name != "nt", reason="Windows notification pump")
def test_continuous_watch_drains_during_long_revalidation(tmp_path):
    import time
    with ProbeMetadataWatch([tmp_path], candidates=[tmp_path / "protected.fits"]) as watch:
        # More than 64 KiB of notifications, with no executor check in between.
        for batch in range(15):
            for number in range(60):
                (tmp_path / f"new-acquisition-{batch:02}-{number:02}-{'x' * 60}.fits").write_bytes(b"synthetic notification")
            time.sleep(0.12)
        watch.check()
        (tmp_path / "dependency.json").write_text(json.dumps({"input": str(tmp_path / "protected.fits")}), encoding="utf-8")
        time.sleep(0.2)
        with pytest.raises(OSError):
            watch.check()
