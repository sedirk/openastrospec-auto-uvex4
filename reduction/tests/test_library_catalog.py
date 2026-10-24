from __future__ import annotations

import base64
import json
import os
from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.library_catalog import LibraryCatalog, SOURCE_POLICY_VERSION, _source, default_catalog_path


def write_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value), encoding="utf-8")


def raw(path: Path, **headers):
    path.parent.mkdir(parents=True, exist_ok=True)
    hdu = fits.PrimaryHDU(np.zeros((4, 4), dtype=np.uint16))
    for key, value in headers.items():
        hdu.header[key] = value
    hdu.writeto(path)
    return path


def manifest(path: Path, run_id="run-1", simulated=False, target="Test star"):
    write_json(path, {
        "simulator": simulated,
        "observationRunId": run_id,
        "plan": {"target": {"name": target},
                 "nightSetupId": "SIM-NIGHT" if simulated else "NIGHT-1",
                 "expectedAtrCameraId": "SIM-ATR" if simulated else "ATR585M"},
        "snapshot": {"state": "Completed"}, "terminalState": "Completed",
        "createdUtc": "2026-10-24T01:00:00Z",
    })


@pytest.fixture
def library(tmp_path):
    with LibraryCatalog(tmp_path / "index" / "catalog.sqlite3") as catalog:
        yield catalog


def test_default_database_uses_local_appdata(monkeypatch, tmp_path):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    assert default_catalog_path() == tmp_path / "UVEX-ADV" / "library" / "catalog.sqlite3"


@pytest.mark.parametrize(("table", "index_name"), [
    ("project_run_context", "project_context_source"),
    ("project_run_segments", "project_segments_source"),
])
def test_project_fact_cleanup_uses_source_file_index(library, table, index_name):
    # Every changed catalogue file invalidates its derived document facts. These
    # tables have run-first primary keys; without a source-file index a 177k-file
    # rescan repeatedly walks all night outcomes/segments (near-quadratic work).
    plan = library.connection.execute(
        f"EXPLAIN QUERY PLAN DELETE FROM {table} WHERE source_file_id=?", (1,)
    ).fetchall()
    details = " ".join(str(row[3]) for row in plan)
    assert "SEARCH" in details
    assert index_name in details
    assert "SCAN" not in details


@pytest.mark.parametrize(("payload", "expected"), [
    ({"plan": {"expectedAtrCameraId": "ATR", "expectedG3ProfileName": "G3",
               "expectedQhyCameraId": "QHY"}, "mode": "Real"}, "unknown"),
    ({"plan": {"expectedAtrCameraId": "ATR"}, "lockedMetadata": {"labels": {"adapter": "unknown"}},
      "gates": {"ValidateNightSetup": {"code": "TEST"}}}, "simulated"),
    ({"plan": {"expectedAtrCameraId": "ATR"},
      "gates": {"ValidateNightSetup": {"code": "FIXTURE_STAGE"}}}, "simulated"),
    ({"plan": {"expectedAtrCameraId": "OFFLINE-LOCAL-ATR"}}, "simulated"),
    ({"isSimulation": True, "plan": {"expectedAtrCameraId": "ATR585M"}}, "simulated"),
    ({"lockedMetadata": {"labels": {"adapter": "real"}}}, "real"),
    ({"plan": {"expectedAtrCameraId": "synthetic-usb#camera-family"}}, "real"),
])
def test_run_source_requires_execution_provenance_not_target_name_or_real_mode(payload, expected):
    payload["target"] = "An arbitrary science target, not a fixture-name heuristic"
    assert _source(payload) == expected


