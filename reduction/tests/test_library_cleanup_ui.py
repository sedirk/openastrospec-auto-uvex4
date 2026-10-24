"""Cleanup UI uses isolated catalogs and mocked executors, never real cleanup."""

from __future__ import annotations

from copy import deepcopy
import inspect
from pathlib import Path
import tkinter as tk
from types import SimpleNamespace

import pytest

from uvex_reduce import library_cleanup_ui as ui
from uvex_reduce.library_theme import apply_library_theme
from uvex_reduce.library_ui import LibraryApp


@pytest.fixture
def cleanup(tmp_path, monkeypatch, tk_root):
    parent = tk.Toplevel(tk_root)
    parent.withdraw()
    apply_library_theme(parent)
    calls = []
    items = [
        {"id": 1, "path": str(tmp_path / "preview.png"), "size_bytes": 4096,
         "candidate": True, "protected": False, "estimated_release_bytes": 4096,
         "category": "preview", "reasons": ["可再生预览"], "project_ids": ["run:a"]},
        {"id": 2, "path": str(tmp_path / "guide.fits"), "size_bytes": 8192,
         "candidate": True, "protected": False, "estimated_release_bytes": 8192,
         "category": "guide", "reasons": ["保留期外诊断"], "project_ids": ["run:a", "run:b"]},
        {"id": 3, "path": str(tmp_path / "science.fits"), "size_bytes": 16384,
         "candidate": False, "protected": True, "estimated_release_bytes": 0,
         "category": "science", "reasons": ["科学原片", "后期工程依赖"], "project_ids": ["run:a"]},
    ]
    projects = [{"project_id": "run:a", "target": "HD 18552", "indexed_bytes": 28672,
                 "candidate_bytes": 12288, "protected_bytes": 16384, "shared_bytes": 8192,
                 "file_count": 3, "candidate_count": 2, "protected_count": 1}]

    class Planner:
        def __init__(self, _catalog):
            pass

        def inventory(self):
            calls.append(("inventory",))
            return deepcopy(projects)

        def preview(self, project_ids, *, action, file_ids=None):
            calls.append(("preview", tuple(project_ids), action, file_ids))
            rows = deepcopy(items)
            for row in rows:
                row["selected"] = (row["id"] in file_ids if file_ids is not None
                                   else action != "mark" and row["candidate"])
            selected = [row for row in rows if row["selected"]]
            return {"project_ids": list(project_ids), "action": action, "items": rows,
                    "plan_id": "plan-123", "digest": "abcdef1234567890" * 4,
                    "selected_bytes": sum(row["size_bytes"] for row in selected),
                    "protected_bytes": 16384,
                    "estimated_release_bytes": sum(row["estimated_release_bytes"] for row in selected)
                    if action == "permanent" else 0}

    class Executor:
        def __init__(self, _catalog, _planner):
            pass

        def execute(self, plan, phrase, **kwargs):
            calls.append(("execute", plan, phrase))
            return {"action": plan["action"], "status": "completed", "results": [
                {"id": row["id"], "path": row["path"], "status": "recycled", "message": "模拟执行"}
                for row in plan["items"] if row["selected"]], "logical_removed_bytes": plan["selected_bytes"],
                "estimated_release_bytes": 0, "audit_path": str(tmp_path / "receipt.json")}

    monkeypatch.setattr(ui, "CleanupPlanner", Planner)
    monkeypatch.setattr(ui, "CleanupExecutor", Executor)
    monkeypatch.setattr(ui, "confirmation_phrase", lambda plan:
                        f"{plan['action']} {sum(row['selected'] for row in plan['items'])} {plan['digest'][:12]}")
    app = SimpleNamespace(root=parent, database=tmp_path / "isolated.sqlite3", cached_stats={},
                          refresh=lambda: calls.append(("refresh",)),
                          projects=SimpleNamespace(refresh=lambda: calls.append(("project-refresh",))),
                          reveal_path=lambda path: calls.append(("reveal", path)) or "定位请求已发送")
    app._combo = LibraryApp._combo.__get__(app)
    app._submit = lambda work, done: done(work())
    window = ui.CleanupWindow(app)
    window.test_calls, window.test_items, window.test_projects = calls, items, projects
    yield window
    if window.winfo_exists():
        if window.confirmation and window.confirmation.winfo_exists():
            window.confirmation.destroy()
        window.destroy()
    parent.destroy()


