"""Destructive tests use only pytest-created tiny synthetic files."""

from __future__ import annotations

from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import uuid

import pytest

from uvex_reduce.library_catalog import LibraryCatalog
from uvex_reduce.library_cleanup_execute import (
    CleanupExecutionError,
    CleanupExecutor,
    WindowsCleanupBackend,
    _safe_file,
    confirmation_phrase,
    latest_file_receipt,
)


class FixturePlanner:
    """Test double; production requires a registry-issued CleanupPlanner plan."""

    def __init__(self):
        self.plans = {}
        self.valid = True
        self.per_item = []
        self.on_check = None

    def issue(self, catalog, paths, action="permanent"):
        items = []
        for path in paths:
            path = Path(os.path.normcase(os.path.abspath(path)))
            info = path.lstat()
            file_id = catalog.connection.execute(
                "SELECT id FROM files WHERE path=?", (str(path),)).fetchone()[0]
            items.append({"id": file_id, "path": str(path), "size_bytes": info.st_size,
                          "mtime_ns": info.st_mtime_ns, "dev": info.st_dev, "ino": info.st_ino,
                          "nlink": info.st_nlink, "selected": True, "protected": False,
                          "candidate": True, "estimated_release_bytes": info.st_size})
        plan = {"plan_id": str(uuid.uuid4()), "action": action, "items": items,
                "catalog_path": str(catalog.db_path)}
        plan["digest"] = hashlib.sha256(json.dumps(plan, sort_keys=True).encode()).hexdigest()
        self.plans[plan["plan_id"]] = deepcopy(plan)
        return plan

    def revalidate(self, plan, **kwargs):
        return {"valid": self.valid and plan == self.plans.get(plan["plan_id"]),
                "reasons": [] if self.valid else ["dependency changed"]}

    def revalidate_item(self, plan, item, completed_file_ids=()):
        self.per_item.append((item["id"], completed_file_ids))
        if self.on_check:
            self.on_check(item)
        return self.revalidate(plan)


class SyntheticBackend:
    """Temp-file permanent operation; no recycle-bin imitation in production."""

    def __init__(self):
        self.calls = []
        self.before = None

    def permanent(self, item, verify):
        self.calls.append(item["id"])
        if self.before:
            self.before(item)
        verify()
        Path(item["path"]).unlink()
        return {"status": "deleted", "message": "synthetic deleted"}

    def recycle(self, item, verify):
        self.calls.append(item["id"])
        verify()
        raise OSError("Synthetic recycle unavailable; no fallback")


@pytest.fixture
def setup(tmp_path):
    root = tmp_path / "synthetic-data"
    root.mkdir()
    paths = [root / f"disposable-{number}.png" for number in range(3)]
    for path in paths:
        path.write_bytes(b"synthetic-only\n")
    with LibraryCatalog(tmp_path / "catalog.sqlite3") as catalog:
        catalog.add_root(root)
        catalog.scan()
        planner = FixturePlanner()
        backend = SyntheticBackend()
        executor = CleanupExecutor(catalog, planner, audit_dir=tmp_path / "audit", _backend=backend)
        yield catalog, planner, backend, executor, paths


def test_confirmation_binds_action_count_and_full_digest(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths[:1])
    for phrase in ("", "yes", confirmation_phrase(plan).replace("永久删除", "移入系统回收站"),
                   confirmation_phrase(plan).replace("1 文件", "2 文件"),
                   confirmation_phrase(plan)[:-1]):
        with pytest.raises(CleanupExecutionError, match="确认语句"):
            executor.execute(plan, phrase)
    assert not backend.calls
    assert all(path.exists() for path in paths)
    assert not executor.audit_dir.exists()


def test_unissued_or_tampered_plan_never_executes(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths[:1])
    plan["items"][0]["protected"] = True
    with pytest.raises(CleanupExecutionError, match="预览已失效"):
        executor.execute(plan, confirmation_phrase(plan))
    assert not backend.calls