def test_source_policy_migrates_stale_v4_manifests_without_fits_reparse(library, tmp_path, monkeypatch):
    root = tmp_path / "data"
    path = root / "run" / "manifest.json"
    write_json(path, {"observationRunId": "old-fixture", "plan": {
        "target": {"name": "An arbitrary target"}, "expectedAtrCameraId": "ATR"},
        "gates": {"ValidateNightSetup": {"code": "FIXTURE_STAGE"}}, "terminalState": "Completed"})
    raw(root / "run" / "one.fits", OBSRUNID="old-fixture")
    library.add_root(root)
    library.scan()
    # Mimic a v4 catalogue created before the independent source-policy fix.
    library.connection.execute("UPDATE runs SET source='real' WHERE id='old-fixture'")
    library.connection.execute("UPDATE files SET source='real',source_policy_version=0 WHERE kind='metadata'")
    manifest_id = library.connection.execute("SELECT id FROM files WHERE kind='metadata'").fetchone()[0]
    indexed_raw = library.list_files(kind="frame")[0]
    library.connection.execute("INSERT OR REPLACE INTO frame_details VALUES(?,?,?)",
        (manifest_id, indexed_raw["path"], json.dumps({"run_id": "old-fixture", "source": "real"})))
    library.connection.commit()
    monkeypatch.setattr(fits, "getheader", lambda *a, **k: pytest.fail("Unchanged FITS was reparsed"))
    result = library.scan()
    assert result["files_parsed"] == 0
    assert result["source_policy"]["evaluated"] == result["source_policy"]["changed"] == 1
    assert library.list_runs()[0]["source"] == "simulated"
    assert library.connection.execute(
        "SELECT source_policy_version FROM files WHERE kind='metadata'").fetchone()[0] == SOURCE_POLICY_VERSION
    assert library.scan()["source_policy"]["evaluated"] == 0
    assert library.list_files(kind="frame")[0]["source"] == "simulated"


def test_source_policy_rechecks_unknown_once_and_recognizes_later_real_adapter(library, tmp_path):
    root = tmp_path / "data"
    path = root / "manifest.json"
    payload = {"observationRunId": "ambiguous", "plan": {"expectedAtrCameraId": "ATR"},
               "terminalState": "Completed"}
    write_json(path, payload)
    library.add_root(root)
    library.scan()
    library.connection.execute("UPDATE files SET source_policy_version=0")
    library.connection.commit()
    assert library.scan()["source_policy"]["evaluated"] == 1
    assert library.list_runs()[0]["source"] == "unknown"
    assert library.scan()["source_policy"]["evaluated"] == 0
    payload["lockedMetadata"] = {"labels": {"adapter": "real"}}
    write_json(path, payload)
    assert library.scan()["files_parsed"] == 1
    assert library.list_runs()[0]["source"] == "real"


def test_only_explicit_roots_are_scanned_and_physical_paths_not_copied(library, tmp_path):
    root = tmp_path / "raw"
    frame = raw(root / "one.fits", OBJECT="one", IMAGETYP="LIGHT")
    untouched = frame.read_bytes()
    raw(tmp_path / "outside" / "other.fits", OBJECT="outside")
    assert library.scan()["files_seen"] == 0
    library.add_root(root)
    assert library.scan()["files_seen"] == 1
    row = library.list_files()[0]
    assert Path(row["path"]) == frame
    assert row["target"] == "one"
    assert frame.read_bytes() == untouched


def test_incremental_scans_do_not_reparse_unchanged_headers(library, tmp_path, monkeypatch):
    root = tmp_path / "raw"
    raw(root / "one.fits", OBJECT="one")
    library.add_root(root)
    assert library.scan()["files_parsed"] == 1
    monkeypatch.setattr(fits, "getheader", lambda *a, **k: pytest.fail("Unchanged header read"))
    assert library.scan()["files_parsed"] == 0