def preview(window):
    window.projects.selection_set("run:a")
    window.preview_projects()


def test_open_only_reads_inventory_and_default_recycle_does_not_free_space(cleanup):
    assert cleanup.action.get() == "recycle"
    assert cleanup.plan is None
    assert cleanup.test_calls == [("inventory",)]
    assert "0 B" in cleanup.mode_note.get()
    assert cleanup.confirm_button.instate(("disabled",))
    assert "不会清空回收站" in cleanup.mode_note.get()
    assert "已完成" in cleanup.summary.get()
    assert "正在统计" not in cleanup.summary.get()


def test_preview_protects_raw_and_dependency_and_counts_shared_file_once(cleanup):
    preview(cleanup)
    assert cleanup.selected_ids == {1, 2}
    assert "预计立即释放 0 B" in cleanup.summary.get()
    cleanup.toggle_file(3)
    assert cleanup.selected_ids == {1, 2}, "Protected rows cannot be physically selected"
    assert cleanup.files.set("3", "check") == "—"
    assert not any(call[0] == "execute" for call in cleanup.test_calls)
    assert ui.selected_summary(cleanup.plan, cleanup.selected_ids, "recycle") == (2, 12288, 0)


def test_exact_subset_has_separate_one_button_confirmation_and_consumed_plan_cannot_repeat(cleanup):
    preview(cleanup)
    cleanup.toggle_file(1)
    cleanup.request_confirmation()
    confirmation = cleanup.confirmation
    assert cleanup.test_calls[-1] == ("preview", ("run:a",), "recycle", [2])
    assert len(confirmation.items) == 1
    assert confirmation.items[0]["id"] == 2
    assert confirmation.confirm_button.instate(("!disabled",))
    assert confirmation.confirm_button.cget("text") == "确认移入回收站"
    assert not hasattr(confirmation, "input")
    assert not hasattr(confirmation, "phrase_entry")
    assert not any(call[0] == "execute" for call in cleanup.test_calls)
    confirmation.confirm()
    confirmation.confirm()  # A queued second click cannot execute twice.
    executed = [call for call in cleanup.test_calls if call[0] == "execute"]
    assert len(executed) == 1
    assert executed[0][2] == ui.confirmation_phrase(executed[0][1])
    assert cleanup.receipt["status"] == "completed"
    assert cleanup.consumed
    assert cleanup.confirm_button.instate(("disabled",))
    cleanup.execute_confirmed(executed[0][1], executed[0][2])
    assert len([call for call in cleanup.test_calls if call[0] == "execute"]) == 1
    cleanup.files.selection_set("2")
    cleanup.select_file()
    assert "最近操作" in cleanup.file_note.get()
    assert "模拟执行" in cleanup.file_note.get()


def test_cancel_confirmation_preserves_files_and_permanent_has_correct_estimate(cleanup):
    cleanup.action.set("permanent")
    cleanup.change_mode()
    preview(cleanup)
    assert "预计立即释放 12.0 KiB" in cleanup.summary.get()
    assert "无法从系统回收站还原" in cleanup.mode_note.get()
    cleanup.request_confirmation()
    assert cleanup.confirmation.plan["action"] == "permanent"
    assert cleanup.confirmation.confirm_button.cget("text") == "确认永久删除"
    cleanup.confirmation.destroy()
    assert not any(call[0] == "execute" for call in cleanup.test_calls)
    assert cleanup.selected_ids == {1, 2}


