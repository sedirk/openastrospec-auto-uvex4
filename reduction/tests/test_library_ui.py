from __future__ import annotations

from concurrent.futures import Future
import gc
import json
from pathlib import Path
import threading
import tkinter as tk
from tkinter import ttk
import weakref

from astropy.io import fits
import numpy as np
from PIL import Image
import pytest

from uvex_reduce import library_ui
from uvex_reduce.library_catalog import LibraryCatalog, SOURCES
from uvex_reduce.library_preview import LibraryPreview
from uvex_reduce.library_selection import LibrarySelection
from uvex_reduce.project import AstroProject


def make_fits(path: Path, target="Target A", camera="ATR585M", run="run-a") -> Path:
    hdu = fits.PrimaryHDU(np.arange(30 * 40, dtype=np.float32).reshape(30, 40))
    hdu.header["OBJECT"] = target
    hdu.header["INSTRUME"] = camera
    hdu.header["UVEXSTG"] = "science"
    hdu.header["OBSRUNID"] = run
    hdu.writeto(path)
    return path


@pytest.fixture
def app(tmp_path, monkeypatch, tk_root):
    directory = tmp_path / "observations"
    directory.mkdir()
    for label in ("a", "b"):
        run_directory = directory / label
        run_directory.mkdir()
        target, run = f"Target {label.upper()}", f"run-{label}"
        make_fits(run_directory / f"{label}.fit", target, run=run)
        manifest = {
            "observationRunId": run,
            "plan": {"target": {"name": target}, "expectedAtrCameraId": "SIM-ATR"},
            "terminalState": "Completed",
        }
        (run_directory / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    database = tmp_path / "library.sqlite3"
    with LibraryCatalog(database) as catalog:
        catalog.add_root(directory)
        catalog.scan()
    monkeypatch.setattr(library_ui, "suggested_roots", lambda: [])
    # Dialogs must never block a test or interact with existing applications.
    for name in ("showerror", "showwarning", "showinfo"):
        monkeypatch.setattr(library_ui.messagebox, name, lambda *args, **kwargs: None)
    monkeypatch.setattr(library_ui.messagebox, "askyesno", lambda *args, **kwargs: False)
    root = tk.Toplevel(tk_root)
    root.withdraw()
    instance = library_ui.LibraryApp(root, database)
    yield instance
    if not instance.closed:
        instance.close()


def fits_rows(app):
    return [row for row in app.records.values() if Path(row["path"]).suffix == ".fit"]


def select_rows(app, rows):
    app.table.selection_set([str(row["id"]) for row in rows])


def prevent_preview_workers(app, monkeypatch):
    pending = []

    def submit(work, done):
        pending.append((work, done))
        return Future()

    monkeypatch.setattr(app, "_submit", submit)
    return pending


def test_withdrawn_tk_smoke_does_not_scan_or_connect_anything(app, monkeypatch):
    assert app.root.state() == "withdrawn"
    assert len(app.records) == 2
    monkeypatch.setattr(
        LibraryCatalog, "scan", lambda *args, **kwargs: pytest.fail("Refresh must not scan sources"),
    )
    app.refresh()
    assert app.root.state() == "withdrawn"
    assert len(app.table.get_children()) == 2


def test_source_filter_values_match_catalog_contract(app):
    source = app.filter_widgets["source"]
    assert isinstance(source, ttk.Combobox)
    assert set(library_ui.CHOICES["source"]) - {""} == SOURCES
    assert tuple(source.cget("values")) == tuple(library_ui.CHOICES["source"].values())
    app.filters["source"].set("simulated")
    app.filter_changed()
    assert source.get() == "模拟 / 测试"
    assert len(app.records) == 2
    assert all(row["source"] == "simulated" for row in app.records.values())


def test_selected_metadata_displays_runs_contract(app, monkeypatch):
    prevent_preview_workers(app, monkeypatch)
    select_rows(app, fits_rows(app)[:1])
    app.select()
    detail = json.loads(app.logs.get("1.0", "end-1c"))
    assert detail["runs"]
    assert detail["runs"][0]["id"] in {"run-a", "run-b"}


@pytest.mark.parametrize("channel", ["photometry", "slit", "g3", "qhy"])
def test_photometry_or_guide_frame_cannot_be_reinterpreted_as_spectrum(tmp_path, channel):
    path = make_fits(tmp_path / "frame.fit")
    with pytest.raises(ValueError, match="测光|盯缝"):
        library_ui.reduction_frame(
            {"path": str(path), "channel": channel, "target": "Target"}, "science",
        )


def test_existing_1d_product_cannot_enter_raw_spectrum_basket(tmp_path):
    path = tmp_path / "product.fits"
    fits.PrimaryHDU(np.arange(30)).writeto(path)
    with pytest.raises(ValueError):
        library_ui.reduction_frame(
            {"path": str(path), "channel": "spectroscopy", "target": "Target"}, "science",
        )


def test_rejected_missing_or_unnamed_files_do_not_enter_basket(tmp_path):
    path = make_fits(tmp_path / "science.fit")
    record = {"path": str(path), "channel": "spectroscopy", "target": "Target"}
    with pytest.raises(ValueError, match="弃片"):
        library_ui.reduction_frame({**record, "review": "reject"}, "science")
    with pytest.raises(ValueError, match="缺失"):
        library_ui.reduction_frame({**record, "missing": True}, "science")
    with pytest.raises(ValueError, match="目标名"):
        library_ui.reduction_frame({**record, "target": ""}, "science")


def test_corrupt_fits_add_basket_shows_feedback_instead_of_tk_traceback(app, monkeypatch):
    row = fits_rows(app)[0]
    path = Path(row["path"])
    path.write_bytes(b"Truncated FITS fixture")
    select_rows(app, [row])
    warnings = []
    monkeypatch.setattr(
        library_ui.messagebox, "showwarning",
        lambda *args, **kwargs: warnings.append(args),
    )
    app.add_basket()
    assert not app.basket
    assert warnings and warnings[0][0] == "选片篮"
    assert path.read_bytes() == b"Truncated FITS fixture"


def test_multi_target_selection_preserves_each_target_identity(app, monkeypatch):
    prevent_preview_workers(app, monkeypatch)
    rows = fits_rows(app)
    select_rows(app, rows)
    app.select()
    app.add_basket()
    assert {frame["target"] for frame in app.basket.values()} == {"Target A", "Target B"}


def test_explicit_multi_target_override_does_not_silently_merge_targets(app, monkeypatch):
    prevent_preview_workers(app, monkeypatch)
    select_rows(app, fits_rows(app))
    app.target_name.set("Wrong shared target")
    app.add_basket()
    assert not app.basket


def test_unknown_channel_needs_explicit_operator_confirmation(app, monkeypatch):
    row = fits_rows(app)[0]
    row["channel"] = "unknown"
    with LibraryCatalog(app.database) as catalog:
        catalog.connection.execute("UPDATE files SET channel='unknown' WHERE id=?", (row["id"],))
        catalog.connection.commit()
    select_rows(app, [row])
    app.add_basket()
    assert not app.basket
    monkeypatch.setattr(library_ui.messagebox, "askyesno", lambda *args, **kwargs: True)
    app.add_basket()
    assert len(app.basket) == 1


def test_annotation_snapshot_never_follows_later_table_selection(app):
    first, second = fits_rows(app)
    select_rows(app, [second])
    app.notes.set("different inspector draft")
    app.rating.set("1")
    app.annotate(rows=[first], rating="4", notes="original dialog draft")
    with LibraryCatalog(app.database) as catalog:
        assert catalog.get_file(first["id"])["notes"] == "original dialog draft"
        assert catalog.get_file(first["id"])["rating"] == 4
        assert catalog.get_file(second["id"])["notes"] == ""


def test_basket_snapshot_keeps_recipients_and_rechecks_new_rejection(app):
    first, second = fits_rows(app)
    select_rows(app, [second])
    assert app.add_basket(rows=[first], role="science", target_name="")
    assert list(app.basket) == [first["path"]]
    app.basket.clear()
    with LibraryCatalog(app.database) as catalog:
        catalog.set_review(first["id"], decision="reject")
    assert not app.add_basket(rows=[first], role="science", target_name="")
    assert not app.basket


def test_stale_preview_completion_never_replaces_new_selection(app, monkeypatch):
    pending = prevent_preview_workers(app, monkeypatch)
    images = []
    monkeypatch.setattr(app.viewer, "set_image", lambda image, **kwargs: images.append(kwargs["source"]))
    first, second = fits_rows(app)
    select_rows(app, [first])
    app.select()
    select_rows(app, [second])
    app.select()
    pending[0][1]((LibraryPreview(image=Image.new("RGB", (4, 4)), caption="stale"), None))
    assert images == []
    pending[1][1]((LibraryPreview(image=Image.new("RGB", (4, 4)), caption="current"), None))
    assert images == ["current"]


def test_filtering_away_selection_clears_identity_actions_and_stale_preview(app, monkeypatch):
    pending = prevent_preview_workers(app, monkeypatch)
    select_rows(app, fits_rows(app)[:1])
    app.select()
    assert app.selected()
    app.target_name.set("An explicit override from the old selection")
    app.filters["source"].set("unknown")
    app.filter_changed()
    assert not app.records
    assert not app.selected()
    assert app.target_name.get() == ""
    assert app.notes.get() == ""
    assert app.selection_title.get() == "文件预览"
    assert app.details.get("1.0", "end-1c") == ""
    assert app.logs.get("1.0", "end-1c") == ""
    assert all(button.instate(["disabled"]) for button in app.row_actions)
    pending[0][1]((LibraryPreview(image=Image.new("RGB", (4, 4)), caption="stale"), None))
    assert app.viewer._image is None


def test_worker_results_are_applied_only_in_tk_thread(app):
    main_thread = threading.get_ident()
    done = []
    future = app._submit(threading.get_ident, lambda worker: done.append((worker, threading.get_ident())))
    assert future.result(timeout=3) != main_thread
    # Manually pump only this independent window; no existing process is touched.
    app._pump()
    assert done and done[0][0] != main_thread and done[0][1] == main_thread


def test_completed_future_does_not_retain_tk_callback_owner_in_worker_closure(app):
    """Old completion closures retained Tk variables until a worker-triggered GC."""
    main_thread = threading.get_ident()
    finalized = []

    class CallbackOwner:
        def __init__(self):
            self.variable = tk.StringVar(app.root, value="owned on UI thread")

        def done(self, value):
            self.variable.set(str(value))

    owner = CallbackOwner()
    reference = weakref.ref(owner)
    finalizer = weakref.finalize(owner, lambda: finalized.append(threading.get_ident()))
    future = app._submit(lambda: 42, owner.done)
    queued = threading.Event()
    future.add_done_callback(lambda _future: queued.set())
    del owner
    future.result(timeout=3)
    assert queued.wait(3), "Completion must be queued before pumping the UI"
    app._pump()
    gc.collect()
    assert reference() is None, "A completed Future must not retain a Tk callback"
    assert not finalizer.alive
    assert finalized == [main_thread]
    # Keep Future alive while forcing a collection from an actual worker. Its
    # callback contains only the plain event queue and numeric task identity.
    assert app.pool.submit(gc.collect).result(timeout=3) >= 0


def test_close_releases_pending_ui_callbacks_before_worker_completion(app):
    main_thread = threading.get_ident()
    finalized = []
    release = threading.Event()

    class CallbackOwner:
        def __init__(self):
            self.variable = tk.StringVar(app.root, value="pending UI callback")

        def done(self, _value):
            pytest.fail("Closed UI callback must never execute")

    owner = CallbackOwner()
    reference = weakref.ref(owner)
    finalizer = weakref.finalize(owner, lambda: finalized.append(threading.get_ident()))
    future = app._submit(lambda: release.wait(3), owner.done)
    del owner
    try:
        app.close()
        gc.collect()
        assert reference() is None
        assert not finalizer.alive
        assert finalized == [main_thread]
    finally:
        release.set()
        if not future.cancelled():
            future.result(timeout=4)


def test_scan_runs_in_worker_and_finishes_on_owned_tk_window(app, monkeypatch):
    main_thread = threading.get_ident()
    work_threads = []
    futures = []
    submit = app._submit

    def fake_scan(_catalog, **kwargs):
        work_threads.append(threading.get_ident())
        kwargs["progress"]({"files_seen": 2})
        return {"files_seen": 2, "cancelled": False}

    def capture_submit(work, done):
        future = submit(work, done)
        futures.append(future)
        return future

    monkeypatch.setattr(LibraryCatalog, "scan", fake_scan)
    monkeypatch.setattr(app, "_submit", capture_submit)
    app.scan()
    assert app.scanning
    futures[0].result(timeout=3)
    app._pump()
    assert work_threads and work_threads[0] != main_thread
    assert not app.scanning
    assert "索引结果" in app.status.get()


def test_marking_and_lifecycle_preview_leave_originals_unchanged(app):
    rows = fits_rows(app)
    originals = {row["path"]: Path(row["path"]).read_bytes() for row in rows}
    select_rows(app, rows)
    app.mark("reject")
    select_rows(app, fits_rows(app))
    app.lifecycle()
    plan = json.loads(app.logs.get("1.0", "end-1c"))
    assert plan["bytes_released"] == 0 and plan["dry_run"] is True
    assert all(item["status"] == "needs_review" for item in plan["items"])
    assert originals == {path: Path(path).read_bytes() for path in originals}
    with LibraryCatalog(app.database) as catalog:
        assert all(row["review"] == "reject" for row in catalog.list_files(kind="frame"))


def test_ui_export_hashes_exact_selection_and_only_opens_project(app, tmp_path, monkeypatch):
    row = fits_rows(app)[0]
    select_rows(app, [row])
    app.add_basket()
    project_path = tmp_path / "selected.astroproj"
    monkeypatch.setattr(library_ui.filedialog, "asksaveasfilename", lambda **kwargs: str(project_path))
    launched = []
    monkeypatch.setattr(library_ui.subprocess, "Popen", lambda args, **kwargs: launched.append(args))
    monkeypatch.setattr(app, "_submit", lambda work, done: done(work()))
    original = Path(row["path"]).read_bytes()

    app.export()

    project = AstroProject.load(project_path)
    selection = LibrarySelection.from_parameters(project.parameters)
    assert len(selection.frames) == 1
    assert Path(selection.frames[0].path) == Path(row["path"])
    selection.validate_files()
    assert Path(row["path"]).read_bytes() == original
    assert len(launched) == 1
    assert launched[0][1:] == ["-m", "uvex_reduce.gui", "--project", str(project_path)]