def test_review_is_independent_persistent_and_refresh_does_not_erase_it(library, tmp_path):
    root = tmp_path / "raw"
    frame = raw(root / "one.fits", OBJECT="one")
    library.add_root(root)
    library.scan()
    file_id = library.list_files()[0]["id"]
    library.set_review(file_id, "reject", 2, "Cloud: keep as evidence")
    # Change metadata to force refresh, not the scientific input in production.
    frame.touch()
    library.scan()
    restored = library.get_file(file_id)
    assert (restored["review"], restored["rating"], restored["notes"]) == (
        "reject", 2, "Cloud: keep as evidence")
    with LibraryCatalog(library.db_path) as reopened:
        assert reopened.get_file(file_id)["review"] == "reject"


def test_review_validation(library):
    with pytest.raises(ValueError):
        library.set_review(1, "delete")
    with pytest.raises(ValueError):
        library.set_review(1, "pick", 6)


def test_real_simulated_and_unknown_do_not_depend_on_folder_name(library, tmp_path):
    root = tmp_path / "SIM-NIGHT"
    manifest(root / "actual" / "manifest.json", "real", target="A")
    manifest(root / "not-test" / "manifest.json", "sim", True, "B")
    raw(root / "unknown.fits", OBJECT="C")
    library.add_root(root)
    library.scan()
    assert library.count_files(source="real") == 1
    assert library.count_files(source="simulated") == 1
    assert library.count_files(source="unknown") == 1
    assert {r["id"]: r["source"] for r in library.list_runs()} == {
        "real": "real", "sim": "simulated"}


def test_run_context_log_links_and_encoded_target(library, tmp_path):
    root = tmp_path / "night"
    target = "织女星 α Lyr"
    encoded = base64.b64encode(target.encode()).decode()
    manifest(root / "run" / "manifest.json", target=target)
    raw(root / "run" / "raw" / "one.fits", OBJECT="ASCII alias", UVEXSTG="SCIENCE",
        INSTRUME="ATR585M", OBSRUNID="run-1", OBJENC="UTF8-B64", OBJCNT=1, OBJ0001=encoded)
    (root / "run" / "run.log").write_text("failed then recovered")
    library.add_root(root)
    library.scan()
    frame = library.list_files(kind="frame")[0]
    assert frame["target"] == target
    assert frame["channel"] == "spectroscopy"
    assert frame["role"] == "SCIENCE"
    assert any(path.endswith("run.log") for path in library.get_file(frame["id"])["related_paths"])


def test_fits_utf8_chunks_are_decoded_without_loading_pixels(library, tmp_path, monkeypatch):
    root = tmp_path / "night"
    text = "伴星 β"
    encoded = base64.b64encode(text.encode()).decode()
    raw(root / "one.fits", OBJECT="alias", OBJENC="UTF8-B64", OBJCNT=1, OBJ0001=encoded)
    monkeypatch.setattr(fits, "getdata", lambda *a, **k: pytest.fail("Pixel array was read"))
    library.add_root(root)
    library.scan()
    assert library.list_files()[0]["target"] == text


def test_qhy_complete_index_not_manifest_window_and_partial_line(library, tmp_path):
    root = tmp_path / "qhy"
    frames = [raw(root / "raw" / f"{i}.fits", OBJECT="fallback") for i in range(3)]
    write_json(root / "manifest.json", {
        "observationRunId": "qhy-run", "requestedTarget": "Star",
        "simulator": False,
        "expectedCameraStableId": "QHYminiCam", "state": "Completed", "frames": [],
    })
    entries = [{"frameId": f"frame-{i}", "fitsPath": str(frame), "role": role,
                "settings": {"exposureSeconds": 5},
                "exposureStartedUtc": "2026-10-24T00:01:00Z"}
               for i, (frame, role) in enumerate(zip(frames, ["ACQUISITION", "PHOTOMETRY-R",
                                                           "PHOTOMETRY-R"]))]
    (root / "frames.jsonl").write_text(
        "\n".join(json.dumps(item) for item in entries) + '\n{"frameId":', encoding="utf-8")
    library.add_root(root)
    result = library.scan()
    assert not result["cancelled"]
    indexed = library.list_files(kind="frame")
    assert len(indexed) == 3
    assert {x["channel"] for x in indexed} == {"photometry"}
    assert {x["source"] for x in indexed} == {"real"}
    assert {x["target"] for x in indexed} == {"fallback"}
    assert all("Identity conflict (target)" in x["warning"] for x in indexed)
    assert library.count_files(role="PHOTOMETRY-R") == 2
    assert library.count_files(date_prefix="2026-10-24", kind="frame") == 3
    row = next(x for x in library.list_files() if x["path"].endswith("frames.jsonl"))
    assert "possibly still writing" in row["warning"]