def test_exact_two_files_receipts_and_progress(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths[:2])
    calls = []
    receipt = executor.execute(plan, confirmation_phrase(plan), progress=lambda *args: calls.append(args))
    assert receipt["status"] == "completed"
    assert len(receipt["results"]) == 2
    assert receipt["logical_removed_bytes"] == sum(item["size_bytes"] for item in plan["items"])
    assert receipt["estimated_release_bytes"] == receipt["logical_removed_bytes"]
    assert paths[2].exists()
    assert not paths[0].exists() and not paths[1].exists()
    assert [(row[0], row[1]) for row in calls] == [(1, 2), (2, 2)]
    records = [json.loads(line) for line in Path(receipt["audit_path"]).read_text("utf-8").splitlines()]
    assert [row["event"] for row in records] == ["plan", "intent", "result", "intent", "result", "finished"]
    assert records[0]["plan"] == plan
    saved = latest_file_receipt(catalog, paths[0], audit_dir=executor.audit_dir)
    assert saved["status"] == "deleted" and saved["plan_id"] == plan["plan_id"]
    assert catalog.connection.execute("SELECT SUM(missing) FROM files").fetchone()[0] == 2


def test_mark_only_preserves_originals_rating_notes_and_zero_space(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths[:2], "mark")
    catalog.set_review(plan["items"][0]["id"], "pick", 4, "keep this operator note")
    # A missing original may still receive a catalog-only operator decision.
    paths[0].unlink()
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "completed"
    assert receipt["estimated_release_bytes"] == receipt["logical_removed_bytes"] == 0
    assert not backend.calls
    assert paths[1].read_bytes() == b"synthetic-only\n"
    row = catalog.connection.execute("SELECT decision,rating,notes FROM reviews WHERE file_id=?",
                                     (plan["items"][0]["id"],)).fetchone()
    assert tuple(row) == ("reject", 4, "keep this operator note")
    assert latest_file_receipt(catalog, paths[1], audit_dir=executor.audit_dir)["status"] == "marked"


def test_recycle_unavailable_has_no_permanent_fallback(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths[:2], "recycle")
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "failed"
    assert len(backend.calls) == 1
    assert receipt["estimated_release_bytes"] == 0
    assert all(path.exists() for path in paths)


