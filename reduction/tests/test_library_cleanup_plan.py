from __future__ import annotations

import json
import os
from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.library_catalog import LibraryCatalog
from uvex_reduce.library_cleanup_plan import CleanupPlanner


def document(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def qhy_project(root, run_id="one", *, state="Completed", job_state="Completed", role="PHOTOMETRY-R"):
    folder = root / run_id
    raw = folder / "qhy-1" / "raw" / "one.fits"
    raw.parent.mkdir(parents=True)
    hdu = fits.PrimaryHDU(np.ones((8, 8), dtype=np.uint16))
    hdu.header["OBSRUNID"] = run_id
    hdu.header["UVEXSTG"] = role
    hdu.writeto(raw)
    preview = folder / "qhy-1" / "preview" / "one.png"
    preview.parent.mkdir()
    preview.write_bytes(b"generated preview" * 32)
    record = {"frameId": "frame-1", "fitsPath": str(raw), "previewPath": str(preview), "role": role}
    index = folder / "qhy-1" / "frames.jsonl"
    index.write_text(json.dumps(record) + "\n", encoding="utf-8")
    document(folder / "qhy-1" / "manifest.json", {
        "id": "job-1", "observationRunId": run_id, "requestedTarget": "Star",
        "state": job_state, "completedUtc": "2026-10-24T01:00:00Z", "frames": [record],
        "expectedCameraStableId": "QHYMINICAM-123",
    })
    document(folder / "manifest.json", {
        "observationRunId": run_id, "simulator": False,
        "plan": {"target": {"name": "Star"}}, "terminalState": state,
        "snapshot": {"state": state}, "createdUtc": "2026-10-24T00:00:00Z",
    })
    return raw, preview, index


@pytest.fixture
def catalog(tmp_path):
    with LibraryCatalog(tmp_path / "catalog.sqlite3") as value:
        yield value


def scan(catalog, root):
    catalog.add_root(root)
    result = catalog.scan()
    assert not result["warnings"]
    return CleanupPlanner(catalog)


def by_path(plan):
    return {Path(item["path"]).name: item for item in plan["items"]}


def test_terminal_project_exact_preview_and_raw_protection(catalog, tmp_path):
    root = tmp_path / "data"
    raw, preview, _ = qhy_project(root)
    planner = scan(catalog, root)
    rows = planner.inventory()
    row = next(row for row in rows if row["project_id"] == "run:one")
    assert row["candidate_count"] == 1
    assert row["candidate_bytes"] == preview.stat().st_size
    plan = planner.preview(["run:one"], "permanent")
    items = by_path(plan)
    assert items["one.fits"]["protected"]
    assert not items["one.fits"]["selected"]
    assert items["one.png"]["candidate"] and items["one.png"]["selected"]
    assert plan["estimated_release_bytes"] == preview.stat().st_size
    assert planner.revalidate(plan)["valid"]
    assert raw.exists() and preview.exists()


@pytest.mark.parametrize("action", ["mark", "recycle"])
def test_mark_recycle_do_not_claim_free_space(catalog, tmp_path, action):
    root = tmp_path / "data"
    qhy_project(root)
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], action)
    assert plan["estimated_release_bytes"] == 0
    if action == "mark":
        assert not any(item["selected"] for item in plan["items"])
        raw_id = next(item["id"] for item in plan["items"] if item["category"] == "raw")
        explicit = planner.preview(["run:one"], action, [raw_id])
        raw = next(item for item in explicit["items"] if item["id"] == raw_id)
        assert raw["selected"] and raw["protected"] and raw["actionable"]
        assert planner.revalidate(explicit)["valid"]


@pytest.mark.parametrize(("state", "job_state"), [("Running", "Completed"), ("Completed", "Running"),
                                                 ("Paused", "Cancelled"), ("Completed", "TakenOver")])
def test_active_main_or_camera_job_protects_preview(catalog, tmp_path, state, job_state):
    root = tmp_path / "data"
    qhy_project(root, state=state, job_state=job_state)
    planner = scan(catalog, root)
    assert not any(row["candidate_bytes"] for row in planner.inventory())
    assert all(not item["selected"] for item in planner.preview(["run:one"])["items"])