def test_evidence_acceptance_is_not_operator_review(library, tmp_path):
    root = tmp_path / "night"
    frame = raw(root / "one.fits", OBJECT="Star")
    write_json(root / "capture.json", {
        "observationRunId": "run", "fitsPath": str(frame), "captureId": "capture",
        "role": "ATR_PROBE", "qualityAccepted": False,
    })
    library.add_root(root)
    library.scan()
    row = library.list_files(kind="frame")[0]
    assert row["accepted"] == 0
    assert row["review"] == "unreviewed"
    library.set_review(row["id"], "pick", 4)
    assert library.get_file(row["id"])["accepted"] == 0


def test_shared_paths_and_overlapping_roots_count_storage_once(library, tmp_path):
    root = tmp_path / "night"
    frame = raw(root / "raw" / "one.fits")
    write_json(root / "a.json", {"inputs": [str(frame)]})
    write_json(root / "b.json", {"inputs": [str(frame)]})
    library.add_root(root)
    library.add_root(root / "raw")
    library.scan()
    assert library.count_files(kind="frame") == 1
    item = library.get_file(library.list_files(kind="frame")[0]["id"])
    assert len(item["references"]) == 2
    assert library.stats()["file_count"] == 3


def test_unknown_raw_and_missing_references_are_retained(library, tmp_path):
    root = tmp_path / "night"
    raw(root / "not-in-any-index.fits")
    write_json(root / "result.json", {"inputs": [str(root / "offline.fits")]})
    library.add_root(root)
    library.scan()
    assert library.count_files(kind="frame") == 1
    result = next(x for x in library.list_files() if x["path"].endswith("result.json"))
    assert library.get_file(result["id"])["references"][0]["status"] == "not-indexed"


def test_missing_file_keeps_review_and_reappears_with_same_id(library, tmp_path, monkeypatch):
    # Rapid scans may share a Windows clock tick; identity must not depend on time.
    monkeypatch.setattr("uvex_reduce.library_catalog.time.time_ns", lambda: 123456789)
    root = tmp_path / "raw"
    frame = raw(root / "one.fits")
    original = frame.read_bytes()
    library.add_root(root)
    library.scan()
    file_id = library.list_files()[0]["id"]
    library.set_review(file_id, "pick", 5, "retain")
    frame.unlink()  # generated fixture only
    library.scan()
    assert library.get_file(file_id)["status"] == "missing"
    frame.write_bytes(original)
    library.scan()
    assert library.get_file(file_id)["status"] == "available"
    assert library.get_file(file_id)["review"] == "pick"


def test_offline_root_and_cancel_do_not_mark_files_missing(library, tmp_path):
    root = tmp_path / "raw"
    frame = raw(root / "one.fits")
    library.add_root(root)
    library.scan()
    file_id = library.list_files()[0]["id"]
    frame.unlink()
    result = library.scan(cancelled=lambda: True)
    assert result["cancelled"]
    assert library.get_file(file_id)["missing"] == 0
    root.rmdir()
    assert library.scan()["warnings"]
    assert library.get_file(file_id)["missing"] == 0