def test_dependency_change_after_preview_stops_before_mutation(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    planner.valid = False
    with pytest.raises(CleanupExecutionError, match="dependency changed"):
        executor.execute(plan, confirmation_phrase(plan))
    assert not backend.calls


def test_dependency_changed_inside_native_boundary_blocks_delete(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    backend.before = lambda item: setattr(planner, "valid", False)
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "failed"
    assert all(path.exists() for path in paths)
    assert len(backend.calls) == 1


def test_replaced_file_not_deleted_even_when_same_size_and_timestamp(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    original = paths[0].stat()
    # Keep the old inode alive at a different name so Windows cannot reuse it.
    paths[0].rename(paths[0].with_suffix(".old"))
    paths[0].write_bytes(b"different-data")
    os.utime(paths[0], ns=(original.st_atime_ns, original.st_mtime_ns))
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "failed"
    assert not backend.calls
    assert paths[0].read_bytes() == b"different-data"


def test_partial_failure_stops_without_expanding_or_retrying(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    count = 0

    def fail_second(item):
        nonlocal count
        count += 1
        if count == 2:
            raise OSError("busy")

    backend.before = fail_second
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "partial"
    assert [row["status"] for row in receipt["results"]] == ["deleted", "failed"]
    assert paths[1].exists() and paths[2].exists()
    paths[0].write_bytes(b"replacement must survive old retry")
    with pytest.raises(CleanupExecutionError, match="已有执行"):
        executor.execute(plan, confirmation_phrase(plan))
    assert paths[0].read_bytes() == b"replacement must survive old retry"


def test_cancellation_before_first_and_after_one_file(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    receipt = executor.execute(plan, confirmation_phrase(plan), cancelled=lambda: True)
    assert receipt["status"] == "cancelled" and not receipt["results"]
    assert not backend.calls
    plan = planner.issue(catalog, paths)
    receipt = executor.execute(plan, confirmation_phrase(plan), cancelled=lambda: not paths[0].exists())
    assert receipt["status"] == "cancelled"
    assert len(receipt["results"]) == 1
    assert paths[1].exists() and paths[2].exists()


def test_cancellation_at_native_boundary_keeps_file_and_explicit_receipt(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    state = {"cancelled": False}
    backend.before = lambda item: state.update(cancelled=True)
    receipt = executor.execute(plan, confirmation_phrase(plan), cancelled=lambda: state["cancelled"])
    assert receipt["status"] == "cancelled"
    assert receipt["results"][0]["status"] == "cancelled"
    assert all(path.exists() for path in paths)


def test_audit_cannot_live_within_observation_roots(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    executor.audit_dir = paths[0].parent / "audit"
    with pytest.raises(CleanupExecutionError, match="数据目录之外"):
        executor.execute(plan, confirmation_phrase(plan))
    assert not backend.calls


def test_hardlinks_and_directory_cannot_be_deleted(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    os.link(paths[0], paths[0].with_suffix(".hardlink"))
    with pytest.raises(CleanupExecutionError, match="硬链接"):
        _safe_file(plan["items"][0])
    directory_item = dict(plan["items"][1], path=str(paths[0].parent))
    with pytest.raises(CleanupExecutionError, match="普通文件"):
        _safe_file(directory_item)


def test_audit_intent_failure_prevents_delete(setup, monkeypatch):
    import uvex_reduce.library_cleanup_execute as module
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    original = module._Journal.append

    def fail_intent(self, value):
        if value["event"] == "intent":
            raise OSError("disk full")
        return original(self, value)

    monkeypatch.setattr(module._Journal, "append", fail_intent)
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "failed"
    assert not backend.calls
    assert all(path.exists() for path in paths)


def test_audit_result_failure_keeps_known_removed_outcome_and_stops(setup, monkeypatch):
    import uvex_reduce.library_cleanup_execute as module
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)
    original = module._Journal.append

    def fail_result(self, value):
        if value["event"] == "result":
            raise OSError("disk full after mutation")
        return original(self, value)

    monkeypatch.setattr(module._Journal, "append", fail_result)
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "partial"
    assert receipt["results"][0]["status"] == "deleted"
    assert receipt["logical_removed_bytes"] == plan["items"][0]["size_bytes"]
    assert not paths[0].exists() and paths[1].exists()
    assert len(backend.calls) == 1


def test_receipt_index_failure_does_not_claim_file_unchanged(setup, monkeypatch):
    import uvex_reduce.library_cleanup_execute as module
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)

    original = module._record_receipt

    def fail(directory, result):
        if result["status"] == "deleted":
            raise sqlite3.OperationalError("index unavailable")
        return original(directory, result)

    monkeypatch.setattr(module, "_record_receipt", fail)
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "partial"
    assert receipt["results"][0]["status"] == "deleted"
    assert not paths[0].exists() and paths[1].exists()
    assert "result" in Path(receipt["audit_path"]).read_text("utf-8")
    assert latest_file_receipt(catalog, paths[0], audit_dir=executor.audit_dir)["status"] == "deleted"


def test_intent_receipt_must_persist_before_physical_operation(setup, monkeypatch):
    import uvex_reduce.library_cleanup_execute as module
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths)

    def full(*args):
        raise OSError("No room for durable receipt index")

    monkeypatch.setattr(module, "_record_receipt", full)
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "failed"
    assert not backend.calls
    assert all(path.exists() for path in paths)


def test_confirmed_recycle_has_zero_expected_immediate_release(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths[:1], "recycle")

    class SyntheticBin:
        def recycle(self, item, verify):
            verify()
            # Test adapter only; production uses native Windows IFileOperation.
            path = Path(item["path"])
            path.rename(path.parent.parent / "synthetic-bin-item")
            return {"status": "recycled", "message": "synthetic adapter", "recycle_path": "fixture"}

    executor.backend = SyntheticBin()
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "completed"
    assert receipt["logical_removed_bytes"] == plan["items"][0]["size_bytes"]
    assert receipt["estimated_release_bytes"] == 0
    assert latest_file_receipt(catalog, paths[0], audit_dir=executor.audit_dir)["status"] == "recycled"


def test_watch_exempts_only_the_exact_selected_paths(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths[:2])
    seen = {}

    class ExactWatch:
        def __init__(self, roots, *, expected_preview_paths):
            seen.update(roots=roots, paths=expected_preview_paths, closed=False)

        def __enter__(self):
            return self

        def check(self):
            pass

        def __exit__(self, *args):
            seen["closed"] = True

    executor.watcher = ExactWatch
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "completed"
    assert seen["paths"] == [item["path"] for item in plan["items"]]
    assert os.path.normcase(str(paths[2])) not in seen["paths"]
    assert seen["closed"]


def test_uncertain_bin_result_stops_and_does_not_claim_released_bytes(setup):
    catalog, planner, backend, executor, paths = setup
    plan = planner.issue(catalog, paths, "recycle")

    class UnknownBin:
        def recycle(self, item, verify):
            verify()
            Path(item["path"]).unlink()
            return {"status": "uncertain", "message": "no Shell bin item confirmation"}

    executor.backend = UnknownBin()
    receipt = executor.execute(plan, confirmation_phrase(plan))
    assert receipt["status"] == "failed"
    assert receipt["results"][0]["status"] == "uncertain"
    assert receipt["estimated_release_bytes"] == 0
    assert paths[1].exists()


def test_default_receipt_lookup_is_read_only_when_no_receipts(setup):
    catalog, planner, backend, executor, paths = setup
    assert latest_file_receipt(catalog, paths[0]) is None
    assert not (catalog.db_path.parent / "cleanup-audit").exists()


def _native_item(path):
    info = path.stat()
    return {"path": str(path), "size_bytes": info.st_size, "mtime_ns": info.st_mtime_ns,
            "dev": info.st_dev, "ino": info.st_ino, "nlink": info.st_nlink}


@pytest.mark.skipif(os.name != "nt", reason="Windows handle deletion")
def test_native_permanent_uses_exact_handle_on_synthetic_file(tmp_path):
    disposable = tmp_path / "synthetic-native-permanent.txt"
    disposable.write_text("Only a pytest fixture, not observation data.", encoding="utf-8")
    item = _native_item(disposable)
    result = WindowsCleanupBackend().permanent(item, lambda: _safe_file(item))
    assert result["status"] == "deleted"
    assert not disposable.exists()


@pytest.mark.skipif(os.name != "nt", reason="Windows locked-handle writer exclusion")
def test_native_permanent_excludes_concurrent_writer_and_does_not_delete(tmp_path):
    disposable = tmp_path / "synthetic-exclude-writer.txt"
    disposable.write_bytes(b"synthetic protected during handle check")
    item = _native_item(disposable)

    def incompatible_writer():
        disposable.write_bytes(b"must not succeed")

    with pytest.raises(PermissionError):
        WindowsCleanupBackend().permanent(item, incompatible_writer)
    assert disposable.read_bytes() == b"synthetic protected during handle check"


@pytest.mark.skipif(os.name != "nt", reason="Windows system recycle bin")
def test_native_recycle_reports_real_system_bin_item_for_tiny_fixture(tmp_path):
    # Deliberately do not empty the recycle bin or delete anything else within it.
    disposable = tmp_path / f"UVEX-pytest-synthetic-recycle-{uuid.uuid4()}.txt"
    disposable.write_text("Tiny synthetic recycle API test; no real data.", encoding="utf-8")
    item = _native_item(disposable)
    result = WindowsCleanupBackend().recycle(item, lambda: _safe_file(item))
    assert result["status"] == "recycled"
    assert not disposable.exists()
    assert "回收站" in result["message"]


@pytest.mark.skipif(os.name != "nt", reason="Windows native production executor integration")
@pytest.mark.parametrize(("action", "status"), [("permanent", "deleted"), ("recycle", "recycled")])
def test_real_planner_two_previews_execute_next_item_and_leave_all_raws(tmp_path, action, status):
    from test_library_cleanup_plan import qhy_project
    from uvex_reduce.library_cleanup_plan import CleanupPlanner
    root = tmp_path / "synthetic-projects"
    raw1, preview1, _ = qhy_project(root, "one")
    raw2, preview2, _ = qhy_project(root, "two")
    database = tmp_path / "catalog.sqlite3"
    with LibraryCatalog(database) as catalog:
        catalog.add_root(root)
        catalog.scan()
        plan = CleanupPlanner(catalog).preview(["run:one", "run:two"], action)
        assert len([row for row in plan["items"] if row["selected"]]) == 2
    # The UI closes its worker connection. A fresh executor connection still
    # validates the process-issued plan, not a per-connection data_version.
    with LibraryCatalog(database) as catalog:
        receipt = CleanupExecutor(catalog).execute(plan, confirmation_phrase(plan))
        assert receipt["status"] == "completed", receipt
        assert [row["status"] for row in receipt["results"]] == [status, status]
        assert catalog.connection.execute("SELECT SUM(missing) FROM files").fetchone()[0] == 2
        assert latest_file_receipt(catalog, preview2)["status"] == status
    assert raw1.exists() and raw2.exists()
    assert not preview1.exists() and not preview2.exists()
