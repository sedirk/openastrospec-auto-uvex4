"""Project UI contracts with synthetic files; never connect equipment or process data."""

from __future__ import annotations

from concurrent.futures import Future
from copy import deepcopy
import hashlib
from pathlib import Path
import tkinter as tk

from PIL import Image
import pytest

from uvex_reduce import library_projects_ui, library_ui
from uvex_reduce.library_catalog import LibraryCatalog
from uvex_reduce.library_preview import LibraryPreview
from uvex_reduce.library_visual_fixture import build_fixture


def test_project_labels_distinguish_repeated_runs_and_historical_reductions():
    assert library_projects_ui.project_label({"target": "HD 18552", "created_utc":
        "2026-10-21T18:28:13Z"}) == "HD 18552 · 02:28"
    assert library_projects_ui.project_label({"target": "Regulus", "id": "reduction:12",
        "manifest_path": "C:/history/2026-02-21-regulus/Regulus_run.json"}) == "Regulus · 2026-02-21-regulus"
    assert library_projects_ui.project_label({"target": "Unknown", "created_utc": None}) == "Unknown"


def synchronous_submit(_self, work, done):
    future = Future()
    value = work()
    future.set_result(value)
    done(value)
    return future


@pytest.fixture
def app(tmp_path, monkeypatch, tk_root):
    database = build_fixture(tmp_path)
    with LibraryCatalog(database) as catalog:
        all_frames = catalog.list_files(kind="frame")
    frames = [row for row in all_frames if row["target"] == "HD 18552"]
    details = {
        "project-a": {
            "id": "project-a", "target": "HD 18552", "run_id": "VISUAL-ONLY-01",
            "source": "real", "night": "2026-10-24", "outcome": "completed",
            "outcome_label": "完成", "reduction_label": "未处理", "science_frames": 3,
            "accepted_frames": 2, "probe_frames": 0, "exposure_seconds": 360,
            "source_paths": [str(tmp_path / "synthetic-observations/visual-run-01/manifest.json")],
            "timeline": [{"stage": "Science", "code": "SYNTHETIC_ACCEPTED"}],
            "files": frames,
        },
        "project-b": {
            "id": "project-b", "target": "HD 21455", "run_id": "VISUAL-ONLY-02",
            "source": "real", "night": "2026-10-24", "outcome": "failed",
            "outcome_label": "失败", "reduction_label": "未处理", "science_frames": 0,
            "accepted_frames": 0, "files": [], "source_paths": [],
            "reason": "SYNTHETIC_NO_SPECTRUM",
        },
    }

    class FakeProjects:
        def __init__(self, _catalog):
            pass

        def list_projects(self, *, offset=0, limit=150, query="", source="", outcome=""):
            rows = [value for value in details.values()
                    if (not source or value["source"] == source)
                    and (not outcome or value["outcome"] == outcome)
                    and (not query or query in value["target"])]
            return deepcopy(rows[offset:offset + limit])

        def count_projects(self, **filters):
            return len(self.list_projects(**filters))

        def get_project(self, project_id):
            return deepcopy(details.get(project_id))

        def refresh_registered_projects(self):
            return None

        def select_science(self, project_id):
            return deepcopy([row for row in details[project_id]["files"]
                             if row.get("accepted") == 1 and row.get("review") != "reject"])

    monkeypatch.setattr(library_projects_ui, "LibraryProjects", FakeProjects)
    monkeypatch.setattr(library_ui.LibraryApp, "_submit", synchronous_submit)
    monkeypatch.setattr(library_ui, "suggested_roots", lambda: [])
    for name in ("showerror", "showwarning", "showinfo"):
        monkeypatch.setattr(library_ui.messagebox, name, lambda *args, **kwargs: None)
    monkeypatch.setattr(library_ui.messagebox, "askyesno", lambda *args, **kwargs: False)
    root = tk.Toplevel(tk_root)
    root.withdraw()
    instance = library_ui.LibraryApp(root, database)
    instance.test_details = details
    instance.test_frames = all_frames
    yield instance
    if not instance.closed:
        instance.close()


def select_project(app, project="project-a"):
    app.projects.table.selection_set(project)
    app.projects.select_project()
    return app.projects


def test_default_workspace_is_project_first_and_not_a_quality_claim(app):
    assert app.workspace.select() == str(app.projects)
    assert app.projects.source.get() == "real"
    assert set(app.projects.rows) == {"project-a", "project-b"}
    browser = select_project(app)
    assert "2 / 3" in browser.metrics.get()
    assert "不是光谱质量结论" in browser.log_text.get("1.0", "end")
    assert "人工“不采用”不删除、不回收" in browser.notice.get()
    assert browser.path.get()
    assert "原位存在" in browser.presence.get()
    assert "无资料库物理清理回执" in browser.presence.get()
    assert str(browser.path_entry.cget("state")) == "readonly"


def test_mark_is_only_catalogue_metadata_and_does_not_change_raw_files(app):
    browser = select_project(app)
    originals = {row["path"]: hashlib.sha256(Path(row["path"]).read_bytes()).hexdigest()
                 for row in app.test_frames}
    row = browser.current_member()
    app.basket[row["path"]] = {"path": row["path"], "role": "science"}
    browser.mark_member()
    with LibraryCatalog(app.database) as catalog:
        marked = catalog.get_file(row["id"])
        assert marked["review"] == "reject"
        assert marked["accepted"] == row["accepted"], "Operator review cannot rewrite quality"
    assert row["path"] not in app.basket
    assert "仅标记" in browser.notice.get()
    assert "未删除、未进入回收站、未移动" in browser.notice.get()
    assert "释放 0 B" in browser.notice.get()
    browser.mark_member()
    with LibraryCatalog(app.database) as catalog:
        assert catalog.get_file(row["id"])["review"] == "unreviewed"
    assert originals == {path: hashlib.sha256(Path(path).read_bytes()).hexdigest()
                         for path in originals}