@pytest.mark.parametrize("role", ["SCIENCE", "PROBE", "PHOTOMETRY-R", "DARK", "FLAT", "ARC"])
def test_all_raw_roles_protected_even_operator_rejects(catalog, tmp_path, role):
    root = tmp_path / "data"
    raw, _, _ = qhy_project(root, role=role)
    planner = scan(catalog, root)
    identifier = catalog.connection.execute("SELECT id FROM files WHERE path=?", (str(raw).lower(),)).fetchone()
    if identifier is None:  # POSIX canonical paths retain case.
        identifier = catalog.connection.execute("SELECT id FROM files WHERE kind='frame'").fetchone()
    catalog.set_review(identifier[0], "reject")
    plan = planner.preview(["run:one"], "permanent", [identifier[0]])
    selected = next(item for item in plan["items"] if item["selected"])
    assert selected["protected"]
    assert not planner.revalidate(plan)["valid"]


def test_extension_and_failed_run_are_not_cache_evidence(catalog, tmp_path):
    root = tmp_path / "data"
    qhy_project(root, state="Faulted")
    unrelated = root / "one" / "pretty.png"
    unrelated.write_bytes(b"not proven disposable")
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    assert by_path(plan)["pretty.png"]["protected"]
    assert by_path(plan)["pretty.png"]["estimated_release_bytes"] == 0


def test_reduction_input_and_legacy_data_root_protects_preview(catalog, tmp_path):
    root = tmp_path / "data"
    _, preview, _ = qhy_project(root)
    document(root / "reduction.astroproj", {
        "schemaVersion": 1, "name": "saved", "dataRoot": str(preview.parent),
        "parameters": {"librarySelection": {"version": 1, "frames": [{"path": str(preview), "role": "science"}]}},
    })
    planner = scan(catalog, root)
    selected = next(item for item in planner.preview(["run:one"])["items"] if item["path"] == str(preview).lower())
    assert selected["protected"]
    assert "reduction_or_legacy_input_dependency" in selected["reasons"]


def test_runtime_model_reference_protects_output_link(catalog, tmp_path):
    root = tmp_path / "data"
    _, preview, _ = qhy_project(root)
    document(root / "local-model.json", {"evidence": [{"absolutePath": str(preview)}]})
    planner = scan(catalog, root)
    assert not next(row for row in planner.inventory() if row["id"] == "run:one")["candidate_bytes"]


def test_runtime_model_inside_run_is_not_its_producer_output(catalog, tmp_path):
    root = tmp_path / "data"
    _, preview, _ = qhy_project(root)
    # Run context is inherited by all JSON in this directory. Sharing a run ID
    # does not turn the model's input/evidence link into disposable provenance.
    model = document(root / "one" / "local-model.json", {"evidence": [{"absolutePath": str(preview)}]})
    planner = scan(catalog, root)
    row = catalog.connection.execute("SELECT run_id FROM files WHERE path=?", (os.path.normcase(str(model)),)).fetchone()
    assert row[0] == "one"
    item = by_path(planner.preview(["run:one"], "permanent"))["one.png"]
    assert item["protected"] and "referenced_dependency_or_runtime_model" in item["reasons"]


def test_exact_main_manifest_preview_output_does_not_create_false_dependency(catalog, tmp_path):
    root = tmp_path / "data"
    _, preview, _ = qhy_project(root)
    manifest = root / "one" / "manifest.json"
    payload = json.loads(manifest.read_text())
    payload["evidence"] = [{"kind": "QhyPreview", "absolutePath": str(preview)}]
    document(manifest, payload)
    planner = scan(catalog, root)
    item = by_path(planner.preview(["run:one"], "permanent"))["one.png"]
    assert item["candidate"] and item["selected"]


def test_changed_raw_metadata_and_catalog_invalidate_plan(catalog, tmp_path):
    root = tmp_path / "data"
    raw, _, _ = qhy_project(root)
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    raw.touch()
    result = planner.revalidate(plan)
    assert not result["valid"]
    assert "dependency_proof_or_paired_raw_changed" in result["reasons"]


def test_modified_or_forged_plan_rejected(catalog, tmp_path):
    root = tmp_path / "data"
    qhy_project(root)
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    plan["items"][0]["protected"] = False
    assert not planner.revalidate(plan)["valid"]