def test_mark_is_explicit_and_can_label_protected_data_without_physically_deleting(cleanup):
    cleanup.action.set("mark")
    cleanup.change_mode()
    preview(cleanup)
    assert cleanup.selected_ids == set()
    assert cleanup.confirm_button.instate(("disabled",))
    cleanup.toggle_file(3)
    assert cleanup.selected_ids == {3}
    assert "预计立即释放 0 B" in cleanup.summary.get()
    assert "不授予后续删除权限" in cleanup.mode_note.get()
    cleanup.action.set("recycle")
    cleanup.change_mode()
    assert cleanup.plan is None
    assert cleanup.selected_ids == set()
    assert cleanup.confirm_button.instate(("disabled",))


def test_stale_preview_cannot_restore_old_mode_or_open_confirmation(cleanup):
    pending = []
    cleanup.app._submit = lambda work, done: pending.append((work, done))
    preview(cleanup)
    cleanup.action.set("permanent")
    cleanup.change_mode()
    work, done = pending.pop()
    done(work())
    assert cleanup.plan is None
    assert cleanup.confirmation is None
    assert cleanup.action.get() == "permanent"
    assert cleanup.offset == 0
    assert cleanup.file_paging[2].cget("text").startswith("0–0 / 0")
    assert cleanup.file_paging[0].instate(("disabled",))
    assert cleanup.file_paging[1].instate(("disabled",))


def test_cleanup_worker_closures_own_only_plain_data_not_tk_windows(cleanup):
    pending = []
    cleanup.app._submit = lambda work, done: pending.append((work, done))
    cleanup.refresh_inventory()
    safe_work, _done = pending.pop()
    work = inspect.getclosurevars(safe_work).nonlocals["work"]
    assert set(inspect.getclosurevars(work).nonlocals) == {"database"}
    cleanup.busy = False
    preview(cleanup)
    safe_work, _done = pending.pop()
    work = inspect.getclosurevars(safe_work).nonlocals["work"]
    assert set(inspect.getclosurevars(work).nonlocals) == {"database", "action", "project_ids"}


def test_source_path_and_protection_reason_are_visible_and_explorer_is_direct(cleanup):
    preview(cleanup)
    cleanup.files.selection_set("3")
    cleanup.select_file()
    assert cleanup.path.get() == cleanup.test_items[2]["path"]
    assert "后期工程依赖" in cleanup.file_note.get()
    assert str(cleanup.path_entry.cget("state")) == "readonly"
    cleanup.reveal()
    assert cleanup.test_calls[-1] == ("reveal", cleanup.test_items[2]["path"])


def test_protection_change_before_confirmation_requires_review_again(cleanup):
    preview(cleanup)
    for row in cleanup.test_items:
        if row["id"] == 2:
            row.update(candidate=False, protected=True, reasons=["reduction_or_legacy_input_dependency"])
    cleanup.request_confirmation()
    assert cleanup.confirmation is None
    assert cleanup.selected_ids == {1}
    assert "发生变化" in cleanup.status.get()
    assert not any(call[0] == "execute" for call in cleanup.test_calls)


def test_close_during_execution_requests_cancel_and_keeps_receipt_window_alive(cleanup):
    preview(cleanup)
    pending = []
    cleanup.app._submit = lambda work, done: pending.append((work, done))
    cleanup.execute_confirmed(cleanup.plan, ui.confirmation_phrase(cleanup.plan))
    assert cleanup.executing
    assert cleanup.cancel_button.instate(("!disabled",))
    cleanup.close()
    assert cleanup.cancel_execution.is_set()
    assert cleanup.winfo_exists()
    work, done = pending.pop()
    done(work())
    assert not cleanup.executing
    assert cleanup.receipt is not None


def test_executor_error_does_not_claim_no_files_changed_or_reuse_plan(cleanup, monkeypatch):
    preview(cleanup)

    class UncertainExecutor:
        def __init__(self, *_args):
            pass

        def execute(self, *_args, **_kwargs):
            raise OSError("audit write error after operation")

    monkeypatch.setattr(ui, "CleanupExecutor", UncertainExecutor)
    cleanup.execute_confirmed(cleanup.plan, ui.confirmation_phrase(cleanup.plan))
    assert "未完整确认" in cleanup.status.get()
    assert "未执行文件操作" not in cleanup.status.get()
    assert cleanup.consumed
    assert cleanup.confirm_button.instate(("disabled",))


