"""All physical deletions below use tiny synthetic pytest files only."""
import hashlib
import json
import os
from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.library_catalog import LibraryCatalog
from uvex_reduce.library_cleanup_execute import CleanupExecutor, confirmation_phrase, latest_file_receipt
from uvex_reduce.library_cleanup_plan import CleanupPlanner
from uvex_reduce.library_probe_retention import ProbeRetentionPlanner
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


def planner(tmp_path, **kwargs):
    catalog = LibraryCatalog(tmp_path / "catalog.sqlite3")
    catalog.add_root(tmp_path / "data")
    catalog.scan()
    return ProbeRetentionPlanner(catalog, authorized=True, **kwargs)


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