def test_corrupt_json_and_fits_are_indexed_as_warning_not_lost(library, tmp_path):
    root = tmp_path / "raw"
    root.mkdir()
    (root / "bad.fits").write_bytes(b"not fits")
    (root / "manifest.json").write_text("{broken", encoding="utf-8")
    library.add_root(root)
    assert library.scan()["files_seen"] == 2
    assert all(row["warning"] for row in library.list_files())


def test_pagination_filters_and_literal_query(library, tmp_path):
    root = tmp_path / "raw"
    for i in range(9):
        raw(root / f"{i}.fits", OBJECT="star_100%" if i == 0 else "star")
    library.add_root(root)
    library.scan()
    one = library.list_files(offset=0, limit=3, sort="path")
    two = library.list_files(offset=3, limit=3, sort="path")
    assert len(one) == len(two) == 3
    assert not {x["id"] for x in one} & {x["id"] for x in two}
    assert library.count_files(query="_100%") == 1
    assert library.stats()["total_bytes"] == sum(x["size_bytes"] for x in library.list_files())


def test_symlinks_are_not_followed_or_accepted_as_roots(library, tmp_path):
    root = tmp_path / "root"
    root.mkdir()
    outside = tmp_path / "outside"
    raw(outside / "raw.fits")
    link = root / "linked"
    try:
        link.symlink_to(outside, target_is_directory=True)
    except OSError:
        pytest.skip("Symlink creation requires developer mode/admin on this Windows host")
    library.add_root(root)
    assert library.scan()["files_seen"] == 0
    with pytest.raises(ValueError):
        library.add_root(link)


def test_progress_callback_and_bounded_cancel(library, tmp_path):
    root = tmp_path / "raw"
    for i in range(10):
        raw(root / f"{i}.fits")
    library.add_root(root)
    updates = []
    result = library.scan(progress=updates.append, cancelled=lambda: bool(updates))
    assert result["cancelled"]
    assert result["files_seen"] < 10
    assert updates[-1]["cancelled"]


@pytest.mark.skipif(os.name != "nt", reason="Windows case-insensitive path contract")
def test_windows_mixed_case_frame_references_join_canonical_file(library, tmp_path):
    root = tmp_path / "MixedCASE"
    frame = raw(root / "RawFrames" / "CaptureOne.FITS", IMAGETYP="LIGHT")
    root.joinpath("frames.jsonl").write_text(json.dumps({
        "fitsPath": str(frame).swapcase(), "role": "PHOTOMETRY-R",
        "frameId": "mixed", "settings": {"exposureSeconds": 7},
    }), encoding="utf-8")
    library.add_root(root)
    library.scan()
    row = library.list_files(kind="frame")[0]
    assert row["role"] == "PHOTOMETRY-R"
    assert row["capture_id"] == "mixed"
    assert library.get_file(row["id"])["references"][0]["status"] == "available"


def test_changed_header_replaces_derived_facts_but_not_manual_notes(library, tmp_path):
    root = tmp_path / "raw"
    frame = raw(root / "one.fits", OBJECT="before", UVEXCID="old", UVEXSTG="PROBE")
    library.add_root(root)
    library.scan()
    file_id = library.list_files()[0]["id"]
    library.set_review(file_id, "pick", 4, "operator fact")
    with fits.open(frame, mode="update") as hdus:  # generated fixture only
        hdus[0].header["OBJECT"] = "after"
        hdus[0].header["UVEXSTG"] = "SCIENCE"
        del hdus[0].header["UVEXCID"]
    library.scan()
    row = library.get_file(file_id)
    assert row["target"] == "after"
    assert row["capture_id"] == ""
    assert row["role"] == "SCIENCE"
    assert row["notes"] == "operator fact"


def test_get_file_limits_large_run_related_paths(library, tmp_path):
    root = tmp_path / "run"
    manifest(root / "manifest.json")
    raw(root / "one.fits")
    for i in range(12):
        (root / f"log-{i}.log").write_text("related")
    library.add_root(root)
    library.scan()
    item = library.get_file(library.list_files(kind="frame")[0]["id"], related_limit=5)
    assert len(item["related_paths"]) == 5
    assert item["related_paths_truncated"]