def test_no_candidates_disables_confirmation_and_large_inventory_is_paginated(cleanup):
    for row in cleanup.test_items:
        row.update(candidate=False, protected=True, reasons=["保护"])
    preview(cleanup)
    assert not cleanup.selected_ids
    assert cleanup.confirm_button.instate(("disabled",))
    template = cleanup.test_projects[0]
    cleanup.inventory = [{**template, "project_id": f"run:{index}", "indexed_bytes": index}
                         for index in range(40001)]
    cleanup.render_projects()
    assert len(cleanup.projects.get_children()) == 100
    assert "40,001" in cleanup.project_paging[2].cget("text")
    cleanup.project_page(1)
    assert cleanup.project_offset == 100
    assert len(cleanup.projects.get_children()) == 100


def test_file_selection_is_not_limited_to_visible_page(cleanup):
    preview(cleanup)
    template = cleanup.plan["items"][0]
    cleanup.plan["items"] = [{**template, "id": index, "path": str(Path(template["path"]).with_name(f"{index}.png"))}
                              for index in range(1000)]
    cleanup.select_candidates()
    assert len(cleanup.files.get_children()) == 100
    assert len(cleanup.selected_ids) == 1000
    assert "1,000 个唯一文件" in cleanup.summary.get()
    cleanup.file_page(1)
    cleanup.toggle_file(101)
    assert len(cleanup.selected_ids) == 999
    assert 101 not in cleanup.selected_ids


@pytest.mark.parametrize("scale", [1.0, 1.25])
def test_cleanup_controls_fit_small_window_at_display_scaling(cleanup, scale):
    original = float(cleanup.tk.call("tk", "scaling"))
    try:
        cleanup.tk.call("tk", "scaling", 96 / 72 * scale)
        cleanup.master.deiconify()
        cleanup.geometry("1100x720")
        cleanup.deiconify()
        preview(cleanup)
        cleanup.update_idletasks()
        cleanup.update()
        for widget in (cleanup.mode_combo, cleanup.files, cleanup.path_entry,
                       cleanup.reveal_button, cleanup.confirm_button, cleanup.status_label):
            assert widget.winfo_ismapped(), str(widget)
            assert widget.winfo_rootx() >= cleanup.winfo_rootx() - 2
            assert widget.winfo_rooty() >= cleanup.winfo_rooty() - 2
            assert widget.winfo_rootx() + widget.winfo_width() <= cleanup.winfo_rootx() + cleanup.winfo_width() + 2
            assert widget.winfo_rooty() + widget.winfo_height() <= cleanup.winfo_rooty() + cleanup.winfo_height() + 2
        assert cleanup.files.winfo_height() >= 180
    finally:
        cleanup.tk.call("tk", "scaling", original)


def test_one_button_confirmation_fits_125_percent_without_technical_input(cleanup):
    original = float(cleanup.tk.call("tk", "scaling"))
    try:
        cleanup.tk.call("tk", "scaling", 96 / 72 * 1.25)
        cleanup.master.deiconify()
        cleanup.deiconify()
        preview(cleanup)
        cleanup.request_confirmation()
        dialog = cleanup.confirmation
        dialog.update_idletasks()
        dialog.update()
        for widget in (dialog.files, dialog.cancel_button, dialog.confirm_button):
            assert widget.winfo_ismapped()
            assert widget.winfo_rootx() >= dialog.winfo_rootx() - 2
            assert widget.winfo_rooty() >= dialog.winfo_rooty() - 2
            assert widget.winfo_rootx() + widget.winfo_width() <= dialog.winfo_rootx() + dialog.winfo_width() + 2
            assert widget.winfo_rooty() + widget.winfo_height() <= dialog.winfo_rooty() + dialog.winfo_height() + 2
        assert not hasattr(dialog, "input")
        assert not hasattr(dialog, "phrase_entry")
        assert dialog.confirm_button.instate(("!disabled",))
    finally:
        cleanup.tk.call("tk", "scaling", original)