def test_reveal_and_copy_keep_exact_source_path_visible(app, monkeypatch):
    browser = select_project(app)
    source = browser.current_member()["path"]
    calls = []
    monkeypatch.setattr(app, "reveal_path", lambda path: calls.append(path) or "定位请求已发送")
    browser.reveal()
    assert calls == [source]
    assert browser.path.get() == source
    assert browser.notice.get() == "定位请求已发送"
    monkeypatch.setattr(browser, "clipboard_clear", lambda: calls.append("clear"))
    monkeypatch.setattr(browser, "clipboard_append", lambda path: calls.append(path))
    browser.copy_path()
    assert calls[-2:] == ["clear", source]
    assert "完整来源路径" in browser.notice.get()


def test_old_preview_callback_cannot_replace_new_selected_member(app, monkeypatch):
    browser = select_project(app)
    pending = []
    monkeypatch.setattr(app, "_submit", lambda work, done: pending.append((work, done)))
    keys = list(browser.members)
    for key in keys[:2]:
        browser.files.selection_set(key)
        browser.select_member()
    old_image = Image.new("RGB", (13, 11), "red")
    new_image = Image.new("RGB", (17, 15), "green")
    pending[-1][1]((LibraryPreview(image=new_image, caption="new"), None))
    pending[-2][1]((LibraryPreview(image=old_image, caption="stale"), None))
    assert browser.viewer._image.size == (17, 15)
    assert browser.path.get() == browser.members[keys[1]]["path"]


def test_old_project_detail_cannot_reappear_after_clear(app, monkeypatch):
    browser = app.projects
    pending = []
    monkeypatch.setattr(app, "_submit", lambda work, done: pending.append((work, done)))
    browser.table.selection_set("project-a")
    browser.select_project()
    browser.clear()
    pending[-1][1](deepcopy(app.test_details["project-a"]))
    assert browser.detail is None
    assert browser.path.get() == ""
    assert not browser.members
    assert browser.handoff_button.instate(("disabled",))


def test_failed_project_without_frames_keeps_failure_record_and_disables_handoff(app):
    browser = select_project(app, "project-b")
    assert browser.detail["outcome"] == "failed"
    assert "SYNTHETIC_NO_SPECTRUM" in browser.log_text.get("1.0", "end")
    assert browser.handoff_button.instate(("disabled",))
    assert browser.mark_button.instate(("disabled",))
    assert browser.path.get() == ""


def test_wrong_channel_cannot_be_handed_to_spectral_reduction(app, monkeypatch):
    browser = select_project(app)
    photo = deepcopy(next(row for row in app.test_frames if row["channel"] == "photometry"))
    photo["accepted"] = 1
    app.test_details["project-a"]["files"] = [photo]
    prior = {"saved.fit": {"path": "saved.fit", "role": "science"}}
    app.basket = deepcopy(prior)
    monkeypatch.setattr(library_ui.messagebox, "askyesno", lambda *args, **kwargs: True)
    monkeypatch.setattr(app, "export", lambda: pytest.fail("Wrong channel must never export"))
    browser.handoff()
    assert app.basket == prior


def test_handoff_revalidates_catalogue_rejection_before_export(app, monkeypatch):
    browser = select_project(app)
    row = next(row for row in browser.members.values() if row.get("accepted") == 1)
    with LibraryCatalog(app.database) as catalog:
        catalog.set_review(row["id"], decision="reject")
    monkeypatch.setattr(app, "export", lambda: pytest.fail("Stale rejected selection must not export"))
    browser.handoff()
    assert not app.basket


def test_open_existing_project_uses_studio_without_running_processing(app, tmp_path, monkeypatch):
    from uvex_reduce.project import AstroProject

    source = tmp_path / "chosen project.astroproj"
    calls = []
    monkeypatch.setattr(AstroProject, "load", lambda path: calls.append(("validate", path)))
    monkeypatch.setattr(library_projects_ui.subprocess, "Popen",
                        lambda args, **kwargs: calls.append(("open", args, kwargs)))
    app.projects.open_project(source)
    assert calls[0] == ("validate", source)
    assert calls[1][1][-2:] == ["--project", str(source)]
    assert "uvex_reduce.gui" in calls[1][1]
    assert "--run" not in calls[1][1]
    assert calls[1][2] == {"shell": False}


def test_member_categories_do_not_mix_guide_and_science_channels():
    assert library_projects_ui.member_matches({"kind": "frame", "channel": "spectroscopy"}, "spectrum")
    for channel in ("photometry", "slit", "unknown"):
        row = {"kind": "frame", "channel": channel}
        assert not library_projects_ui.member_matches(row, "spectrum")
    assert library_projects_ui.acceptance_label({"accepted": None}) == "未作质量结论"
    assert library_projects_ui.acceptance_label({"accepted": 0}) == "采集未通过"
    assert library_projects_ui.acceptance_label({"accepted": 1}) == "采集合格"
