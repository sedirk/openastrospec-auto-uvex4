from __future__ import annotations

import json
from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.library_catalog import LibraryCatalog
from uvex_reduce.library_projects import LibraryProjects, observing_night
from uvex_reduce.library_selection import export_library_project


def document(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def frame(path, run_id, role="SCIENCE", target="HD 18552"):
    path.parent.mkdir(parents=True, exist_ok=True)
    hdu = fits.PrimaryHDU(np.ones((4, 8), dtype=np.uint16))
    for key, value in {"OBJECT": target, "OBSRUNID": run_id, "UVEXSTG": role,
                       "INSTRUME": "ATR585M", "EXPTIME": 600}.items():
        hdu.header[key] = value
    hdu.writeto(path)
    return path


def run(root, run_id="one", state="Completed", source="real", target="HD 18552", frames=()):
    entries = [{"kind": "AtrScienceFrame", "absolutePath": str(path), "metadata": {
        "observationRunId": run_id, "targetName": target, "stageRole": role,
        "qualityAccepted": accepted, "exposureSeconds": 600,
    }} for path, role, accepted in frames]
    return document(root / run_id / "manifest.json", {
        "simulator": source == "simulated",
        "observationRunId": run_id,
        "plan": {"target": {"name": target}, "nightSetupId": "NIGHT",
                 "expectedAtrCameraId": "OFFLINE-LOCAL-ATR" if source == "simulated" else "ATR585M"},
        "createdUtc": "2026-10-22T01:15:00Z", "terminalState": state,
        "terminalReason": "recorded reason", "evidence": entries,
        "snapshot": {"state": state, "recentEvents": [{
            "timestampUtc": "2026-10-22T01:16:00Z", "stage": "Science",
            "code": "SCIENCE_DONE", "message": "Recorded event"}]},
    })


@pytest.fixture
def catalog(tmp_path):
    with LibraryCatalog(tmp_path / "index.sqlite3") as value:
        yield value


def scan(catalog, root):
    catalog.add_root(root)
    assert not catalog.scan()["warnings"]
    return LibraryProjects(catalog)


def test_run_identity_not_target_name_and_explicit_acceptance(catalog, tmp_path):
    root = tmp_path / "data"
    a = frame(root / "one" / "science.fits", "one")
    b = frame(root / "one" / "probe.fits", "one", "PROBE")
    c = frame(root / "two" / "science.fits", "two")
    run(root, frames=[(a, "SCIENCE", "True"), (b, "PROBE", "True")])
    run(root, "two", "Faulted", frames=[(c, "SCIENCE", "False")])
    projects = scan(catalog, root)
    rows = {row["run_id"]: row for row in projects.list_projects()}
    assert len(rows) == 2
    assert rows["one"]["science_frames"] == rows["one"]["accepted_frames"] == 1
    assert rows["one"]["probe_frames"] == 1
    assert rows["one"]["exposure_seconds"] == 600
    assert rows["two"]["accepted_frames"] == 0
    assert rows["two"]["outcome"] == "failed"
    assert projects.count_projects(query="HD 18552", outcome="failed") == 1
    assert [Path(r["path"]) for r in projects.select_science("run:one")] == [a]
    assert projects.get_project("run:one")["timeline"][0]["code"] == "SCIENCE_DONE"


def test_completed_is_not_claim_of_scientific_success_and_failed_no_raw_is_listed(catalog, tmp_path):
    root = tmp_path / "data"
    run(root, "empty", "Faulted")
    unassessed = frame(root / "done" / "unknown.fits", "done", "LIGHT")
    run(root, "done")
    projects = scan(catalog, root)
    empty = projects.get_project("run:empty")
    assert empty["file_count"] == 1
    assert empty["science_frames"] == 0
    done = projects.get_project("run:done")
    assert done["outcome_label"] == "流程完成"
    assert done["science_frames"] == 1 and done["accepted_frames"] == 0
    assert projects.select_science(done["id"]) == []
    assert unassessed.exists()


def test_offline_expected_device_is_simulated_even_in_production_named_folder(catalog, tmp_path):
    root = tmp_path / "observations"
    run(root, "simulation", source="simulated")
    run(root, "sky")
    projects = scan(catalog, root)
    assert [r["run_id"] for r in projects.list_projects(source="simulated")] == ["simulation"]
    assert [r["run_id"] for r in projects.list_projects(source="real")] == ["sky"]


def test_saved_night_and_explicit_recovery_segments_preserve_frames(catalog, tmp_path):
    root = tmp_path / "data"
    a = frame(root / "first" / "one.fits", "first")
    b = frame(root / "continuation" / "two.fits", "continuation")
    run(root, "first", "Faulted", frames=[(a, "SCIENCE", True)])
    run(root, "continuation", frames=[(b, "SCIENCE", True)])
    document(root / "night.json", {"deadlineSchedule": {"ObservingNightDate": "2026-10-20"},
        "outcomes": [{"runId": "continuation", "target": "HD 18552", "outcome": "Completed",
                      "retainedScience": {"requestedFrames": 2, "acceptedFrames": 2,
                          "acceptedExposureSeconds": 1200,
                          "segmentRunIds": ["first", "continuation", "first"]}}]})
    projects = scan(catalog, root)
    rows = projects.list_projects()
    assert len(rows) == 1
    assert rows[0]["night"] == "2026-10-20"
    assert rows[0]["science_frames"] == rows[0]["accepted_frames"] == 2
    assert rows[0]["logged_accepted_frames"] == 2
    assert len(projects.select_science(rows[0]["id"])) == 2
    assert len(projects.get_project(rows[0]["id"])["segment_run_ids"]) == 2


def test_rejected_is_only_review_original_remains_and_not_handed_off(catalog, tmp_path):
    root = tmp_path / "data"
    raw = frame(root / "one" / "one.fits", "one")
    original = raw.read_bytes()
    run(root, frames=[(raw, "SCIENCE", True)])
    projects = scan(catalog, root)
    file_id = projects.select_science("run:one")[0]["id"]
    catalog.set_review(file_id, "reject", notes="Cloud")
    detail = projects.get_project("run:one")
    assert detail["accepted_frames"] == 1  # acquisition quality, not human culling
    assert detail["rejected_files"] == 1
    assert projects.select_science("run:one") == []
    assert "未删除" in detail["review_note"]
    assert raw.read_bytes() == original


def test_probe_reused_as_science_one_physical_file_not_double_counted(catalog, tmp_path):
    root = tmp_path / "data"
    raw = frame(root / "one" / "probe.fits", "one", "PROBE")
    path = run(root, frames=[(raw, "PROBE", True)])
    payload = json.loads(path.read_text())
    promoted = dict(payload["evidence"][0])
    promoted["metadata"] = {**promoted["metadata"], "effectiveStageRole": "SCIENCE",
                            "sourceCaptureRole": "PROBE", "scienceReusedFromProbe": "True"}
    payload["evidence"].append(promoted)
    document(path, payload)
    projects = scan(catalog, root)
    summary = projects.list_projects()[0]
    assert summary["science_frames"] == summary["accepted_frames"] == 1
    assert summary["probe_frames"] == 0
    assert len(projects.select_science(summary["id"])) == 1


def test_project_exact_selection_links_runs_not_same_target_or_shared_calibration(catalog, tmp_path):
    root = tmp_path / "data"
    a = frame(root / "one" / "a.fits", "one")
    b = frame(root / "two" / "b.fits", "two")
    run(root, "one", frames=[(a, "SCIENCE", True)])
    run(root, "two", frames=[(b, "SCIENCE", True)])
    projects = scan(catalog, root)
    saved = export_library_project(tmp_path / "projects" / "one.astroproj", name="One",
        data_root=root, output_root=tmp_path / "products", frames=[
            {"path": str(a), "target": "HD 18552", "role": "science"},
            {"path": str(b), "target": "", "role": "bias"}])
    roots_before = catalog.list_roots()
    projects.index_project(saved)
    assert catalog.list_roots() == roots_before  # no implicit broad scan root
    assert projects.get_project("run:one")["reduction_stage"] == "project"
    assert projects.get_project("run:two")["reduction_stage"] == "raw"
    linked = projects.get_project("run:one")["reduction_projects"]
    assert Path(linked[0]["path"]) == saved
    assert linked[0]["name"] == "One"
    assert projects.count_projects() == 2  # attached project is not duplicated


def test_reduction_result_uses_science_inputs_not_shared_bias(catalog, tmp_path):
    root = tmp_path / "data"
    a = frame(root / "one" / "a.fits", "one")
    b = frame(root / "two" / "b.fits", "two")
    run(root, "one", frames=[(a, "SCIENCE", True)])
    run(root, "two", frames=[(b, "SCIENCE", True)])
    product = root / "products" / "one_spectrum.csv"
    product.parent.mkdir(parents=True)
    product.write_text("wavelength,flux\n4000,1\n")
    document(root / "products" / "one_run.json", {"target": "Different old FITS name",
        "inputs": {"science": [str(a)], "bias": [str(b)]},
        "wavelength": {"status": "calibrated", "rmsAngstrom": 0.5},
        "artifacts": {"spectrum": str(product)}})
    projects = scan(catalog, root)
    assert projects.get_project("run:one")["reduction_stage"] == "wavelength"
    assert projects.get_project("run:two")["reduction_stage"] == "raw"
    result = projects.get_project("run:one")["products"][0]
    assert result["details"]["wavelength"]["rmsAngstrom"] == 0.5
    assert any(Path(r["target_path"]) == product for r in result["references"])


def test_registered_project_refresh_only_exact_file(catalog, tmp_path):
    root = tmp_path / "elsewhere"
    saved = document(root / "one.astroproj", {"schema_version": 1, "name": "Before"})
    document(root / "do-not-index.json", {"observationRunId": "unwanted"})
    projects = LibraryProjects(catalog)
    projects.index_project(saved)
    assert projects.count_projects() == 1
    document(saved, {"schema_version": 1, "name": "After changed", "last_output": "one_run.json"})
    assert projects.refresh_registered_projects() == 1
    row = projects.list_projects()[0]
    assert row["target"] == "After changed"
    assert catalog.count_files() == 1


def test_utc8_noon_fallback_is_not_utc_date_or_machine_clock():
    assert observing_night("2026-10-22T01:15:00Z") == "2026-10-21"
    assert observing_night("2026-10-22T13:15:00+00:00") == "2026-10-22"
    assert observing_night("") == ""
    assert observing_night("2026-10-22T01:15:00") == ""


@pytest.mark.parametrize("saved", [None, "None", "null", "2026-99-99", "not a date"])
def test_null_and_legacy_invalid_saved_night_use_recorded_time_fallback(catalog, tmp_path, saved):
    root = tmp_path / "data"
    run(root)
    document(root / "night.json", {"deadlineSchedule": {"ObservingNightDate": saved},
        "outcomes": [{"runId": "one", "target": "HD 18552", "outcome": "Completed"}]})
    model = scan(catalog, root)
    assert catalog.connection.execute("SELECT night FROM project_run_context").fetchone()[0] == ""
    # The running old scanner may already have persisted str(None). The query
    # projection must repair that display without needing another whole scan.
    catalog.connection.execute("UPDATE project_run_context SET night=?", (str(saved),))
    row = model.list_projects()[0]
    assert row["night"] == "2026-10-21"
    assert row["night_basis"] == "UTC+8 noon fallback"


def test_unreparsed_old_real_source_is_unknown_not_a_real_sky_claim(catalog, tmp_path):
    root = tmp_path / "data"
    manifest_path = run(root, "any-name", target="Arbitrary target", source="simulated")
    model = scan(catalog, root)
    catalog.connection.execute("UPDATE files SET parser_version=3,source='real' WHERE path=?",
                               (str(manifest_path).lower(),))
    catalog.connection.execute("UPDATE runs SET source='real' WHERE id='any-name'")
    assert model.list_projects(source="real") == []
    row = model.list_projects(source="unknown")[0]
    assert row["target"] == "Arbitrary target"
    assert row["state"] == "Completed"  # retained, not deleted by a name heuristic
    # Once a current-parser manifest supplies the explicit OFFLINE identity,
    # trust that identity even if a legacy runs row has not yet been reconciled.
    catalog.connection.execute("UPDATE files SET parser_version=4,source='simulated' WHERE path=?",
                               (str(manifest_path).lower(),))
    assert model.list_projects(source="real") == []
    assert model.list_projects(source="simulated")[0]["run_id"] == "any-name"


def test_qhy_offline_stable_id_does_not_become_real(catalog, tmp_path):
    root = tmp_path / "qhy"
    document(root / "manifest.json", {"observationRunId": "qhy-offline",
        "requestedTarget": "Arbitrary", "expectedCameraStableId": "OFFLINE-LOCAL-QHY",
        "state": "Completed"})
    model = scan(catalog, root)
    assert model.list_projects(source="real") == []
    assert model.list_projects(source="simulated")[0]["run_id"] == "qhy-offline"