def test_explicit_fits_identity_is_never_relabelled_from_folder_or_qhy_context(library, tmp_path):
    root = tmp_path / "run"
    manifest(root / "manifest.json", "folder-run", target="A")
    first = raw(root / "raw" / "a.fits", OBJECT="A", OBSRUNID="folder-run")
    other = raw(root / "raw" / "b.fits", OBJECT="B", OBSRUNID="actual-B")
    (root / "frames.jsonl").write_text("\n".join(json.dumps({
        "fitsPath": str(path), "role": "PHOTOMETRY-R", "frameId": str(i),
    }) for i, path in enumerate([first, other])), encoding="utf-8")
    library.add_root(root)
    library.scan()
    library.scan()  # protected identities must also survive unchanged-index refresh
    rows = {row["target"]: row for row in library.list_files(kind="frame")}
    assert set(rows) == {"A", "B"}
    assert rows["B"]["run_id"] == "actual-B"
    assert "Identity conflict (target)" in rows["B"]["warning"]
    assert "Identity conflict (run_id)" in rows["B"]["warning"]
    assert rows["A"]["warning"] == ""


@pytest.mark.parametrize("reverse", [False, True])
def test_master_run_state_not_overwritten_by_qhy_manifest(library, tmp_path, reverse):
    master = tmp_path / ("z-master" if reverse else "a-master")
    qhy = tmp_path / ("a-qhy" if reverse else "z-qhy")
    manifest(master / "manifest.json", "same-run", target="Master target")
    payload = json.loads((master / "manifest.json").read_text())
    payload["terminalReason"] = "Science frames committed"
    write_json(master / "manifest.json", payload)
    write_json(qhy / "manifest.json", {
        "observationRunId": "same-run", "requestedTarget": "QHY alias",
        "expectedCameraStableId": "QHYminiCam", "state": "Completed",
        "createdUtc": "2026-10-24T01:01:00Z",
    })
    library.add_root(master)
    library.add_root(qhy)
    library.scan()
    # A later QHY job revision cannot erase the master terminal reason/path.
    write_json(qhy / "manifest.json", {
        "observationRunId": "same-run", "requestedTarget": "QHY alias updated",
        "expectedCameraStableId": "QHYminiCam", "state": "Running",
    })
    library.scan()
    run = library.list_runs()[0]
    assert run["target"] == "Master target"
    assert run["state"] == "Completed"
    assert run["reason"] == "Science frames committed"
    assert run["created_utc"] == "2026-10-24T01:00:00Z"
    assert Path(run["manifest_path"]) == master / "manifest.json"


@pytest.mark.parametrize("value,expected", [(False, 0), ("False", 0), ("True", 1),
                                            (0, 0), (1, 1), ("indeterminate", None)])
def test_manifest_evidence_quality_is_exact_per_frame_and_strict_bool(
    library, tmp_path, value, expected,
):
    root = tmp_path / "run"
    one = raw(root / "one.fits", OBJECT="Test star")
    raw(root / "other.fits", OBJECT="Test star")
    manifest(root / "manifest.json")
    payload = json.loads((root / "manifest.json").read_text())
    payload["evidence"] = [{
        "kind": "atr-probe-fits", "absolutePath": str(one),
        "metadata": {"role": "SNAPSHOT", "stageRole": "PROBE", "captureId": "one",
                     "qualityAccepted": value, "qualityCode": "SNR_LOW"},
    }]
    write_json(root / "manifest.json", payload)
    library.add_root(root)
    library.scan()
    rows = {Path(row["path"]).name: row for row in library.list_files(kind="frame")}
    assert rows["one.fits"]["accepted"] == expected
    assert rows["one.fits"]["role"] == "PROBE"
    assert rows["other.fits"]["accepted"] is None
    assert library.get_file(rows["one.fits"]["id"])["metadata"]["quality"]["qualityCode"] == "SNR_LOW"