def test_new_worker_connection_can_revalidate_issued_plan(catalog, tmp_path):
    root = tmp_path / "data"
    qhy_project(root)
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    with LibraryCatalog(catalog.db_path) as other:
        second = CleanupPlanner(other)
        assert second.revalidate(plan)["valid"]
        item = next(item for item in plan["items"] if item["selected"])
        assert second.revalidate_item(plan, item)["valid"]


def test_inventory_unique_totals_and_unattributed_bucket(catalog, tmp_path):
    root = tmp_path / "data"
    qhy_project(root)
    (root / "not-associated.log").write_bytes(b"retained log")
    planner = scan(catalog, root)
    rows = planner.inventory()
    assert any(row["project_id"] == "unattributed" for row in rows)
    assert planner.inventory_totals["indexed_bytes"] == catalog.stats()["indexed_bytes"]
    assert planner.inventory_totals["protected_bytes"] + planner.inventory_totals["candidate_bytes"] == planner.inventory_totals["indexed_bytes"]


def test_hardlinked_preview_not_counted_as_released(catalog, tmp_path):
    root = tmp_path / "data"
    _, preview, _ = qhy_project(root)
    os.link(preview, root / "other-link.png")
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    assert by_path(plan)["one.png"]["protected"]
    assert not plan["estimated_release_bytes"]


def test_stale_dependency_document_protects_without_reusing_old_links(catalog, tmp_path):
    root = tmp_path / "data"
    _, preview, _ = qhy_project(root)
    dependency = document(root / "saved.astroproj", {"schemaVersion": 1, "name": "empty"})
    planner = scan(catalog, root)
    dependency.write_text("{partially written", encoding="utf-8")
    plan = planner.preview(["run:one"], "permanent")
    assert by_path(plan)["one.png"]["protected"]
    assert preview.exists()


def test_new_unindexed_project_document_invalidates_preview(catalog, tmp_path):
    root = tmp_path / "data"
    _, preview, _ = qhy_project(root)
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    assert planner.revalidate(plan)["valid"]
    document(root / "just-created" / "dependency.astroproj", {"name": "new", "dataRoot": str(preview.parent)})
    validation = planner.revalidate(plan)
    assert not validation["valid"]
    fresh = planner.preview(["run:one"], "permanent")
    item = by_path(fresh)["one.png"]
    assert item["protected"] and "unindexed_metadata_requires_rescan" in item["reasons"]


def test_same_size_mtime_dependency_change_is_detected_by_content(catalog, tmp_path):
    root = tmp_path / "data"
    qhy_project(root)
    project = document(root / "saved.astroproj", {"schemaVersion": 1, "name": "one"})
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    info = project.stat()
    project.write_text(project.read_text().replace("one", "two"), encoding="utf-8")
    os.utime(project, ns=(info.st_atime_ns, info.st_mtime_ns))
    assert not planner.revalidate(plan)["valid"]


def test_snapshot_terminal_without_final_record_does_not_allow_cleanup(catalog, tmp_path):
    root = tmp_path / "data"
    qhy_project(root)
    manifest = root / "one" / "manifest.json"
    payload = json.loads(manifest.read_text())
    payload.pop("terminalState")
    document(manifest, payload)
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    item = by_path(plan)["one.png"]
    assert item["protected"]
    assert "observation_run_has_no_explicit_terminal_record" in item["reasons"]


def test_per_item_requires_fresh_full_validation_and_catches_catalog_change(catalog, tmp_path):
    root = tmp_path / "data"
    qhy_project(root)
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    item = next(item for item in plan["items"] if item["selected"])
    assert not planner.revalidate_item(plan, item)["valid"]
    assert planner.revalidate(plan)["valid"]
    assert planner.revalidate_item(plan, item)["valid"]
    catalog.set_review(item["id"], "pick")
    assert not planner.revalidate_item(plan, item)["valid"]


def test_legacy_toml_glob_scope_is_protected(catalog, tmp_path):
    root = tmp_path / "data"
    _, preview, _ = qhy_project(root)
    (root / "legacy.toml").write_text(f'data_root = "{preview.parent.as_posix()}"\nscience_glob = "*.png"\n', encoding="utf-8")
    planner = scan(catalog, root)
    plan = planner.preview(["run:one"], "permanent")
    assert by_path(plan)["one.png"]["protected"]