def test_nested_pipeline_inputs_products_and_frame_selection_references(library, tmp_path):
    root = tmp_path / "result"
    root.mkdir()
    write_json(root / "manifest.json", {
        "inputs": {"science": ["raw/science.fits"], "bias": ["calibration/bias.fits"]},
        "artifacts": {"fits": "result/spectrum.fits", "csv": "result/spectrum.csv"},
        "frameSelection": {"accepted": ["raw/accepted.fits"],
                           "rejected": [{"path": "raw/rejected.fits", "reason": "LOW_SNR"}]},
        "quality": {"message": "Not a file", "code": "LOW_SNR"},
    })
    library.add_root(root)
    library.scan()
    item = library.get_file(library.list_files()[0]["id"])
    refs = {Path(ref["target_path"]).name for ref in item["references"]}
    assert refs == {"science.fits", "bias.fits", "spectrum.fits", "spectrum.csv",
                    "accepted.fits", "rejected.fits"}


def test_g3_channel_from_evidence_contract_without_camera_header(library, tmp_path):
    root = tmp_path / "run"
    frame = raw(root / "one.fits", OBJECT="Test star")
    manifest(root / "manifest.json")
    value = json.loads((root / "manifest.json").read_text())
    value["evidence"] = [{"kind": "g3-phd2-raw-fits", "absolutePath": str(frame)}]
    write_json(root / "manifest.json", value)
    library.add_root(root)
    library.scan()
    assert library.list_files(kind="frame")[0]["channel"] == "slit"


def test_changed_sidecar_removes_old_acceptance_but_preserves_review(library, tmp_path):
    root = tmp_path / "run"
    frame = raw(root / "one.fits", OBJECT="Test star", UVEXSTG="PROBE")
    sidecar = root / "capture.json"
    write_json(sidecar, {"fitsPath": str(frame), "role": "SCIENCE", "qualityAccepted": True})
    library.add_root(root)
    library.scan()
    file_id = library.list_files(kind="frame")[0]["id"]
    library.set_review(file_id, "pick", 3, "own note")
    write_json(sidecar, {"fitsPath": str(frame), "captureId": "new-unknown-quality"})
    library.scan()
    row = library.get_file(file_id)
    assert row["accepted"] is None
    assert row["role"] == "PROBE"
    assert row["review"] == "pick"
    assert row["notes"] == "own note"


def test_catalog_paging_and_run_lookup_have_covering_query_indexes(library):
    queries = [
        ("SELECT id,path FROM files ORDER BY mtime_ns DESC,id LIMIT 100", (), "files_date"),
        ("SELECT id,path FROM files WHERE kind=? ORDER BY mtime_ns DESC,id LIMIT 100",
         ("frame",), "files_kind_date"),
        ("SELECT file_id FROM file_runs WHERE run_id=?", ("run",), "file_runs_run"),
        ("SELECT DISTINCT target_path FROM links WHERE run_id=? LIMIT 501", ("run",), "links_run"),
    ]
    for sql, values, expected in queries:
        detail = " ".join(row[3] for row in library.connection.execute(
            "EXPLAIN QUERY PLAN " + sql, values))
        assert expected in detail
        assert "TEMP B-TREE" not in detail


def test_review_stats_sql_grouping_retains_all_decisions(library, tmp_path):
    root = tmp_path / "raw"
    for i in range(5):
        raw(root / f"{i}.fits")
    library.add_root(root)
    library.scan()
    rows = library.list_files(sort="path")
    library.set_review(rows[0]["id"], "pick")
    library.set_review(rows[1]["id"], "pick")
    library.set_review(rows[2]["id"], "reject")
    assert library.stats()["review"] == {"pick": 2, "reject": 1, "unreviewed": 2}
